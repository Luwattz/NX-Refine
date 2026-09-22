using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using NXOpen;
using NXOpen.BlockStyler;
using NXOpen.Features;
using NXRefine.Core;
using SelectObject = NXOpen.BlockStyler.SelectObject;

namespace NXRefine.UI
{
    // Native Block Styler workflow for selecting solid bodies, filtering their
    // recognized blend faces by radius, reviewing individual faces, and
    // healing as many retained faces as NX can safely delete.
    internal sealed class RemoveBlendsDialog : IDisposable
    {
        private const double LargestRadius = 100000.0;
        // Delete Face is a kernel operation and can become extremely expensive
        // when a bad blend poisons a large connected chain.  Keep Apply a
        // bounded UI operation: a failed set is isolated, but never forever.
        private const int MaxDeleteAttempts = 256;
        private const int MaxDeleteMilliseconds = 120000;
        private HashSet<Tag> excludedFaces = new HashSet<Tag>();
        private Tag[] recognitionSeeds = new Tag[0];
        private readonly NxContext context;
        private readonly CleanupSettings settings;
        private readonly BlockDialog dialog;
        private readonly System.Windows.Forms.Timer radiusTimer;
        private readonly System.Windows.Forms.Timer previewTimer;
        private SelectObject bodySelect;
        private StringBlock minRadius;
        private StringBlock maxRadius;
        private FaceCollector faceSelect;
        private string minRadiusText;
        private string maxRadiusText;
        private readonly Dictionary<Tag, Body> bodies = new Dictionary<Tag, Body>();
        private readonly Dictionary<Tag, Face> candidates = new Dictionary<Tag, Face>();
        private readonly HashSet<Tag> retained = new HashSet<Tag>();
        private double activeMinRadius;
        private double activeMaxRadius;
        private bool radiusEditPending;
        private bool previewValid;
        private bool updating;
        private bool ready;
        private Stopwatch deleteClock;
        private int deleteAttempts;
        private bool deleteBudgetReported;

