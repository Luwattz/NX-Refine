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
        private readonly HashSet<Tag> retained = new HashSet<Tag>();
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
            dialog.AddFocusNotifyHandler(SelectionFocusChanged);
            dialog.AddKeyboardFocusNotifyHandler(KeyboardFocusChanged);
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
            // Keep the native NX selection-intent menu on the review collector:
            // one click represents all faces belonging to a boss or pocket
            // (including a connected character), rather than a single face.
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
                    retained.Clear();
                    Face[] selected = carrierSelect.GetSelectedObjects().OfType<Face>().ToArray();
                    if (selected.Length != 1) { status.Label = "Select exactly one carrier face."; return 0; }
                    carrier = selected[0];
                    body = carrier.GetBody();
                    if (carrier.IsOccurrence || !body.IsSolidBody)
                        throw new InvalidOperationException("Select a solid-body face in the work part.");
                    Scan();
                    foreach (Face face in groups.SelectMany(group => group)) retained.Add(face.Tag);
                    SyncCollector();
                }
                else if (blockName == "faces")
                {
                    // Removing one face from the native collector excludes its complete connected group.
                    var collectorTags = new HashSet<Tag>(keptSelect.GetSelectedObjects().Select(o => o.Tag));
                    retained.Clear();
                    foreach (Face[] group in groups.Where(group => group.All(face => collectorTags.Contains(face.Tag))))
                        foreach (Face face in group) retained.Add(face.Tag);
                    SyncCollector();
                }
                Preview();
                status.Label = RetainedGroupCount() + " connected boss/pocket groups; " + retained.Count +
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
                retained.Clear();
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
            var acceptedHeights = new List<double>();
            int rejectedByHeight = 0;
            foreach (Tag seed in boundary)
            {
                if (seen.Contains(seed)) continue;
                // Candidate discovery deliberately uses only body topology.
                // NX's Boss/Pocket selection-intent rule remains available in
                // the native collector for review, but it can merge unrelated
                // features or return only part of a connected character when
                // used as a scanner.
                Face[] component = ConnectedComponent(seed, faces, adjacency);
                if (component.Length == 0) continue;
                foreach (Face face in component) seen.Add(face.Tag);
                double height = FeatureHeight(component);
                double heightTolerance = Math.Max(1e-6, maxHeight.Value * 1e-6);
                if (height <= maxHeight.Value + heightTolerance && component.Length < faces.Count)
                {
                    groups.Add(component);
                    acceptedHeights.Add(height);
                }
                else if (height > maxHeight.Value)
                    rejectedByHeight++;
            }
            context.Log("Remove Markings scan: max height " + maxHeight.Value.ToString("0.###") +
                ", accepted topology groups " + acceptedHeights.Count +
                (acceptedHeights.Count == 0 ? string.Empty :
                    ", measured heights " + string.Join(", ", acceptedHeights.Select(value => value.ToString("0.###")))) +
                ", rejected above maximum " + rejectedByHeight + ".");
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
            // actual topology vertices onto that normal measures height without
            // mixing character width into the result.  Projecting an axis-aligned
            // WCS bounding box is not valid here: when the carrier normal is not
            // aligned to WCS, a wide character can falsely appear much taller.
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
            double height = 0;
            bool sampled = false;
            foreach (Edge edge in faces.SelectMany(face => face.GetEdges())
                .GroupBy(edge => edge.Tag).Select(group => group.First()))
            {
                try
                {
                    Point3d first;
                    Point3d second;
                    edge.GetVertices(out first, out second);
                    height = Math.Max(height, DistanceFromCarrier(first, carrierProjection, normal));
                    height = Math.Max(height, DistanceFromCarrier(second, carrierProjection, normal));
                    sampled = true;
                }
                catch (NXException) { }
            }
            // A closed analytic face can exceptionally have no usable edge
            // vertices.  Preserve a conservative fallback for that case only.
            return sampled ? height : BoundingBoxHeight(faces, carrierProjection, normal);
        }

        private static double DistanceFromCarrier(Point3d point, double carrierProjection, double[] normal)
        {
            double projection = point.X * normal[0] + point.Y * normal[1] + point.Z * normal[2];
            return Math.Abs(projection - carrierProjection);
        }

        private double BoundingBoxHeight(IEnumerable<Face> faces, double carrierProjection, double[] normal)
        {
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

        private Face[] RetainedFaces()
        {
            return groups.SelectMany(group => group).Where(face => retained.Contains(face.Tag)).ToArray();
        }

        private int RetainedGroupCount()
        {
            return groups.Count(group => group.All(face => retained.Contains(face.Tag)));
        }

        private void SyncCollector()
        {
            keptSelect.SetSelectedObjects(RetainedFaces().Cast<TaggedObject>().ToArray());
        }

        private void SelectionFocusChanged(UIBlock block, bool isFocus)
        {
            // Block Styler repaints native selection collectors when focus
            // changes and can clear programmatic UF highlights after Update
            // has returned. Reapply the retained snapshot after focus enters a
            // selection block so the viewport and collector count stay aligned.
            if (isFocus && ready && !updating) Preview();
        }

        private void KeyboardFocusChanged(UIBlock block, bool isFocus)
        {
            // Numeric controls have a separate keyboard-focus notification.
            // Repainting here covers the common workflow of editing the height
            // and then moving back to the face collector.
            if (isFocus && ready && !updating) Preview();
        }

        private void Preview()
        {
            Face[] faces = RetainedFaces();
            var retainedTags = new HashSet<Tag>(faces.Select(face => face.Tag));
            Tag[] excludedTags = groups.SelectMany(group => group)
                .Select(face => face.Tag)
                .Where(tag => !retainedTags.Contains(tag))
                .Distinct()
                .ToArray();

            // SetSelectedObjects has already asked the native FaceCollector to
            // display every retained face.  Do not unhighlight those same faces
            // here: clearing all candidates after populating the collector lets
            // Block Styler's deferred repaint restore the previous visual state.
            // Only clear faces that the user excluded, then reinforce the exact
            // retained snapshot in one batch.
            SetHighlights(excludedTags, 0);
            SetHighlights(retainedTags.ToArray(), 1);
            context.UF.Disp.Refresh();
        }

        private void ClearPreview()
        {
            SetGroupHighlights(0);
            context.UF.Disp.Refresh();
        }

        private void SetGroupHighlights(int highlight)
        {
            Tag[] tags = groups.SelectMany(group => group).Select(face => face.Tag).Distinct().ToArray();
            SetHighlights(tags, highlight);
        }

        private void SetHighlights(Tag[] tags, int highlight)
        {
            if (tags.Length == 0) return;
            try { context.UF.Disp.SetHighlights(tags.Length, tags, highlight); }
            catch (NXException)
            {
                // If one stale tag prevents a bulk update after a modeling
                // change, still clear or highlight every remaining valid face.
                foreach (Tag tag in tags)
                    try { context.UF.Disp.SetHighlight(tag, highlight); } catch (NXException) { }
            }
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
                retained.Clear();
                carrier = null;
                body = null;
            }
            finally { updating = false; }
        }

        private int Apply()
        {
            if (!ready) return 1;
            // Use the exact normalized preview snapshot.  The native collector
            // is a review UI and may expand its own selection-intent rules;
            // it is not the authority for the delete operation.
            Face[] selected = RetainedFaces();
            if (selected.Length == 0) return 1;
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
