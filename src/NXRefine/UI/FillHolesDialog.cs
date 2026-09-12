using System;
using System.Collections.Generic;
using System.Globalization;
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
    // Native Block Styler workflow for selecting target bodies and inner-hole
    // seed faces, reviewing native Boss/Pocket candidate regions, excluding
    // connected groups, and healing the exact retained faces on Apply/OK.
    internal sealed class FillHolesDialog : IDisposable
    {
        private readonly NxContext context;
        private readonly CleanupSettings settings;
        private readonly BlockDialog dialog;
        private readonly System.Windows.Forms.Timer radiusTimer;
        private readonly System.Windows.Forms.Timer previewTimer;
        private SelectObject bodySelect;
        private FaceCollector seedSelect;
        private StringBlock maxRadius;
        private FaceCollector faceSelect;
        private string radiusText;
        private readonly Dictionary<Tag, Body> bodies = new Dictionary<Tag, Body>();
        private readonly Dictionary<Tag, Face> seeds = new Dictionary<Tag, Face>();
        private readonly List<Face[]> groups = new List<Face[]>();
        private readonly HashSet<Tag> retained = new HashSet<Tag>();
        private double activeMaxRadius;
        private bool previewValid;
        private bool updating;
        private bool ready;

        public FillHolesDialog(NxContext context, CleanupSettings settings)
        {
            this.context = context;
            this.settings = settings;
            context.RequireWorkPart();
            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "NXRefine.FillHoles.dlx");
            dialog = context.UI.CreateDialog(path);
            dialog.AddInitializeHandler(Initialize);
            dialog.AddUpdateHandler(Update);
            dialog.AddApplyHandler(Apply);
            dialog.AddOkHandler(Apply);
            dialog.AddCancelHandler(Cancel);
            dialog.AddFocusNotifyHandler(FocusChanged);
            dialog.AddKeyboardFocusNotifyHandler(KeyboardFocusChanged);
            dialog.AddEnableOKButtonHandler(CanApply);
            radiusTimer = new System.Windows.Forms.Timer { Interval = 200 };
            radiusTimer.Tick += RefreshRadius;
            previewTimer = new System.Windows.Forms.Timer { Interval = 50 };
            previewTimer.Tick += RefreshPreview;
        }

        public void ShowDialog()
        {
            try { dialog.Launch(); }
            finally
            {
                radiusTimer.Stop();
                previewTimer.Stop();
                try { context.UI.SelectionManager.ClearGlobalSelectionList(); }
                catch (Exception ex) { context.Log("Fill Holes selection cleanup: " + ex); }
                ready = false;
            }
        }

        private void Initialize()
        {
            bodySelect = (SelectObject)dialog.TopBlock.FindBlock("bodies");
            bodySelect.AddFilter(SelectObject.FilterType.SolidBodies);
            bodySelect.SelectModeAsString = "Multiple";
            bodySelect.MaximumScopeAsString = "Within Work Part Only";

            seedSelect = (FaceCollector)dialog.TopBlock.FindBlock("seedFaces");
            seedSelect.EntityType = 16; // Faces
            seedSelect.MaximumScopeAsString = "Within Work Part Only";
            // Let NX perform the same Boss and Pocket Faces expansion as the
            // native Delete Face command when the user picks an inner hole
            // ring.  The preview model still applies an orientation filter so
            // an outer boss or housing wall cannot enter the delete set.
            seedSelect.FaceRules = 2049; // Single Face | Boss and Pocket Faces
            seedSelect.DefaultFaceRulesAsString = "Boss and Pocket Faces";
            seedSelect.PopupMenuEnabled = true;

            maxRadius = (StringBlock)dialog.TopBlock.FindBlock("radiusInput");
            radiusText = settings.FillHolesMaxRadius.ToString("0.########", CultureInfo.InvariantCulture);
            maxRadius.RetainValue = false;
            maxRadius.Value = radiusText;
            maxRadius.SetKeystrokeCallback(RadiusEdited);

            faceSelect = (FaceCollector)dialog.TopBlock.FindBlock("faces");
            faceSelect.EntityType = 16; // Faces
            faceSelect.MaximumScopeAsString = "Within Work Part Only";
            faceSelect.FaceRules = 1; // Single Face; connected grouping is handled by the preview model
            faceSelect.DefaultFaceRulesAsString = "Single Face";
            faceSelect.PopupMenuEnabled = true;

            activeMaxRadius = ReadMaximumRadius();
            ready = true;
        }

        private int Update(UIBlock block)
        {
            if (updating) return 0;
            try
            {
                updating = true;
                string blockName = block == null ? string.Empty : block.Name;
                if (blockName == "radiusInput") RebuildCandidates();
                else if (blockName == "bodies") UpdateBodies();
                else if (blockName == "seedFaces") UpdateSeedSelection();
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
            bodies.Clear();
            foreach (Body body in selected) bodies[body.Tag] = body;
            RebuildCandidates();
        }

        private void UpdateSeedSelection()
        {
            Face[] selected = seedSelect.GetSelectedObjects().OfType<Face>()
                .Where(face => !face.IsOccurrence && face.GetBody() != null && face.GetBody().IsSolidBody)
                .GroupBy(face => face.Tag).Select(group => group.First()).ToArray();
            var selectedTags = new HashSet<Tag>(selected.Select(face => face.Tag));
            if (selectedTags.SetEquals(seeds.Keys)) return;
            // Remove the previous native collector highlight before replacing
            // the seed set; otherwise a deselected Boss/Pocket region can stay
            // visible until the next NX repaint.
            SetHighlights(seeds.Keys.ToArray(), 0);
            seeds.Clear();
            foreach (Face face in selected) seeds[face.Tag] = face;
            RebuildCandidates();
        }

        private void UpdateFaceSelection()
        {
            var selectedTags = new HashSet<Tag>(faceSelect.GetSelectedObjects().OfType<Face>().Select(face => face.Tag));
            retained.Clear();
            // Deselecting one face excludes its entire connected candidate group.
            foreach (Face[] group in groups.Where(group => group.All(face => selectedTags.Contains(face.Tag))))
                foreach (Face face in group) retained.Add(face.Tag);
            SyncFaceCollector();
            previewValid = bodies.Count > 0 && groups.Count > 0;
        }

        private void RebuildCandidates()
        {
            radiusTimer.Stop();
            previewTimer.Stop();
            previewValid = false;
            ClearPreview();
            groups.Clear();
            retained.Clear();
            if (faceSelect != null) faceSelect.SetSelectedObjects(new TaggedObject[0]);
            activeMaxRadius = ReadMaximumRadius();
            settings.FillHolesMaxRadius = activeMaxRadius;
            settings.Save();
            if (seeds.Count == 0) return;
            // A target body limits the search when supplied.  If the user
            // only picks seed faces, infer their solid bodies so the native
            // face-selection workflow remains convenient.
            IEnumerable<Body> searchBodies = bodies.Count > 0
                ? bodies.Values
                : seeds.Values.Select(face => face.GetBody()).Where(body => body != null && body.IsSolidBody)
                    .GroupBy(body => body.Tag).Select(group => group.First());
            Face[] selectedSeeds = seeds.Values.ToArray();
            foreach (Body body in searchBodies)
                groups.AddRange(FindGroups(body, selectedSeeds, activeMaxRadius));
            foreach (Face face in groups.SelectMany(group => group)) retained.Add(face.Tag);
            SyncFaceCollector();
            previewValid = true;
        }

        private double ReadMaximumRadius()
        {
            double value;
            if ((!double.TryParse(radiusText, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
                 !double.TryParse(radiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) ||
                double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 100000.0)
                throw new InvalidOperationException("Enter a maximum hole radius in part units from 0 upward; 0 means any radius.");
            return value;
        }

        private int RadiusEdited(StringBlock block, string uncommittedValue)
        {
            if (!ready || updating || radiusText == uncommittedValue) return 0;
            radiusText = uncommittedValue;
            radiusTimer.Stop();
            previewTimer.Stop();
            previewValid = false;
            updating = true;
            try
            {
                ClearPreview();
                groups.Clear();
                retained.Clear();
                if (faceSelect != null) faceSelect.SetSelectedObjects(new TaggedObject[0]);
                radiusTimer.Start();
            }
            catch (Exception ex) { Error(ex); }
            finally { updating = false; }
            return 0;
        }

        private void RefreshRadius(object sender, EventArgs args)
        {
            radiusTimer.Stop();
            if (!ready || updating) return;
            try
            {
                if (Update(maxRadius) == 0 && previewValid)
                {
                    faceSelect.Focus();
                    Preview();
                    QueuePreviewRefresh();
                }
            }
            catch (Exception ex) { Error(ex); }
        }

        private bool CanApply()
        {
            if (!ready || updating || !previewValid || radiusTimer.Enabled || retained.Count == 0) return false;
            try { return ReadMaximumRadius() == activeMaxRadius; }
            catch (InvalidOperationException) { return false; }
        }

        private Face[][] FindGroups(Body body, Face[] selectedSeeds, double maximumRadius)
        {
            Face[] allFaces = body.GetFaces();
            var faces = allFaces.ToDictionary(face => face.Tag);
            var adjacency = faces.Keys.ToDictionary(tag => tag, tag => new HashSet<Tag>());
            foreach (Edge edge in body.GetEdges())
            {
                Face[] touching = edge.GetFaces().Where(face => faces.ContainsKey(face.Tag)).ToArray();
                for (int i = 0; i < touching.Length; i++)
                    for (int j = i + 1; j < touching.Length; j++)
                    {
                        adjacency[touching[i].Tag].Add(touching[j].Tag);
                        adjacency[touching[j].Tag].Add(touching[i].Tag);
                    }
            }
            var selectedTags = new HashSet<Tag>(selectedSeeds
                .Where(face => face.GetBody() != null && face.GetBody().Tag == body.Tag)
                .Select(face => face.Tag));
            var holeSeeds = new List<HoleSeed>();
            foreach (Face face in allFaces.Where(face => selectedTags.Contains(face.Tag)))
            {
                HoleSeed seed;
                if (TryGetInnerCylinder(face, body, out seed) &&
                    (maximumRadius == 0.0 || seed.Radius <= maximumRadius))
                    holeSeeds.Add(seed);
            }
            var result = new List<Face[]>();
            var seenCandidates = new HashSet<Tag>();
            foreach (HoleSeed seed in holeSeeds)
            {
                if (seenCandidates.Contains(seed.Face.Tag)) continue;
                // The native collector normally returns the whole Boss and
                // Pocket Faces region.  Use that exact connected selection as
                // the preferred boundary, then intersect it with the
                // cavity-facing region derived from the selected inner ring.
                // If NX returns only the seed face, the geometric fallback
                // still expands the same hole without scanning unrelated body
                // faces.
                HashSet<Tag> nativeRegion = ConnectedSelection(seed.Face.Tag, selectedTags, adjacency);
                List<Face> cavityRegion = ExpandCavityRegion(seed, faces, adjacency);
                var component = nativeRegion.Count > 1
                    ? cavityRegion.Where(face => nativeRegion.Contains(face.Tag)).ToList()
                    : cavityRegion;
                if (component.Count == 0) continue;
                // A stepped/counterbored hole is one connected region. Its
                // largest inner cylindrical radius must satisfy the limit.
                if (maximumRadius > 0.0 && component.Any(face =>
                {
                    HoleSeed inner;
                    return TryGetInnerCylinder(face, body, out inner) && inner.Radius > maximumRadius;
                })) continue;
                Face[] group = component.GroupBy(face => face.Tag).Select(faceGroup => faceGroup.First()).ToArray();
                result.Add(group);
                foreach (Face face in group) seenCandidates.Add(face.Tag);
            }
            return result.ToArray();
        }

        private HashSet<Tag> ConnectedSelection(Tag start, HashSet<Tag> selectedTags,
            Dictionary<Tag, HashSet<Tag>> adjacency)
        {
            var connected = new HashSet<Tag>();
            if (!selectedTags.Contains(start)) return connected;
            var queue = new Queue<Tag>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Tag tag = queue.Dequeue();
                if (!connected.Add(tag)) continue;
                foreach (Tag next in adjacency[tag])
                    if (selectedTags.Contains(next) && !connected.Contains(next)) queue.Enqueue(next);
            }
            return connected;
        }

        private List<Face> ExpandCavityRegion(HoleSeed seed, Dictionary<Tag, Face> faces,
            Dictionary<Tag, HashSet<Tag>> adjacency)
        {
            var component = new List<Face>();
            var seen = new HashSet<Tag>();
            var queue = new Queue<Tag>();
            queue.Enqueue(seed.Face.Tag);
            while (queue.Count > 0)
            {
                Tag tag = queue.Dequeue();
                if (!seen.Add(tag)) continue;
                component.Add(faces[tag]);
                foreach (Tag next in adjacency[tag])
                {
                    if (seen.Contains(next)) continue;
                    Face neighbor = faces[next];
                    if (IsCavityFacing(neighbor, seed.VoidPoint)) queue.Enqueue(next);
                }
            }
            return component;
        }

        private bool TryGetInnerCylinder(Face face, Body body, out HoleSeed seed)
        {
            seed = null;
            if (face.SolidFaceType != Face.FaceType.Cylindrical) return false;
            try
            {
                int type;
                int normalDirection;
                double radiusData;
                double[] axisPoint = new double[3];
                double[] axisDirection = new double[3];
                double[] box = new double[6];
                double radius;
                context.UF.Modl.AskFaceData(face.Tag, out type, axisPoint, axisDirection, box,
                    out radius, out radiusData, out normalDirection);
                if (radius <= 0.0 || double.IsNaN(radius) || double.IsInfinity(radius)) return false;
                double[] surfacePoint;
                double[] surfaceNormal;
                if (!TryGetFacePointNormal(face, out surfacePoint, out surfaceNormal)) return false;
                double towardAxis = (axisPoint[0] - surfacePoint[0]) * surfaceNormal[0] +
                    (axisPoint[1] - surfacePoint[1]) * surfaceNormal[1] +
                    (axisPoint[2] - surfacePoint[2]) * surfaceNormal[2];
                // Solid-face normals point out of the material. An inner hole
                // wall points toward its axis; an exterior cylindrical boss
                // points away from its axis and is therefore rejected.
                if (towardAxis <= 1e-7) return false;
                int containment;
                context.UF.Modl.AskPointContainment(axisPoint, body.Tag, out containment);
                // The cylinder axis point is in the void for an inner hole;
                // an exterior boss has its axis point inside the solid.
                if (containment == 1) return false;
                seed = new HoleSeed(face, axisPoint, radius);
                return true;
            }
            catch (NXException) { return false; }
        }

        private bool IsCavityFacing(Face face, double[] voidPoint)
        {
            double[] point;
            double[] normal;
            if (!TryGetFacePointNormal(face, out point, out normal)) return false;
            double towardVoid = (voidPoint[0] - point[0]) * normal[0] +
                (voidPoint[1] - point[1]) * normal[1] +
                (voidPoint[2] - point[2]) * normal[2];
            return towardVoid > 1e-7;
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

        private sealed class HoleSeed
        {
            public HoleSeed(Face face, double[] voidPoint, double radius)
            {
                Face = face;
                VoidPoint = voidPoint;
                Radius = radius;
            }

            public Face Face { get; private set; }
            public double[] VoidPoint { get; private set; }
            public double Radius { get; private set; }
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
            // The native seed collector may keep its own selection highlight
            // (including faces that the safety filter rejected).  Clear that
            // display first, then paint only the exact pending delete set.
            SetHighlights(seeds.Keys.ToArray(), 0);
            SetHighlights(excludedTags, 0);
            SetHighlights(retainedTags.ToArray(), 1);
            context.UF.Disp.Refresh();
        }

        private void ClearPreview()
        {
            SetHighlights(groups.SelectMany(group => group).Select(face => face.Tag).Distinct().ToArray(), 0);
            SetHighlights(seeds.Keys.ToArray(), 0);
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
            radiusTimer.Stop();
            previewTimer.Stop();
            previewValid = false;
            updating = true;
            try
            {
                if (bodySelect != null) bodySelect.SetSelectedObjects(new TaggedObject[0]);
                if (seedSelect != null) seedSelect.SetSelectedObjects(new TaggedObject[0]);
                if (faceSelect != null) faceSelect.SetSelectedObjects(new TaggedObject[0]);
                context.UI.SelectionManager.ClearGlobalSelectionList();
                ClearPreview();
                bodies.Clear();
                seeds.Clear();
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
            if (!ready) return 1;
            try
            {
                if (!previewValid || radiusTimer.Enabled || ReadMaximumRadius() != activeMaxRadius)
                {
                    Update(maxRadius);
                    return 1;
                }
            }
            catch (Exception ex) { return Error(ex); }
            Face[] selected = RetainedFaces();
            if (selected.Length == 0) return 1;
            Session.UndoMarkId mark = context.Session.SetUndoMark(Session.MarkVisibility.Visible, "NX Refine - Fill Holes");
            DeleteFaceBuilder builder = null;
            try
            {
                Reset();
                foreach (Face[] bodyFaces in selected.GroupBy(face => face.GetBody().Tag).Select(group => group.ToArray()))
                {
                    builder = context.WorkPart.Features.CreateDeleteFaceBuilder(null);
                    builder.Type = DeleteFaceBuilder.SelectTypes.Hole;
                    builder.Heal = true;
                    builder.UseHoleDiameter = false;
                    FaceDumbRule rule = context.WorkPart.ScRuleFactory.CreateRuleFaceDumb(bodyFaces);
                    builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                    builder.CommitFeature();
                    builder.Destroy();
                    builder = null;
                }
                context.Log("Filled " + selected.Length + " hole faces.");
                return 0;
            }
            catch (Exception ex)
            {
                context.Session.UndoToMark(mark, "NX Refine - Fill Holes");
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
            radiusTimer.Stop();
            previewTimer.Stop();
            previewValid = false;
            ClearPreview();
            context.Log(ex.ToString());
            return 1;
        }

        public void Dispose()
        {
            radiusTimer.Stop();
            radiusTimer.Dispose();
            previewTimer.Stop();
            previewTimer.Dispose();
            try { context.UI.SelectionManager.ClearGlobalSelectionList(); }
            catch (Exception ex) { context.Log("Fill Holes selection cleanup: " + ex); }
            try { dialog.Dispose(); }
            finally
            {
                try { context.UI.SelectionManager.ClearGlobalSelectionList(); }
                catch (Exception ex) { context.Log("Fill Holes selection cleanup: " + ex); }
            }
        }
    }
}
