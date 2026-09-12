using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NXOpen;
using NXOpen.BlockStyler;
using NXOpen.Features;
using NXOpen.UF;
using NXRefine.Core;
using SelectObject = NXOpen.BlockStyler.SelectObject;

namespace NXRefine.UI
{
    // Native Block Styler workflow for selecting solid bodies, finding closed
    // internal shells, reviewing their faces, and healing only the retained
    // cavity shells on Apply/OK.
    internal sealed class ClearCavitiesDialog : IDisposable
    {
        private readonly NxContext context;
        private readonly BlockDialog dialog;
        private readonly System.Windows.Forms.Timer previewTimer;
        private SelectObject bodySelect;
        private FaceCollector faceSelect;
        private readonly Dictionary<Tag, Body> bodies = new Dictionary<Tag, Body>();
        private readonly List<Face[]> groups = new List<Face[]>();
        private readonly HashSet<Tag> retained = new HashSet<Tag>();
        private bool previewValid;
        private bool updating;
        private bool ready;

        public ClearCavitiesDialog(NxContext context)
        {
            this.context = context;
            context.RequireWorkPart();
            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "NXRefine.ClearCavities.dlx");
            dialog = context.UI.CreateDialog(path);
            dialog.AddInitializeHandler(Initialize);
            dialog.AddUpdateHandler(Update);
            dialog.AddApplyHandler(Apply);
            dialog.AddOkHandler(Apply);
            dialog.AddCancelHandler(Cancel);
            dialog.AddFocusNotifyHandler(FocusChanged);
            dialog.AddKeyboardFocusNotifyHandler(KeyboardFocusChanged);
            dialog.AddEnableOKButtonHandler(CanApply);
            previewTimer = new System.Windows.Forms.Timer { Interval = 50 };
            previewTimer.Tick += RefreshPreview;
        }

        public void ShowDialog()
        {
            try { dialog.Launch(); }
            finally
            {
                previewTimer.Stop();
                try { context.UI.SelectionManager.ClearGlobalSelectionList(); }
                catch (Exception ex) { context.Log("Clear Cavities selection cleanup: " + ex); }
                ready = false;
            }
        }

        private void Initialize()
        {
            bodySelect = (SelectObject)dialog.TopBlock.FindBlock("bodies");
            bodySelect.AddFilter(SelectObject.FilterType.SolidBodies);
            bodySelect.SelectModeAsString = "Multiple";
            bodySelect.MaximumScopeAsString = "Within Work Part Only";

            faceSelect = (FaceCollector)dialog.TopBlock.FindBlock("faces");
            faceSelect.EntityType = 16; // Faces
            faceSelect.MaximumScopeAsString = "Within Work Part Only";
            faceSelect.FaceRules = 1; // Single Face; group exclusion is handled by the preview model
            faceSelect.DefaultFaceRulesAsString = "Single Face";
            faceSelect.PopupMenuEnabled = true;
            ready = true;
        }

        private int Update(UIBlock block)
        {
            if (updating) return 0;
            try
            {
                updating = true;
                string blockName = block == null ? string.Empty : block.Name;
                if (blockName == "bodies") UpdateBodies();
                else if (blockName == "faces") UpdateFaceSelection();
                Preview();
                QueuePreviewRefresh();
                return 0;
            }
            catch (Exception ex) { return Error(ex); }
            finally { updating = false; }
        }

        private void UpdateBodies()
        {
            Body[] selected = bodySelect.GetSelectedObjects().OfType<Body>()
                .Where(body => !body.IsOccurrence && body.IsSolidBody)
                .GroupBy(body => body.Tag).Select(group => group.First()).ToArray();
            var selectedTags = new HashSet<Tag>(selected.Select(body => body.Tag));
            if (selectedTags.SetEquals(bodies.Keys)) return;
            ClearPreview();
            bodies.Clear();
            foreach (Body body in selected) bodies[body.Tag] = body;
            RebuildCandidates();
        }

