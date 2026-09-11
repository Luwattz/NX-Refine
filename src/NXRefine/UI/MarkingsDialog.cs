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
    internal sealed class MarkingsDialog : IDisposable
    {
        private readonly NxContext context;
        private readonly BlockDialog dialog;
        private SelectObject carrierSelect;
        private FaceCollector keptSelect;
        private DoubleBlock maxHeight;
        private NXOpen.BlockStyler.Label status;
        private Body body;
        private Face carrier;
        private readonly List<Face[]> groups = new List<Face[]>();
        private bool updating;
        private bool ready;

        public MarkingsDialog(NxContext context)
        {
            this.context = context;
            context.RequireWorkPart();
            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "NXRefine.Markings.dlx");
            dialog = context.UI.CreateDialog(path);
            dialog.AddInitializeHandler(Initialize);
            dialog.AddUpdateHandler(Update);
            dialog.AddApplyHandler(Apply);
            dialog.AddOkHandler(Apply);
            dialog.AddCancelHandler(Cancel);
        }

        public void ShowDialog()
        {
            try { dialog.Launch(); }
            finally
            {
                // A native Block Styler dialog can be closed from its title-bar
                // close button without entering the OK/Apply callbacks.  Clear
                // NX's global selection list here as a final safety net.  Do not
                // touch native block handles after Launch returns; NX has already
                // released them at that point.
                try { context.UI.SelectionManager.ClearGlobalSelectionList(); } catch (Exception ex) { context.Log("Remove Markings selection cleanup: " + ex); }
                ready = false;
            }
        }

        private void Initialize()
        {
            carrierSelect = (SelectObject)dialog.TopBlock.FindBlock("carrier");
            keptSelect = (FaceCollector)dialog.TopBlock.FindBlock("faces");
            maxHeight = (DoubleBlock)dialog.TopBlock.FindBlock("maxHeight");
            status = (NXOpen.BlockStyler.Label)dialog.TopBlock.FindBlock("status");
            var mask = new Selection.MaskTriple(UFConstants.UF_solid_type, 0, UFConstants.UF_UI_SEL_FEATURE_ANY_FACE);
            carrierSelect.SetSelectionFilter(Selection.SelectionAction.ClearAndEnableSpecific, new[] { mask });
            carrierSelect.MaximumScopeAsString = "Within Work Part Only";
            keptSelect.EntityType = 16; // Faces
            keptSelect.MaximumScopeAsString = "Within Work Part Only";
            // Keep the native NX selection-intent menu on the deletion collector
            // focused on the same rule used by the scanner: one click represents
            // all faces belonging to a boss or pocket (including a connected
            // character), rather than a single face at a time.
            keptSelect.FaceRules = 2048; // Boss and Pocket Faces
            keptSelect.DefaultFaceRulesAsString = "Boss and Pocket Faces";
            keptSelect.PopupMenuEnabled = true;
            // NX can retain a value by the block's previous layout position
            // while a dialog is reloaded in the same session.  Set the new,
            // single height control explicitly so it cannot inherit the old
            // 20-unit group-diagonal value.
            maxHeight.Value = 2.0;
            ready = true;
            status.Label = "Select the lettering carrier face (its body is the search scope). Connected boss and pocket faces within the height limits will be highlighted.";
        }

        private int Update(UIBlock block)
        {
            if (updating) return 0;
            try
            {
                updating = true;
                string blockName = block == null ? string.Empty : block.Name;
                // Block Styler may supply a fresh managed wrapper for an
                // update event, so compare the stable block id rather than
                // relying on managed object reference equality.  In
                // particular, changing Max feature height must rescan rather
                // than merely repainting the previous candidate list.
                if (blockName == "carrier" || blockName == "maxHeight" || blockName == "find")
                {
                    ClearPreview();
                    keptSelect.SetSelectedObjects(new TaggedObject[0]);
                    groups.Clear();
                    Face[] selected = carrierSelect.GetSelectedObjects().OfType<Face>().ToArray();
                    if (selected.Length != 1) { status.Label = "Select exactly one carrier face."; return 0; }
                    carrier = selected[0];
                    body = carrier.GetBody();
                    if (carrier.IsOccurrence || !body.IsSolidBody)
                        throw new InvalidOperationException("Select a solid-body face in the work part.");
                    Scan();
                    keptSelect.SetSelectedObjects(groups.SelectMany(g => g).Cast<TaggedObject>().ToArray());
                }
                else if (blockName == "faces")
                {
                    // Removing one face from the native collector excludes its complete connected group.
                    var kept = new HashSet<Tag>(keptSelect.GetSelectedObjects().Select(o => o.Tag));
                    keptSelect.SetSelectedObjects(groups.Where(g => g.All(f => kept.Contains(f.Tag)))
                        .SelectMany(g => g).Cast<TaggedObject>().ToArray());
                }
                Preview();
                status.Label = groups.Count + " connected boss/pocket groups; " + keptSelect.GetSelectedObjects().Length +
                    " faces retained. Maximum height " + maxHeight.Value.ToString("0.###") +
                    ". Review bosses and holes before Apply.";
                return 0;
            }
            catch (Exception ex) { return Error(ex); }
            finally { updating = false; }
        }

        private int Cancel()
        {
            // Native selection collectors must be emptied while the Block Styler
            // dialog is still active. Doing this after Launch returns makes NX
            // attempt to free an already-released collector object.
            updating = true;
            try
            {
                ClearPreview();
                if (keptSelect != null) keptSelect.SetSelectedObjects(new TaggedObject[0]);
                if (carrierSelect != null) carrierSelect.SetSelectedObjects(new TaggedObject[0]);
                context.UI.SelectionManager.ClearGlobalSelectionList();
                groups.Clear();
                carrier = null;
                body = null;
                ready = false;
            }
            catch (Exception ex)
            {
                context.Log("Remove Markings cancel cleanup: " + ex);
            }
            finally { updating = false; }
            return 0;
        }

        private void Scan()
        {
            if (maxHeight.Value <= 0 || double.IsNaN(maxHeight.Value) || double.IsInfinity(maxHeight.Value))
                throw new InvalidOperationException("Maximum feature height must be positive.");
            var faces = body.GetFaces().Where(f => f.Tag != carrier.Tag).ToDictionary(f => f.Tag);
            var adjacency = faces.Keys.ToDictionary(tag => tag, tag => new HashSet<Tag>());
            var boundary = new HashSet<Tag>();
            foreach (Edge edge in body.GetEdges())
            {
                Face[] touching = edge.GetFaces();
                bool atCarrier = touching.Any(f => f.Tag == carrier.Tag);
                foreach (Face face in touching.Where(f => faces.ContainsKey(f.Tag)))
                {
                    if (atCarrier) boundary.Add(face.Tag);
                    foreach (Face other in touching.Where(f => faces.ContainsKey(f.Tag) && f.Tag != face.Tag))
                        adjacency[face.Tag].Add(other.Tag);
                }
            }
            var seen = new HashSet<Tag>();
            foreach (Tag seed in boundary)
            {
                if (seen.Contains(seed)) continue;
                Face[] component = ResolveBossPocketFaces(faces[seed], faces);
                // Imported or damaged geometry may not be recognized by NX's
                // boss/pocket intent rule. Preserve the topology-only fallback
                // so the command still finds connected marking islands.
                if (component == null || component.Length < 2)
                    component = ConnectedComponent(seed, faces, adjacency);
                if (component.Length == 0 || component.Any(face => seen.Contains(face.Tag)))
                    continue;
                foreach (Face face in component) seen.Add(face.Tag);
                double height = FeatureHeight(component);
                if (height <= maxHeight.Value && component.Length < faces.Count)
                    groups.Add(component);
            }
        }

        private Face[] ResolveBossPocketFaces(Face seed, IDictionary<Tag, Face> bodyFaces)
        {
            ScCollector collector = null;
            try
            {
                collector = context.WorkPart.ScCollectors.CreateCollector();
                SelectionIntentRule rule = context.WorkPart.ScRuleFactory.CreateRuleFaceBossPocket(seed);
                collector.ReplaceRules(new[] { rule }, false);
                return (collector.GetObjects() ?? new DisplayableObject[0]).OfType<Face>()
                    .Where(face => bodyFaces.ContainsKey(face.Tag))
                    .GroupBy(face => face.Tag).Select(group => group.First()).ToArray();
            }
            catch (NXException)
            {
                return null;
            }
            finally
            {
                if (collector != null)
                    try { collector.Destroy(); } catch (NXException) { }
            }
        }

        private Face[] ConnectedComponent(Tag seed, IDictionary<Tag, Face> faces, IDictionary<Tag, HashSet<Tag>> adjacency)
        {
            var component = new List<Face>();
            var queue = new Queue<Tag>();
            var visited = new HashSet<Tag>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                Tag tag = queue.Dequeue();
                if (!visited.Add(tag) || !faces.ContainsKey(tag)) continue;
                component.Add(faces[tag]);
                foreach (Tag next in adjacency[tag])
                    if (!visited.Contains(next)) queue.Enqueue(next);
            }
            return component.ToArray();
        }

        private double FeatureHeight(IEnumerable<Face> faces)
        {
            // The selected carrier supplies the reference normal. Projecting
            // each candidate's WCS bounding box onto that normal gives a stable
            // boss/cavity height for planar and mildly curved carrier faces,
            // without depending on a particular modeling feature history.
            double[] point = new double[3];
            double[] normal = new double[3];
            double[] carrierBox = new double[6];
            int type;
            double radius;
            double radialData;
            int normDirection;
            context.UF.Modl.AskFaceData(carrier.Tag, out type, point, normal, carrierBox,
                out radius, out radialData, out normDirection);
            double normalLength = Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]);
            if (normalLength < 1e-9)
            {
                normal[0] = 0;
                normal[1] = 0;
                normal[2] = 1;
                normalLength = 1;
            }
            for (int i = 0; i < 3; i++) normal[i] /= normalLength;
            double carrierProjection = Dot(point, normal);
            double low = double.MaxValue;
            double high = double.MinValue;
            foreach (Face face in faces)
            {
                var box = new double[6];
                context.UF.Modl.AskBoundingBox(face.Tag, box);
                double center = 0;
                double radiusOnNormal = 0;
                for (int i = 0; i < 3; i++)
                {
                    center += normal[i] * (box[i] + box[i + 3]) * 0.5;
                    radiusOnNormal += Math.Abs(normal[i]) * (box[i + 3] - box[i]) * 0.5;
                }
                low = Math.Min(low, center - radiusOnNormal);
                high = Math.Max(high, center + radiusOnNormal);
            }
            if (low == double.MaxValue || high == double.MinValue) return double.PositiveInfinity;
            return Math.Max(Math.Abs(low - carrierProjection), Math.Abs(high - carrierProjection));
        }

        private static double Dot(double[] left, double[] right)
        {
            return left[0] * right[0] + left[1] * right[1] + left[2] * right[2];
        }

        private void Preview()
        {
            ClearPreview();
            foreach (Face face in keptSelect.GetSelectedObjects().OfType<Face>())
                context.UF.Disp.SetHighlight(face.Tag, 1);
            context.WorkPart.ModelingViews.WorkView.UpdateDisplay();
        }

        private void ClearPreview()
        {
            foreach (Face face in groups.SelectMany(g => g))
                try { context.UF.Disp.SetHighlight(face.Tag, 0); } catch (NXException) { }
            context.UF.Disp.Refresh();
        }

        private void Reset()
        {
            updating = true;
            try
            {
                if (ready)
                {
                    keptSelect.SetSelectedObjects(new TaggedObject[0]);
                    carrierSelect.SetSelectedObjects(new TaggedObject[0]);
                    context.UI.SelectionManager.ClearGlobalSelectionList();
                }
                ClearPreview();
                groups.Clear();
                carrier = null;
                body = null;
            }
            finally { updating = false; }
        }

        private int Apply()
        {
            if (!ready) return 1;
            Face[] selected = keptSelect.GetSelectedObjects().OfType<Face>().ToArray();
            if (selected.Length == 0) return 1;
            var allowed = new HashSet<Tag>(groups.SelectMany(g => g).Select(f => f.Tag));
            if (selected.Any(f => !allowed.Contains(f.Tag))) return 1;
            Session.UndoMarkId mark = context.Session.SetUndoMark(Session.MarkVisibility.Visible, "NX Refine - Remove Marking Faces");
            DeleteFaceBuilder builder = null;
            try
            {
                // Release native selections and highlights while all face handles are still valid.
                Reset();
                builder = context.WorkPart.Features.CreateDeleteFaceBuilder(null);
                builder.Type = DeleteFaceBuilder.SelectTypes.Face;
                builder.Heal = true;
                builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] {
                    context.WorkPart.ScRuleFactory.CreateRuleFaceDumb(selected) }, false);
                builder.CommitFeature();
                status.Label = "Applied. Select a carrier for another pass. NX Undo reverses an applied pass.";
                context.Log("Removed " + selected.Length + " marking candidate faces.");
                return 0;
            }
            catch (Exception ex)
            {
                context.Session.UndoToMark(mark, "NX Refine - Remove Marking Faces");
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
            ClearPreview();
            context.Log(ex.ToString());
            if (status != null) status.Label = "Failed: " + ex.Message;
            return 1;
        }

        public void Dispose()
        {
            // The global selection owner is restored only after Block Styler has
            // released its dialog. Clear it both before and after Dispose so a
            // title-bar close or native Cancel cannot leave the carrier/body
            // selection painted in the modeling view.
            try { context.UI.SelectionManager.ClearGlobalSelectionList(); } catch (Exception ex) { context.Log("Remove Markings selection cleanup: " + ex); }
            try { dialog.Dispose(); }
            finally
            {
                try { context.UI.SelectionManager.ClearGlobalSelectionList(); } catch (Exception ex) { context.Log("Remove Markings selection cleanup: " + ex); }
            }
        }
    }
}