        public RemoveBlendsDialog(NxContext context, CleanupSettings settings)
        {
            this.context = context;
            this.settings = settings;
            context.RequireWorkPart();
            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "NXRefine.RemoveBlends.dlx");
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
            radiusTimer.Tick += RefreshRadii;
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
                catch (Exception ex) { context.Log("Remove Blends selection cleanup: " + ex); }
                ready = false;
            }
        }

        private void Initialize()
        {
            bodySelect = (SelectObject)dialog.TopBlock.FindBlock("bodies");
            bodySelect.AddFilter(SelectObject.FilterType.SolidBodies);
            bodySelect.SelectModeAsString = "Multiple";
            bodySelect.MaximumScopeAsString = "Within Work Part Only";

            minRadius = (StringBlock)dialog.TopBlock.FindBlock("minRadiusInput");
            maxRadius = (StringBlock)dialog.TopBlock.FindBlock("maxRadiusInput");
            minRadiusText = Format(settings.MinBlendRadius);
            maxRadiusText = Format(settings.MaxBlendRadius);
            minRadius.RetainValue = false;
            maxRadius.RetainValue = false;
            minRadius.Value = minRadiusText;
            maxRadius.Value = maxRadiusText;
            minRadius.SetKeystrokeCallback(MinRadiusEdited);
            maxRadius.SetKeystrokeCallback(MaxRadiusEdited);

            faceSelect = (FaceCollector)dialog.TopBlock.FindBlock("faces");
            faceSelect.EntityType = 16;
            faceSelect.MaximumScopeAsString = "Within Work Part Only";
            faceSelect.FaceRules = 1;
            faceSelect.DefaultFaceRulesAsString = "Single Face";
            faceSelect.PopupMenuEnabled = true;

            ReadRadiusRange(out activeMinRadius, out activeMaxRadius);
            radiusEditPending = false;
            ready = true;
        }

        private int Update(UIBlock block)
        {
            if (updating) return 0;
            try
            {
                updating = true;
                string name = block == null ? string.Empty : block.Name;
                if (name == "minRadiusInput" || name == "maxRadiusInput")
                {
                    if (radiusEditPending) return 0;
                    double minimum;
                    double maximum;
                    ReadRadiusRange(out minimum, out maximum);
                    if (previewValid && minimum == activeMinRadius && maximum == activeMaxRadius) return 0;
                    RebuildCandidates();
                }
                else
                {
                    if (radiusEditPending && !CommitRadiusInput()) return 0;
                    if (name == "bodies") UpdateBodies();
                    else if (name == "faces") UpdateFaceSelection();
                }
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
            var tags = new HashSet<Tag>(selected.Select(body => body.Tag));
            if (tags.SetEquals(bodies.Keys)) return;
            ClearPreview();
            bodies.Clear();
            foreach (Body body in selected) bodies[body.Tag] = body;
            RebuildCandidates();
        }

        private void UpdateFaceSelection()
        {
            retained.Clear();
            foreach (Face face in faceSelect.GetSelectedObjects().OfType<Face>())
                if (candidates.ContainsKey(face.Tag)) retained.Add(face.Tag);
            SyncFaceCollector();
            previewValid = bodies.Count > 0;
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
            radiusTimer.Stop();
            previewTimer.Stop();
            previewValid = false;
            ClearPreview();
            candidates.Clear();
            retained.Clear();
            if (faceSelect != null) faceSelect.SetSelectedObjects(new TaggedObject[0]);

            ReadRadiusRange(out activeMinRadius, out activeMaxRadius);
            settings.MinBlendRadius = activeMinRadius;
            settings.MaxBlendRadius = activeMaxRadius;
            settings.Save();
            foreach (Body body in bodies.Values)
            {
                SelectionScan.Checkpoint();
                foreach (Face face in body.GetFaces())
                {
                    SelectionScan.Checkpoint();
                    double radius;
                    bool isBlend;
                    if (!TryGetBlend(face, out radius, out isBlend)) continue;
                    if (isBlend && radius >= activeMinRadius && radius <= activeMaxRadius)
                        candidates[face.Tag] = face;
                }
            }
            int seedCount = candidates.Count;
            recognitionSeeds = candidates.Keys.ToArray();
            Face[] expanded = BuildConnectedGroups(candidates.Values.ToArray()).SelectMany(group => group)
                .GroupBy(face => face.Tag).Select(group => group.First()).ToArray();
            candidates.Clear();
            foreach (Face face in expanded) candidates[face.Tag] = face;
            foreach (Tag tag in candidates.Keys) retained.Add(tag);
            SyncFaceCollector();
            previewValid = bodies.Count > 0;
            context.Log("Remove Blends: " + seedCount + " radius-filtered seeds; " + candidates.Count + " expanded connected faces. Seed radii " +
                Format(activeMinRadius) + " and " + Format(activeMaxRadius) + ".");
        }

        private bool TryGetBlend(Face face, out double radius, out bool isBlend)
        {
            radius = 0.0;
            isBlend = false;
            try
            {
                face.GetBlendData(out radius, out isBlend);
                if (isBlend && radius > 0.0) return true;
            }
            catch (NXException) { }

            // Imported Parasolid bodies can lack blend feature history. In
            // that case GetBlendData may report false even when the underlying
            // surface is a fillet. UF_MODL_ask_face_data is the reliable
            // geometry query here: type 23 is explicitly "fillet (blend)".
            // Do not gate this query on SolidFaceType first. SolidFaceType is
            // a Parasolid classification and imported fillets are commonly
            // returned as a generic/analytic face even though UF reports the
            // face as type 23.
            try
            {
                int type;
                int normalDirection;
                double radiusData;
                double[] point = new double[3];
                double[] direction = new double[3];
                double[] box = new double[6];
                context.UF.Modl.AskFaceData(face.Tag, out type, point, direction, box,
                    out radius, out radiusData, out normalDirection);
                isBlend = type == 23 || face.SolidFaceType == Face.FaceType.Blending;
                if (!isBlend || radius <= 0.0)
                {
                    radius = 0.0;
                    isBlend = false;
                }
                return isBlend;
            }
            catch (NXException) { return false; }
        }

        private void ReadRadiusRange(out double minimum, out double maximum)
        {
            if (!TryReadRadius(minRadiusText, out minimum) || !TryReadRadius(maxRadiusText, out maximum))
                throw new InvalidOperationException("Enter blend radii from 0 to " + Format(LargestRadius) + " in the current part units.");
            if (minimum > maximum)
                throw new InvalidOperationException("Minimum blend radius cannot exceed maximum blend radius.");
        }

        private static bool TryReadRadius(string text, out double value)
        {
            return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
                !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0 && value <= LargestRadius;
        }

        private int MinRadiusEdited(StringBlock block, string value)
        {
            return RadiusEdited(ref minRadiusText, value);
        }

        private int MaxRadiusEdited(StringBlock block, string value)
        {
            return RadiusEdited(ref maxRadiusText, value);
        }

        private int RadiusEdited(ref string storedText, string uncommittedValue)
        {
            if (!ready || updating) return 0;
            if (storedText == uncommittedValue)
            {
                if (radiusEditPending) ScheduleRadiusCommit();
                return 0;
            }
            storedText = uncommittedValue;
            radiusEditPending = true;
            radiusTimer.Stop();
            previewTimer.Stop();
            previewValid = false;
            updating = true;
            try
            {
                ClearPreview();
                candidates.Clear();
                retained.Clear();
                if (faceSelect != null) faceSelect.SetSelectedObjects(new TaggedObject[0]);
            }
            catch (Exception ex) { Error(ex); }
            finally { updating = false; }
            return 0;
        }

        private bool CommitRadiusInput()
        {
            if (!radiusEditPending) return true;
            double minimum;
            double maximum;
            try { ReadRadiusRange(out minimum, out maximum); }
            catch (InvalidOperationException) { return false; }
            radiusEditPending = false;
            radiusTimer.Stop();
            return true;
        }

        private void ScheduleRadiusCommit()
        {
            radiusTimer.Stop();
            radiusTimer.Start();
        }

        private void RefreshRadii(object sender, EventArgs args)
        {
            radiusTimer.Stop();
            if (!ready || updating) return;
            try
            {
                if (CommitRadiusInput())
                {
                    RebuildCandidates();
                    Preview();
                    QueuePreviewRefresh();
                }
            }
            catch (Exception ex) { Error(ex); }
        }

        private bool CanApply()
        {
            if (!ready || updating || radiusEditPending || radiusTimer.Enabled || !previewValid || retained.Count == 0)
                return false;
            try
            {
                double minimum;
                double maximum;
                ReadRadiusRange(out minimum, out maximum);
                return minimum == activeMinRadius && maximum == activeMaxRadius;
            }
            catch (InvalidOperationException) { return false; }
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
            if (!ready || updating) return;
            if (!isFocus && block != null &&
                (block.Name == "minRadiusInput" || block.Name == "maxRadiusInput") && radiusEditPending)
            {
                ScheduleRadiusCommit();
                return;
            }
            if (isFocus && previewValid)
            {
                Preview();
                QueuePreviewRefresh();
            }
        }

        private Face[] RetainedFaces()
        {
            return candidates.Values.Where(face => retained.Contains(face.Tag)).ToArray();
        }

        private void SyncFaceCollector()
        {
            if (faceSelect != null)
                faceSelect.SetSelectedObjects(RetainedFaces().Cast<TaggedObject>().ToArray());
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
            SetHighlights(bodies.Keys.ToArray(), 0);
            SetHighlights(candidates.Keys.Where(tag => !retained.Contains(tag)).ToArray(), 0);
            SetHighlights(retained.ToArray(), 1);
            context.UF.Disp.Refresh();
        }

        private void ClearPreview()
        {
            SetHighlights(candidates.Keys.ToArray(), 0);
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

        private int Apply()
        {
            if (!ready) return 1;
            if (radiusEditPending)
            {
                if (!CommitRadiusInput()) return 1;
                try
                {
                    RebuildCandidates();
                    Preview();
                    QueuePreviewRefresh();
                }
                catch (Exception ex) { return Error(ex); }
                return 1;
            }
            try
            {
                double minimum;
                double maximum;
                ReadRadiusRange(out minimum, out maximum);
                if (!previewValid || radiusTimer.Enabled || minimum != activeMinRadius || maximum != activeMaxRadius)
                {
                    RebuildCandidates();
                    Preview();
                    QueuePreviewRefresh();
                    return 1;
                }
            }
            catch (Exception ex) { return Error(ex); }

            Face[] selected = RetainedFaces();
            if (selected.Length == 0) return 1;
            Tag[] selectedTags = selected.Select(face => face.Tag).ToArray();
            context.Session.SetUndoMark(Session.MarkVisibility.Visible, "NX Refine - Remove Blends");
            excludedFaces = new HashSet<Tag>(candidates.Keys.Where(tag => !retained.Contains(tag)));
            Reset();
            try { DeleteBestEffort(selectedTags); }
            catch (Exception ex) { return Error(ex); }
            int failed = ResolveCurrentFaces(selectedTags).Length;
            int removed = selected.Length - failed;
            if (deleteClock != null)
                context.Log("Remove Blends delete budget: " + deleteAttempts + " attempts, " +
                    deleteClock.ElapsedMilliseconds + " ms.");
            context.Log("Remove Blends removed " + removed + " of " + selected.Length +
                " selected faces; " + failed + (deleteBudgetReported ? " remain (processing budget reached)." : " remain after native-chain attempts (see failure/skip log)."), true);
            context.UF.Disp.Refresh();
            return 0;
        }

        private sealed class ConnectedChain
        {
            public Tag Seed;
            public Face[] Faces;
        }

        private FaceConnectedBlendRule CreateConnectedBlendRule(Face seed)
        {
            // Imported/previously healed loops can contain blend-like and
            // unlabeled transition faces. Use the same complete recognition
            // options for preview and the preferred Delete Face attempt.
            // Other complete native modes are tried only when progress stalls.
            return CreateConnectedBlendRuleForMode(seed, 3);
        }

        private FaceConnectedBlendRule CreateConnectedBlendRuleForMode(Face seed, int mode)
        {
            return context.WorkPart.ScRuleFactory.CreateRuleFaceConnectedBlend(seed,
                (mode & 1) != 0, (mode & 2) != 0, null);
        }
        private Face[][] BuildConnectedGroups(Face[] seeds)
        {
            return BuildConnectedChains(seeds).Select(chain => chain.Faces).ToArray();
        }

        private ConnectedChain[] BuildConnectedChains(Face[] seeds)
        {
            return BuildChainsForMode(seeds, 3);
        }

        private ConnectedChain[] BuildChainsForMode(Face[] seeds, int mode)
        {
            var result = new List<ConnectedChain>();
            var seen = new HashSet<string>();
            foreach (Face seed in seeds)
            {
                SelectionScan.Checkpoint();
                ScCollector collector = context.WorkPart.ScCollectors.CreateCollector();
                try
                {
                    collector.ReplaceRules(new SelectionIntentRule[] {
                        CreateConnectedBlendRuleForMode(seed, mode) }, false);
                    Face[] region = collector.GetObjects().OfType<Face>()
                        .GroupBy(face => face.Tag).Select(group => group.First()).OrderBy(face => face.Tag).ToArray();
                    // Radius is only a seed filter. Do not clip the native chain,
                    // synthesize uncovered single faces, or union overlapping rules.
                    if (region.Length == 0 || region.Any(face => face.GetBody().Tag != seed.GetBody().Tag)) continue;
                    string key = string.Join(",", region.Select(face => face.Tag.ToString()));
                    if (seen.Add(key)) result.Add(new ConnectedChain { Seed = seed.Tag, Faces = region });
                }
                catch (NXException ex) { context.Log("Connected blend recognition failed: " + ex.Message); }
                finally { collector.Destroy(); }
            }
            return result.ToArray();
        }

        private int DeleteBestEffort(Tag[] tags)
        {
            deleteAttempts = 0;
            deleteBudgetReported = false;
            deleteClock = Stopwatch.StartNew();
            if (excludedFaces == null) excludedFaces = new HashSet<Tag>();
            // A native rule's seed need not belong to its expanded result.
            // Re-seeding only from preview faces can therefore lose the exact
            // operation which produced the preview, especially on later runs.
            var seedPool = new HashSet<Tag>(tags.Concat(recognitionSeeds ?? new Tag[0]));
            bool progress;
            do
            {
                progress = false;
                var attemptedRegions = new HashSet<string>();
                // A broader native recognition result is not necessarily healable.
                // Exhaust the preferred mode, then try other complete native rules
                // if it made no progress. Never trim a rule into fixed face subsets.
                foreach (int mode in new[] { 3, 0, 1, 2 })
                {
                    SelectionScan.Checkpoint();
                    if (!DeleteBudgetAvailable()) break;
                    ConnectedChain[] chains = BuildChainsForMode(ResolveCurrentFaces(seedPool), mode);
                    foreach (ConnectedChain chain in chains)
                        foreach (Face face in chain.Faces) seedPool.Add(face.Tag);
                    foreach (ConnectedChain chain in chains.OrderByDescending(item => item.Faces.Length))
                    {
                        SelectionScan.Checkpoint();
                        if (!DeleteBudgetAvailable()) break;
                        Face seed = ResolveCurrentFaces(new[] { chain.Seed }).FirstOrDefault();
                        if (seed != null && TryDeleteChainForMode(seed, mode, attemptedRegions))
                        {
                            progress = true;
                            attemptedRegions.Clear(); // Topology changed; old failures may now heal.
                        }
                    }
                    if (progress) break;
                }
                // Re-recognize from current topology after progress, as manual
                // connected-face selection does after each successful operation.
            } while (progress && DeleteBudgetAvailable());
            deleteClock.Stop();
            return tags.Length - ResolveCurrentFaces(tags).Length;
        }

        private Face[] ResolveCurrentFaces(IEnumerable<Tag> tags)
        {
            var wanted = new HashSet<Tag>(tags);
            return context.WorkPart.Bodies.ToArray()
                .Where(body => !body.IsOccurrence && body.IsSolidBody)
                .SelectMany(body => body.GetFaces()).Where(face => wanted.Contains(face.Tag)).ToArray();
        }

        private int BodyFaultCount(Body body)
        {
            int count;
            int[] faults;
            Tag[] objects;
            context.UF.Modl.AskBodyConsistency(body.Tag, out count, out faults, out objects);
            return count;
        }

        private bool TryDeleteConnectedChain(Face seed)
        {
            return TryDeleteChainForMode(seed, 3, null);
        }

        private bool TryDeleteChainForMode(Face seed, int mode, HashSet<string> attemptedRegions)
        {
            DeleteFaceBuilder builder = null;
            int recognizedCount = 0;
            Tag seedTag = seed.Tag;
            Session.UndoMarkId mark = context.Session.SetUndoMark(Session.MarkVisibility.Invisible,
                "NX Refine - Native Connected Blend Faces");
            try
            {
                Body body = seed.GetBody();
                builder = context.WorkPart.Features.CreateDeleteFaceBuilder(null);
                builder.Type = DeleteFaceBuilder.SelectTypes.Face;
                builder.Heal = true;
                // Same selection intent as Delete Face > Connected Blend Faces.
                // Keep the live native rule in the actual builder, not a FaceDumbRule.
                builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] {
                    CreateConnectedBlendRuleForMode(seed, mode) }, false);
                Face[] chain = builder.FaceCollector.GetObjects().OfType<Face>().ToArray();
                recognizedCount = chain.Length;
                if (chain.Length == 0) return false;
                if (chain.Any(face => face.GetBody().Tag != body.Tag))
                    throw new InvalidOperationException("Connected rule crossed the selected body.");
                if (excludedFaces != null && chain.Any(face => excludedFaces.Contains(face.Tag)))
                {
                    context.Log("Remove Blends skipped a connected chain containing a manually deselected face.");
                    return false;
                }
                string regionKey = string.Join(",", chain.Select(face => face.Tag).OrderBy(tag => tag)
                    .Select(tag => tag.ToString()));
                if (attemptedRegions != null && !attemptedRegions.Add(regionKey)) return false;
                if (!BeginDeleteAttempt()) return false;
                Tag[] selected = chain.Select(face => face.Tag).ToArray();
                int originalFaults = BodyFaultCount(body);
                builder.CommitFeature();
                if (ResolveCurrentFaces(selected).Length != 0)
                    throw new InvalidOperationException("NX did not remove the full currently selected native chain.");
                if (!body.IsSolidBody || BodyFaultCount(body) > originalFaults)
                    throw new InvalidOperationException("NX healing introduced body consistency errors.");
                context.Log("Remove Blends committed native connected chain: " + selected.Length +
                    " face(s), recognition mode=" + mode + ".");
                return true;
            }
            catch (Exception ex)
            {
                context.Session.UndoToMark(mark, null);
                context.Log("Remove Blends " + (ex is NXException ? "NX deletion failed" : "post-check rollback") +
                    " (seed=" + seedTag + ", mode=" + mode + ", faces=" + recognizedCount +
                    (ex is NXException ? ", code=" + ((NXException)ex).ErrorCode : "") + "): " + ex.Message);
                return false;
            }
            finally
            {
                try { if (builder != null) builder.Destroy(); }
                catch (NXException) { /* Rollback can already invalidate the builder. */ }
                finally { context.Session.DeleteUndoMark(mark, null); }
            }
        }
        private bool BeginDeleteAttempt()
        {
            if (!DeleteBudgetAvailable()) return false;
            deleteAttempts++;
            return true;
        }

        private bool DeleteBudgetAvailable()
        {
            if (deleteClock == null) return true;
            bool available = deleteAttempts < MaxDeleteAttempts &&
                deleteClock.ElapsedMilliseconds < MaxDeleteMilliseconds;
            if (!available && !deleteBudgetReported)
            {
                deleteBudgetReported = true;
                context.Log("Remove Blends stopped early after reaching the safety budget (" +
                    MaxDeleteAttempts + " attempts or " + MaxDeleteMilliseconds + " ms). Remaining blends were left unchanged.");
            }
            return available;
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
                if (faceSelect != null) faceSelect.SetSelectedObjects(new TaggedObject[0]);
                context.UI.SelectionManager.ClearGlobalSelectionList();
                ClearPreview();
                bodies.Clear();
                candidates.Clear();
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

        private int Error(Exception ex)
        {
            radiusTimer.Stop();
            previewTimer.Stop();
            previewValid = false;
            ClearPreview();
            context.Log(ex.ToString());
            return 1;
        }

        private static string Format(double value)
        {
            return value.ToString("0.########", CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            radiusTimer.Stop();
            radiusTimer.Dispose();
            previewTimer.Stop();
            previewTimer.Dispose();
            try { context.UI.SelectionManager.ClearGlobalSelectionList(); }
            catch (Exception ex) { context.Log("Remove Blends selection cleanup: " + ex); }
            try { dialog.Dispose(); }
            finally
            {
                try { context.UI.SelectionManager.ClearGlobalSelectionList(); }
                catch (Exception ex) { context.Log("Remove Blends selection cleanup: " + ex); }
            }
        }
    }
}