        private void UpdateFaceSelection()
        {
            var selectedTags = new HashSet<Tag>(faceSelect.GetSelectedObjects().OfType<Face>().Select(face => face.Tag));
            retained.Clear();
            // Deselecting any face removes the complete closed cavity group.
            foreach (Face[] group in groups.Where(group => group.All(face => selectedTags.Contains(face.Tag))))
                foreach (Face face in group) retained.Add(face.Tag);
            SyncFaceCollector();
            previewValid = groups.Count > 0;
        }

        private void RebuildCandidates()
        {
            previewTimer.Stop();
            previewValid = false;
            ClearPreview();
            groups.Clear();
            retained.Clear();
            if (faceSelect != null) faceSelect.SetSelectedObjects(new TaggedObject[0]);
            foreach (Body body in bodies.Values) groups.AddRange(FindCavityGroups(body));
            foreach (Face face in groups.SelectMany(group => group)) retained.Add(face.Tag);
            SyncFaceCollector();
            previewValid = groups.Count > 0;
        }

        private Face[][] FindCavityGroups(Body body)
        {
            Face[] allFaces = body.GetFaces();
            if (allFaces.Length < 2) return new Face[0][];
            var faces = allFaces.ToDictionary(face => face.Tag);
            var adjacency = BuildAdjacency(body, faces);
            HashSet<Tag> exterior = IdentifyExteriorFaces(body);
            var result = new List<Face[]>();
            var visited = new HashSet<Tag>();

            foreach (Face start in allFaces)
            {
                if (visited.Contains(start.Tag)) continue;
                var component = new List<Face>();
                var queue = new Queue<Tag>();
                queue.Enqueue(start.Tag);
                while (queue.Count > 0)
                {
                    Tag tag = queue.Dequeue();
                    if (!visited.Add(tag)) continue;
                    component.Add(faces[tag]);
                    foreach (Tag next in adjacency[tag])
                        if (!visited.Contains(next)) queue.Enqueue(next);
                }

                // Exterior faces identify the shell connected to the outside.
                // A cavity shell is a separate closed component with no such
                // face. The ray test below verifies that it bounds a void.
                if (component.Count == allFaces.Length || component.Any(face => exterior.Contains(face.Tag)))
                    continue;
                if (!IsClosedInternalShell(body, component)) continue;
                result.Add(component.ToArray());
            }
            context.Log("Clear Cavities: " + result.Count + " closed internal shells in body " + body.Tag);
            return result.ToArray();
        }

        private Dictionary<Tag, HashSet<Tag>> BuildAdjacency(Body body, Dictionary<Tag, Face> faces)
        {
            var adjacency = faces.Keys.ToDictionary(tag => tag, tag => new HashSet<Tag>());
            foreach (Edge edge in body.GetEdges())
            {
                Face[] touching = edge.GetFaces().Where(face => faces.ContainsKey(face.Tag))
                    .GroupBy(face => face.Tag).Select(group => group.First()).ToArray();
                for (int i = 0; i < touching.Length; i++)
                    for (int j = i + 1; j < touching.Length; j++)
                    {
                        adjacency[touching[i].Tag].Add(touching[j].Tag);
                        adjacency[touching[j].Tag].Add(touching[i].Tag);
                    }
            }
            return adjacency;
        }

        private HashSet<Tag> IdentifyExteriorFaces(Body body)
        {
            try
            {
                double tolerance = 0.001;
                try { context.UF.Modl.AskDistanceTolerance(out tolerance); }
                catch (NXException) { }
                if (tolerance <= 0.0 || double.IsNaN(tolerance) || double.IsInfinity(tolerance)) tolerance = 0.001;
                int count = 0;
                Tag[] exterior;
                int[] bodyIndex;
                context.UF.Modl.IdentifyExteriorUsingRays(
                    1, new[] { body.Tag }, new[] { Tag.Null }, new double[3], tolerance, 0,
                    ref count, out exterior, out bodyIndex);
                if (exterior == null || count <= 0) return new HashSet<Tag>();
                return new HashSet<Tag>(exterior.Take(Math.Min(count, exterior.Length)));
            }
            catch (NXException ex)
            {
                context.Log("Clear Cavities exterior-face scan failed for " + body.Tag + ": " + ex.Message);
                return new HashSet<Tag>();
            }
        }

