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
        private SelectObject carrierSelect, keptSelect;
        private DoubleBlock limit;
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
            keptSelect = (SelectObject)dialog.TopBlock.FindBlock("faces");
            limit = (DoubleBlock)dialog.TopBlock.FindBlock("limit");
            status = (NXOpen.BlockStyler.Label)dialog.TopBlock.FindBlock("status");
            var mask = new Selection.MaskTriple(UFConstants.UF_solid_type, 0, UFConstants.UF_UI_SEL_FEATURE_ANY_FACE);
            foreach (SelectObject select in new[] { carrierSelect, keptSelect })
            {
                select.SetSelectionFilter(Selection.SelectionAction.ClearAndEnableSpecific, new[] { mask });
                select.MaximumScopeAsString = "Within Work Part Only";
            }
            ready = true;
            status.Label = "Select the lettering carrier face (its body is the search scope).";
        }

        private int Update(UIBlock block)
        {
            if (updating) return 0;
            try
            {
                updating = true;
                if (block == carrierSelect || block == limit || block.Name == "find")
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
                else if (block == keptSelect)
                {
                    // Removing one face from the native collector excludes its complete connected group.
                    var kept = new HashSet<Tag>(keptSelect.GetSelectedObjects().Select(o => o.Tag));
                    keptSelect.SetSelectedObjects(groups.Where(g => g.All(f => kept.Contains(f.Tag)))
                        .SelectMany(g => g).Cast<TaggedObject>().ToArray());
                }
                Preview();
                status.Label = groups.Count + " candidate groups; " + keptSelect.GetSelectedObjects().Length +
                    " faces retained. Review bosses and holes before Apply.";
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
            if (limit.Value <= 0 || double.IsNaN(limit.Value) || double.IsInfinity(limit.Value))
                throw new InvalidOperationException("Maximum group diagonal must be positive.");
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
                var component = new List<Face>();
                var queue = new Queue<Tag>();
                queue.Enqueue(seed);
                seen.Add(seed);
                while (queue.Count > 0)
                {
                    Tag tag = queue.Dequeue();
                    component.Add(faces[tag]);
                    foreach (Tag next in adjacency[tag]) if (seen.Add(next)) queue.Enqueue(next);
                }
                double diagonal = Diagonal(component);
                if (diagonal <= limit.Value && component.Count < faces.Count)
                    groups.Add(component.ToArray());
            }
        }

        private double Diagonal(IEnumerable<Face> faces)
        {
            double[] low = { double.MaxValue, double.MaxValue, double.MaxValue };
            double[] high = { double.MinValue, double.MinValue, double.MinValue };
            foreach (Face face in faces)
            {
                var box = new double[6];
                context.UF.Modl.AskBoundingBox(face.Tag, box);
                for (int i = 0; i < 3; i++) { low[i] = Math.Min(low[i], box[i]); high[i] = Math.Max(high[i], box[i + 3]); }
            }
            return Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (high[i] - low[i]) * (high[i] - low[i])));
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
