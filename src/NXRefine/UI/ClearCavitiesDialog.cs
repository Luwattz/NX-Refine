using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NXOpen;
using NXOpen.BlockStyler;
using NXOpen.Features;
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
            bool wasUpdating = updating;
            updating = true;
            try
            {
                using (var scan = new SelectionScan(context))
                {
                    RebuildCandidatesCore();
                    SelectionScan.Checkpoint();
                }
            }
            catch (OperationCanceledException)
            {
                Reset();
                context.Log("选面识别已停止；可重新选择后继续。");
            }
            finally { updating = wasUpdating; }
        }

        private void RebuildCandidatesCore()
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
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Face[][] result = NXRefine.Analysis.CavityShellDetector.Find(body);
            context.Log("Clear Cavities: " + result.Length + " internal shells, " +
                result.Sum(group => group.Length) + " cavity faces in body " + body.Tag +
                "; native topology " + timer.ElapsedMilliseconds + " ms");
            return result;
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
            try
            {
                Reset();
                DeleteCavityFaces(selected);
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
                context.UF.Disp.Refresh();
            }
        }

        private void DeleteCavityFaces(Face[] selected)
        {
            DeleteFaceBuilder builder = null;
            try
            {
                foreach (Face[] bodyFaces in selected.GroupBy(face => face.GetBody().Tag).Select(group => group.ToArray()))
                {
                    SelectionScan.Checkpoint();
                    builder = context.WorkPart.Features.CreateDeleteFaceBuilder(null);
                    builder.Type = DeleteFaceBuilder.SelectTypes.Face;
                    builder.Heal = true;
                    FaceDumbRule rule = context.WorkPart.ScRuleFactory.CreateRuleFaceDumb(bodyFaces);
                    builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                    builder.CommitFeature();
                    builder.Destroy();
                    builder = null;
                }
            }
            finally
            {
                if (builder != null) builder.Destroy();
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