        private bool IsClosedInternalShell(Body body, IList<Face> component)
        {
            double tolerance = 0.001;
            try { context.UF.Modl.AskDistanceTolerance(out tolerance); }
            catch (NXException) { }
            if (tolerance <= 0.0 || double.IsNaN(tolerance) || double.IsInfinity(tolerance)) tolerance = 0.001;

            foreach (Face face in component.Take(12))
            {
                double[] point;
                double[] normal;
                if (!TryGetFacePointNormal(face, out point, out normal)) continue;
                double epsilon = Math.Max(tolerance * 4.0, 1e-5);
                double[] origin = {
                    point[0] + normal[0] * epsilon,
                    point[1] + normal[1] * epsilon,
                    point[2] + normal[2] * epsilon };
                int containment;
                try { context.UF.Modl.AskPointContainment(origin, body.Tag, out containment); }
                catch (NXException) { continue; }
                if (containment == 1)
                {
                    normal[0] = -normal[0];
                    normal[1] = -normal[1];
                    normal[2] = -normal[2];
                    origin[0] = point[0] + normal[0] * epsilon;
                    origin[1] = point[1] + normal[1] * epsilon;
                    origin[2] = point[2] + normal[2] * epsilon;
                    try { context.UF.Modl.AskPointContainment(origin, body.Tag, out containment); }
                    catch (NXException) { continue; }
                }
                if (containment == 1) continue;
                if (RayHitsBody(body, origin, normal)) return true;
            }
            // An internal shell normally has at least one reliable outward
            // sample. Treat an untestable component as unknown, not a cavity.
            return false;
        }

        private bool RayHitsBody(Body body, double[] origin, double[] direction)
        {
            try
            {
                int count;
                UFModl.RayHitPointInfo[] hits;
                double[] transform = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
                context.UF.Modl.TraceARay(1, new[] { body.Tag }, origin, direction, transform, 0,
                    out count, out hits);
                return count > 0 && hits != null && hits.Any(hit => hit.hit_face != Tag.Null);
            }
            catch (NXException) { return false; }
        }

        private bool TryGetFacePointNormal(Face face, out double[] point, out double[] normal)
        {
            point = new double[3];
            normal = new double[3];
            try
            {
                double[] box = new double[6];
                context.UF.Modl.AskBoundingBox(face.Tag, box);
                double[] reference = {
                    (box[0] + box[3]) * 0.5,
                    (box[1] + box[4]) * 0.5,
                    (box[2] + box[5]) * 0.5 };
                double[] parameter = new double[2];
                context.UF.Modl.AskFaceParm(face.Tag, reference, parameter, point);
                double[] u1 = new double[3];
                double[] v1 = new double[3];
                double[] u2 = new double[3];
                double[] v2 = new double[3];
                double[] radii = new double[2];
                context.UF.Modl.AskFaceProps(face.Tag, parameter, point, u1, v1, u2, v2, normal, radii);
                double length = Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]);
                if (length < 1e-9) return false;
                normal[0] /= length;
                normal[1] /= length;
                normal[2] /= length;
                return true;
            }
            catch (NXException) { return false; }
        }

        private Face[] RetainedFaces()
        {
            return groups.SelectMany(group => group).Where(face => retained.Contains(face.Tag)).ToArray();
        }

        private void SyncFaceCollector()
        {
            if (faceSelect == null) return;
            faceSelect.SetSelectedObjects(RetainedFaces().Cast<TaggedObject>().ToArray());
        }

        private bool CanApply()
        {
            return ready && !updating && previewValid && retained.Count > 0;
        }

        private void FocusChanged(UIBlock block, bool isFocus)
        {
            if (isFocus && ready && !updating)
            {
                Preview();
                QueuePreviewRefresh();
            }
        }

        private void KeyboardFocusChanged(UIBlock block, bool isFocus)
        {
            if (isFocus && ready && !updating && previewValid)
            {
                Preview();
                QueuePreviewRefresh();
            }
        }

        private void QueuePreviewRefresh()
        {
            if (!ready) return;
            previewTimer.Stop();
            previewTimer.Start();
        }

        private void RefreshPreview(object sender, EventArgs args)
        {
            previewTimer.Stop();
            if (ready && !updating) Preview();
        }

        private void Preview()
        {
            Face[] faces = RetainedFaces();
            var retainedTags = new HashSet<Tag>(faces.Select(face => face.Tag));
            Tag[] excludedTags = groups.SelectMany(group => group).Select(face => face.Tag)
                .Where(tag => !retainedTags.Contains(tag)).Distinct().ToArray();
            SetHighlights(bodies.Keys.ToArray(), 0);
            SetHighlights(excludedTags, 0);
            SetHighlights(retainedTags.ToArray(), 1);
            context.UF.Disp.Refresh();
        }

        private void ClearPreview()
        {
            SetHighlights(groups.SelectMany(group => group).Select(face => face.Tag).Distinct().ToArray(), 0);
            SetHighlights(bodies.Keys.ToArray(), 0);
            context.UF.Disp.Refresh();
        }

        private void SetHighlights(Tag[] tags, int highlight)
        {
            if (tags.Length == 0) return;
            try { context.UF.Disp.SetHighlights(tags.Length, tags, highlight); }
            catch (NXException)
            {
                foreach (Tag tag in tags)
                    try { context.UF.Disp.SetHighlight(tag, highlight); } catch (NXException) { }
            }
        }

        private void Reset()
        {
            previewTimer.Stop();
            previewValid = false;
            updating = true;
            try
            {
                if (bodySelect != null) bodySelect.SetSelectedObjects(new TaggedObject[0]);
                if (faceSelect != null) faceSelect.SetSelectedObjects(new TaggedObject[0]);
                context.UI.SelectionManager.ClearGlobalSelectionList();
                ClearPreview();
                bodies.Clear();
                groups.Clear();
                retained.Clear();
            }
            finally { updating = false; }
        }

        private int Cancel()
        {
            Reset();
            ready = false;
            return 0;
        }

        private int Apply()
        {
            if (!ready || !previewValid || retained.Count == 0) return 1;
            Face[] selected = RetainedFaces();
            Session.UndoMarkId mark = context.Session.SetUndoMark(Session.MarkVisibility.Visible, "NX Refine - Clear Cavities");
            DeleteFaceBuilder builder = null;
            try
            {
                Reset();
                foreach (Face[] bodyFaces in selected.GroupBy(face => face.GetBody().Tag).Select(group => group.ToArray()))
                {
                    builder = context.WorkPart.Features.CreateDeleteFaceBuilder(null);
                    builder.Type = DeleteFaceBuilder.SelectTypes.Face;
                    builder.Heal = true;
                    FaceDumbRule rule = context.WorkPart.ScRuleFactory.CreateRuleFaceDumb(bodyFaces);
                    builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                    builder.CommitFeature();
                    builder.Destroy();
                    builder = null;
                }
                context.Log("Cleared " + selected.Length + " cavity faces.");
                return 0;
            }
            catch (Exception ex)
            {
                context.Session.UndoToMark(mark, "NX Refine - Clear Cavities");
                return Error(ex);
            }
            finally
            {
                if (builder != null) builder.Destroy();
                context.UF.Disp.Refresh();
            }
        }

        private int Error(Exception ex)
        {
            previewTimer.Stop();
            previewValid = false;
            ClearPreview();
            context.Log(ex.ToString());
            return 1;
        }

        public void Dispose()
        {
            previewTimer.Stop();
            previewTimer.Dispose();
            try { context.UI.SelectionManager.ClearGlobalSelectionList(); }
            catch (Exception ex) { context.Log("Clear Cavities selection cleanup: " + ex); }
            try { dialog.Dispose(); }
            finally
            {
                try { context.UI.SelectionManager.ClearGlobalSelectionList(); }
                catch (Exception ex) { context.Log("Clear Cavities selection cleanup: " + ex); }
            }
        }
    }
}
