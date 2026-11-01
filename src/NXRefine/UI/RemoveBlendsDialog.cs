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
        // Bounded defaults for diagnostics. Interactive Apply continues until
        // the native queue is exhausted or the user requests Stop.
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
        private bool continuousDelete;
        private Queue<DeleteJob> primaryJobs;
        private Queue<DeleteJob> retryJobs;
        private bool deletePassProgress;
        private HashSet<string> attemptedDeleteRegions;
        private SelectionScan activeDeleteScan;
        private Dictionary<Tag, Tag> deleteOwners;
        private Dictionary<Tag, int> validatedBodyFaults;
        private Dictionary<Tag, ConnectedChain> previewChains;
        private Dictionary<Tag, Tag> previewOwners;
        private Dictionary<Tag, BlendInfo> blendCache;
        private Dictionary<Tag, Face> committedFaces;
        private long recognitionTicks, commitTicks, consistencyTicks, rollbackTicks, resolveTicks;
        private int consistencyChecks;
        // Keep each complete native rule as a separate feature. Amortize the
        // expensive whole-body check over a small undo-protected transaction.
        private bool strictDeleteValidation = false; // Diagnostic comparison only.
        private int strictReplayJobs;
        private DeleteValidationGroup activeValidationGroup;

        private sealed class BlendInfo
        {
            internal double Radius;
            internal bool IsBlend;
            internal bool Available;
        }

        private sealed class DeleteJob
        {
            internal Tag Seed;
            internal int Mode;
        }

        private sealed class DeleteValidationGroup
        {
            internal Session.UndoMarkId Mark;
            internal DeleteJob[] Primary, Retry;
            internal HashSet<Tag> Seeds;
            internal HashSet<string> Regions;
            internal Dictionary<Tag, int> Faults;
            internal bool PassProgress;
            internal int Jobs, NativeAttempts;
            internal readonly Dictionary<Tag, Body> Bodies = new Dictionary<Tag, Body>();
        }

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
            primaryJobs = null;
            retryJobs = null;
            if (blendCache == null) blendCache = new Dictionary<Tag, BlendInfo>();
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
            Face[] inputFaces = bodies.Values.SelectMany(body => body.GetFaces()).ToArray();
            int scanned = 0;
            foreach (Face face in inputFaces)
            {
                SelectionScan.Checkpoint();
                    scanned++;
                    if (scanned == 1 || scanned % 32 == 0 || scanned == inputFaces.Length)
                        SelectionScan.ReportProgress("阶段 1/3：筛选倒圆角\r\n已检查 " + scanned + "/" + inputFaces.Length + " 个面", scanned, inputFaces.Length);
                    double radius;
                    bool isBlend;
                    if (!TryGetBlend(face, out radius, out isBlend)) continue;
                    if (isBlend && radius >= activeMinRadius && radius <= activeMaxRadius)
                        candidates[face.Tag] = face;
            }
            int seedCount = candidates.Count;
            recognitionSeeds = candidates.Keys.OrderBy(tag => tag).ToArray();
            Face[] expanded = BuildFastPreviewGroups(candidates.Values.OrderBy(face => face.Tag).ToArray()).SelectMany(group => group)
                .GroupBy(face => face.Tag).Select(group => group.First()).ToArray();
            candidates.Clear();
            foreach (Face face in expanded) candidates[face.Tag] = face;
            foreach (Tag tag in candidates.Keys) retained.Add(tag);
            SelectionScan.ReportProgress("阶段 3/3：显示选择结果\r\n正在显示 " + candidates.Count + " 个面");
            SyncFaceCollector();
            previewValid = bodies.Count > 0;
            context.Log("Remove Blends: " + seedCount + " radius-filtered seeds; " + candidates.Count + " expanded connected faces. Seed radii " +
                Format(activeMinRadius) + " and " + Format(activeMaxRadius) + ".");
        }

        private bool TryGetBlend(Face face, out double radius, out bool isBlend)
        {
            BlendInfo data;
            if (blendCache != null && blendCache.TryGetValue(face.Tag, out data))
            {
                radius = data.Radius;
                isBlend = data.IsBlend;
                return data.Available;
            }
            bool available = ReadBlendData(face, out radius, out isBlend);
            if (blendCache != null) blendCache[face.Tag] = new BlendInfo {
                Radius = radius, IsBlend = isBlend, Available = available };
            return available;
        }

        private bool ReadBlendData(Face face, out double radius, out bool isBlend)
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
            Tag[] reviewedTags = candidates.Keys.ToArray();
            Tag[] selectedBodies = bodies.Keys.ToArray();
            context.Session.SetUndoMark(Session.MarkVisibility.Visible, "NX Refine - Remove Blends");
            excludedFaces = new HashSet<Tag>(candidates.Keys.Where(tag => !retained.Contains(tag)));
            Reset();
            bool stopped = false;
            int previousDisplay = 1;
            bool restoreDisplay = false;
            try
            {
                // Avoid retessellating/redrawing the entire housing after every
                // Delete Face. The independent progress window stays responsive.
                if (!context.Session.IsBatch)
                {
                    context.UF.Disp.AskDisplay(out previousDisplay);
                    if (previousDisplay == 1)
                    {
                        restoreDisplay = true;
                        context.UF.Disp.SetDisplay(0);
                    }
                }
                using (var scan = new SelectionScan(context, "正在删除倒角。\r\n单击“停止”可中断。"))
                {
                    activeDeleteScan = scan;
                    continuousDelete = true;
                    try { DeleteBestEffort(selectedTags); }
                    catch (OperationCanceledException) { stopped = true; }
                    finally { activeDeleteScan = null; continuousDelete = false; }
                }
                if (stopped || deleteBudgetReported)
                    RestorePendingBlends(selectedBodies, reviewedTags, selectedTags);
            }
            catch (Exception ex) { return Error(ex); }
            finally
            {
                if (restoreDisplay)
                {
                    context.UF.Disp.SetDisplay(previousDisplay);
                    context.UF.Disp.RegenerateDisplay();
                }
            }
            int failed = ResolveCurrentFaces(selectedTags).Length;
            int removed = selected.Length - failed;
            if (deleteClock != null)
                context.Log("Remove Blends deletion: " + deleteAttempts + " attempts, " +
                    deleteClock.ElapsedMilliseconds + " ms.");
            context.Log("Remove Blends removed " + removed + " of " + selected.Length +
                " selected faces; " + failed + (stopped || deleteBudgetReported ?
                " remain; Apply continues the pending queue without scanning again." :
                " remain after native-chain attempts (see failure/skip log)."), true);
            context.UF.Disp.Refresh();
            return 0;
        }

        private void RestorePendingBlends(Tag[] bodyTags, Tag[] reviewedTags, Tag[] selectedTags)
        {
            updating = true;
            try
            {
                var wantedBodies = new HashSet<Tag>(bodyTags);
                Body[] liveBodies = context.WorkPart.Bodies.ToArray()
                    .Where(body => wantedBodies.Contains(body.Tag) && body.IsSolidBody).ToArray();
                var live = liveBodies.SelectMany(body => body.GetFaces()).ToDictionary(face => face.Tag);
                var selected = new HashSet<Tag>(selectedTags);
                foreach (Body body in liveBodies) bodies[body.Tag] = body;
                foreach (Tag tag in reviewedTags)
                    if (live.ContainsKey(tag))
                    {
                        candidates[tag] = live[tag];
                        if (selected.Contains(tag)) retained.Add(tag);
                    }
                if (bodySelect != null) bodySelect.SetSelectedObjects(liveBodies.Cast<TaggedObject>().ToArray());
                SyncFaceCollector();
                previewValid = retained.Count > 0;
                Preview();
                QueuePreviewRefresh();
            }
            finally { updating = false; }
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

        private Face[][] BuildFastPreviewGroups(Face[] seeds)
        {
            var sources = seeds.GroupBy(face => face.Tag).ToDictionary(group => group.Key, group => group.First());
            var queue = new Queue<Face>(sources.Values);
            var queried = new HashSet<Tag>();
            var outstanding = new HashSet<Tag>(sources.Keys);
            var covered = new HashSet<Tag>();
            var ambiguous = new HashSet<Tag>();
            var union = new HashSet<Tag>();
            var groups = new List<Face[]>();
            var groupTags = new List<HashSet<Tag>>();
            var keys = new HashSet<string>();
            while (queue.Count > 0)
            {
                SelectionScan.Checkpoint();
                Face seed = queue.Dequeue();
                if (queried.Contains(seed.Tag)) continue;
                if (covered.Contains(seed.Tag) && !ambiguous.Contains(seed.Tag) &&
                    seed.SolidFaceType != Face.FaceType.Spherical)
                {
                    outstanding.Remove(seed.Tag);
                    continue;
                }
                queried.Add(seed.Tag);
                int settled = sources.Count - outstanding.Count;
                SelectionScan.ReportProgress("阶段 2/3：识别连接倒圆角\r\n已检查或覆盖 " + settled + "/" + sources.Count + " 个源面\r\n已找到 " + union.Count + " 个面；正在计算连接链", settled, sources.Count);
                foreach (Face[] faces in BuildConnectedGroups(new[] { seed }))
                {
                    var tags = new HashSet<Tag>(faces.Select(face => face.Tag));
                    string key = string.Join(",", tags.OrderBy(tag => tag));
                    if (!keys.Add(key)) continue;
                    // A new unequal region that expands a covered source is
                    // not a canonical component. Exhaust every source in its
                    // overlap closure, retaining optimization elsewhere.
                    if (tags.Any(covered.Contains) && tags.Any(tag => !union.Contains(tag)))
                    {
                        var affected = new HashSet<Tag>(tags);
                        bool changed;
                        do
                        {
                            int count = affected.Count;
                            foreach (HashSet<Tag> previous in groupTags)
                                if (previous.Overlaps(affected)) affected.UnionWith(previous);
                            changed = count != affected.Count;
                        } while (changed);
                        foreach (Tag tag in affected)
                        {
                            ambiguous.Add(tag);
                            if (sources.ContainsKey(tag) && !queried.Contains(tag))
                            {
                                outstanding.Add(tag);
                                queue.Enqueue(sources[tag]);
                            }
                        }
                    }
                    groups.Add(faces);
                    groupTags.Add(tags);
                    union.UnionWith(tags);
                    foreach (Tag tag in tags) if (sources.ContainsKey(tag)) covered.Add(tag);
                }
                outstanding.Remove(seed.Tag);
            }
            context.Log("Remove Blends component preview: " + queried.Count + "/" + sources.Count +
                " native sources, " + ambiguous.Count + " faces in exhaustive overlap components.");
            SelectionScan.ReportProgress("阶段 2/3：连接识别完成\r\n已找到 " + union.Count + " 个面", sources.Count, sources.Count);
            return groups.ToArray();
        }

        private ConnectedChain[] BuildConnectedChains(Face[] seeds)
        {
            return BuildChainsForMode(seeds, 3);
        }

        private ConnectedChain[] BuildChainsForMode(Face[] seeds, int mode)
        {
            // Preview must remain complete, independently of a previous
            // deletion's stopped clock or exhausted attempt count.
            return EnumerateChainsForMode(seeds, mode, false).ToArray();
        }

        private IEnumerable<ConnectedChain> EnumerateChainsForMode(Face[] seeds, int mode, bool checkDeleteBudget)
        {
            var seen = new HashSet<string>();
            bool cachePreview = mode == 3 && !checkDeleteBudget;
            if (cachePreview && previewChains == null) previewChains = new Dictionary<Tag, ConnectedChain>();
            // Preview topology is immutable. Cache only exact ownership queries,
            // never infer that another seed returns the same native chain.
            if (cachePreview && previewOwners == null) previewOwners = new Dictionary<Tag, Tag>();
            var owners = cachePreview ? previewOwners : new Dictionary<Tag, Tag>();
            int nativeCalls = 0;
            foreach (Face seed in seeds)
            {
                SelectionScan.Checkpoint();
                if (checkDeleteBudget && !DeleteBudgetAvailable()) yield break;
                ConnectedChain cached;
                if (cachePreview && previewChains.TryGetValue(seed.Tag, out cached))
                {
                    string cachedKey = string.Join(",", cached.Faces.Select(face => face.Tag.ToString()));
                    if (seen.Add(cachedKey)) yield return cached;
                    continue;
                }
                ConnectedChain chain = null;
                var clock = Stopwatch.StartNew();
                ScCollector collector = context.WorkPart.ScCollectors.CreateCollector();
                FaceConnectedBlendRule rule = null;
                try
                {
                    nativeCalls++;
                    rule = CreateConnectedBlendRuleForMode(seed, mode);
                    collector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                    Face[] region = collector.GetObjects().OfType<Face>()
                        .GroupBy(face => face.Tag).Select(group => group.First()).OrderBy(face => face.Tag).ToArray();
                    // Radius is only a seed filter. Do not clip the native chain,
                    // synthesize uncovered single faces, or union overlapping rules.
                    Tag owner = FaceOwner(seed, owners);
                    if (region.Length == 0 || region.Any(face => FaceOwner(face, owners) != owner)) continue;
                    string key = string.Join(",", region.Select(face => face.Tag.ToString()));
                    if (cachePreview) previewChains[seed.Tag] = new ConnectedChain { Seed = seed.Tag, Faces = region };
                    if (seen.Add(key)) chain = new ConnectedChain { Seed = seed.Tag, Faces = region };
                }
                catch (NXException ex) { context.Log("Connected blend recognition failed: " + ex.Message); }
                finally
                {
                    try { collector.Destroy(); }
                    finally { if (rule != null) rule.Dispose(); }
                    if (clock.ElapsedMilliseconds >= 1000)
                        context.Log("Remove Blends slow native recognition: seed=" + seed.Tag +
                            ", mode=" + mode + ", " + clock.ElapsedMilliseconds + " ms.");
                }
                // Destroy the temporary collector before the caller mutates
                // topology. The delete builder recreates the complete live rule.
                if (chain != null) yield return chain;
            }
            if (!checkDeleteBudget && seeds.Length > 1) context.Log("Remove Blends preview: " + nativeCalls +
                " native rules; " + owners.Count + " unique face ownership queries.");
        }

        private static Tag FaceOwner(Face face, Dictionary<Tag, Tag> owners)
        {
            Tag owner;
            if (!owners.TryGetValue(face.Tag, out owner))
                owners.Add(face.Tag, owner = face.GetBody().Tag);
            return owner;
        }

        private int DeleteBestEffort(Tag[] tags)
        {
            // Any native edit invalidates preview-only geometry/rule caches.
            previewChains = null;
            previewOwners = null;
            blendCache = null;
            deleteAttempts = 0;
            deleteBudgetReported = false;
            deleteClock = Stopwatch.StartNew();
            recognitionTicks = commitTicks = consistencyTicks = rollbackTicks = resolveTicks = 0;
            consistencyChecks = 0;
            if (excludedFaces == null) excludedFaces = new HashSet<Tag>();
            // A native rule's seed need not belong to its expanded result.
            // Re-seeding only from preview faces can therefore lose the exact
            // operation which produced the preview, especially on later runs.
            var orderedSources = (recognitionSeeds ?? new Tag[0]).OrderBy(tag => tag)
                .Concat(tags.OrderBy(tag => tag)).Distinct().ToArray();
            var seedPool = new HashSet<Tag>(orderedSources);
            if (primaryJobs != null) seedPool.UnionWith(primaryJobs.Select(job => job.Seed));
            if (retryJobs != null) seedPool.UnionWith(retryJobs.Select(job => job.Seed));
            if (primaryJobs == null || retryJobs == null ||
                (primaryJobs.Count == 0 && retryJobs.Count == 0))
            {
                primaryJobs = new Queue<DeleteJob>(orderedSources.Select(tag => new DeleteJob { Seed = tag, Mode = 3 }));
                retryJobs = new Queue<DeleteJob>();
                deletePassProgress = false;
                attemptedDeleteRegions = new HashSet<string>();
                strictReplayJobs = 0;
            }
            if (attemptedDeleteRegions == null) attemptedDeleteRegions = new HashSet<string>();
            var live = ResolveCurrentFaces(seedPool).ToDictionary(face => face.Tag);
            int removedCount = tags.Count(tag => !live.ContainsKey(tag));
            int validatedRemovedCount = removedCount;
            deleteOwners = new Dictionary<Tag, Tag>();
            validatedBodyFaults = new Dictionary<Tag, int>();
            try
            {
                while (DeleteBudgetAvailable())
                {
                    SelectionScan.Checkpoint();
                    if (primaryJobs.Count == 0 && retryJobs.Count == 0)
                    {
                        if (!deletePassProgress) break;
                        primaryJobs = new Queue<DeleteJob>(live.Keys.Select(tag => new DeleteJob { Seed = tag, Mode = 3 }));
                        deletePassProgress = false;
                        attemptedDeleteRegions.Clear();
                    }
                    Queue<DeleteJob> queue = primaryJobs.Count > 0 ? primaryJobs : retryJobs;
                    if (queue.Count == 0) break;
                    if (!strictDeleteValidation && strictReplayJobs == 0 && activeValidationGroup == null)
                        BeginDeleteValidationGroup(seedPool);
                    DeleteJob job = queue.Peek();
                    Face seed;
                    if (live.TryGetValue(job.Seed, out seed))
                    {
                        if (activeDeleteScan != null)
                            activeDeleteScan.SetProgress("正在清理倒圆角\r\n已删除 " + validatedRemovedCount + "/" + tags.Length + " 个选面；已尝试 " + deleteAttempts + " 条链\r\n单击“停止”保留已完成的清理", validatedRemovedCount, tags.Length);
                        Tag[] recognized;
                        int attemptsBefore = deleteAttempts;
                        bool deleted = TryDeleteChainCore(seed, job.Mode, attemptedDeleteRegions, out recognized);
                        // A slow recognition may reach the time limit before
                        // commit. Keep that unattempted job for Resume.
                        if (!deleted && deleteBudgetReported && deleteAttempts == attemptsBefore) break;
                        queue.Dequeue();
                        foreach (Tag tag in recognized)
                            if (seedPool.Add(tag)) primaryJobs.Enqueue(new DeleteJob { Seed = tag, Mode = 3 });
                        if (deleted)
                        {
                            deletePassProgress = true;
                            // Retry changed failures only after a complete pass.
                            deleteOwners.Clear();
                            live = committedFaces.Where(pair => seedPool.Contains(pair.Key))
                                .ToDictionary(pair => pair.Key, pair => pair.Value);
                            removedCount = tags.Count(tag => !live.ContainsKey(tag));
                            if (activeValidationGroup == null) validatedRemovedCount = removedCount;
                        }
                        else
                        {
                            int nextMode = job.Mode == 3 ? 1 : job.Mode == 1 ? 2 : job.Mode == 2 ? 0 : -1;
                            if (nextMode >= 0) retryJobs.Enqueue(new DeleteJob { Seed = job.Seed, Mode = nextMode });
                        }
                    }
                    else queue.Dequeue();
                    if (strictReplayJobs > 0) strictReplayJobs--;
                    DeleteValidationGroup group = activeValidationGroup;
                    if (group != null)
                    {
                        group.Jobs++;
                        // Count failed kernel calls too: an invalid tentative
                        // heal must not poison an unbounded number of retries.
                        if (group.NativeAttempts >= 4 || group.Jobs >= 16 ||
                            (primaryJobs.Count == 0 && retryJobs.Count == 0))
                        {
                            if (!FinishDeleteValidationGroup(group))
                            {
                                seedPool = new HashSet<Tag>(group.Seeds);
                                live = ResolveCurrentFaces(seedPool).ToDictionary(face => face.Tag);
                                removedCount = tags.Count(tag => !live.ContainsKey(tag));
                            }
                            validatedRemovedCount = removedCount;
                        }
                    }
                }
            }
            finally
            {
                // Stop is cooperative. Validate or undo pending edits before
                // returning; do not call Checkpoint or replay after Stop.
                try
                {
                    if (activeValidationGroup != null) FinishDeleteValidationGroup(activeValidationGroup);
                }
                finally
                {
                    deleteOwners = null; validatedBodyFaults = null; committedFaces = null; deleteClock.Stop();
                    context.Log("Remove Blends phase timing (ms): recognition=" + ToMilliseconds(recognitionTicks) +
                        ", commit=" + ToMilliseconds(commitTicks) + ", consistency=" + ToMilliseconds(consistencyTicks) +
                        " (" + consistencyChecks + " checks), undo=" + ToMilliseconds(rollbackTicks) +
                        ", face-map=" + ToMilliseconds(resolveTicks) + ", total=" + deleteClock.ElapsedMilliseconds + ".");
                }
            }
            return tags.Length - ResolveCurrentFaces(tags).Length;
        }

        private DeleteValidationGroup BeginDeleteValidationGroup(HashSet<Tag> seeds)
        {
            var group = new DeleteValidationGroup
            {
                Primary = primaryJobs.ToArray(), Retry = retryJobs.ToArray(),
                Seeds = new HashSet<Tag>(seeds), Regions = new HashSet<string>(attemptedDeleteRegions),
                Faults = new Dictionary<Tag, int>(validatedBodyFaults), PassProgress = deletePassProgress,
                Mark = context.Session.SetUndoMark(Session.MarkVisibility.Invisible, "NX Refine - Validate Blend Edits")
            };
            activeValidationGroup = group;
            return group;
        }

        private bool FinishDeleteValidationGroup(DeleteValidationGroup group)
        {
            bool valid = true;
            var faults = new Dictionary<Tag, int>();
            try
            {
                if (group.Bodies.Count > 0 && activeDeleteScan != null)
                    activeDeleteScan.SetProgress("正在验证清理结果\r\n停止后保留通过验证的清理");
                foreach (var pair in group.Bodies)
                {
                    int count = BodyFaultCount(pair.Value);
                    faults.Add(pair.Key, count);
                    if (!pair.Value.IsSolidBody || count > validatedBodyFaults[pair.Key]) valid = false;
                }
            }
            catch (Exception ex)
            {
                valid = false;
                context.Log("Remove Blends grouped validation failed: " + ex.Message);
            }
            try
            {
                if (valid)
                {
                    foreach (var pair in faults) validatedBodyFaults[pair.Key] = pair.Value;
                    if (group.Bodies.Count > 0)
                        context.Log("Remove Blends validated transaction: " + group.NativeAttempts +
                            " native attempt(s), " + group.Bodies.Count + " body check(s).");
                }
                else
                {
                    long start = Stopwatch.GetTimestamp();
                    try { context.Session.UndoToMark(group.Mark, null); }
                    finally { rollbackTicks += Stopwatch.GetTimestamp() - start; }
                    primaryJobs = new Queue<DeleteJob>(group.Primary);
                    retryJobs = new Queue<DeleteJob>(group.Retry);
                    attemptedDeleteRegions = new HashSet<string>(group.Regions);
                    validatedBodyFaults = new Dictionary<Tag, int>(group.Faults);
                    deletePassProgress = group.PassProgress;
                    strictReplayJobs = group.Jobs;
                    if (deleteOwners != null) deleteOwners.Clear();
                    committedFaces = null;
                    context.Log("Remove Blends rolled back a transaction; queued " + group.Jobs +
                        " job(s) for individual consistency validation.");
                }
                return valid;
            }
            finally
            {
                activeValidationGroup = null;
                context.Session.DeleteUndoMark(group.Mark, null);
            }
        }

        private Face[] ResolveCurrentFaces(IEnumerable<Tag> tags)
        {
            var wanted = new HashSet<Tag>(tags);
            return CurrentFaceMap().Where(pair => wanted.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
        }

        private Dictionary<Tag, Face> CurrentFaceMap()
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                return context.WorkPart.Bodies.ToArray().Where(body => !body.IsOccurrence && body.IsSolidBody)
                    .SelectMany(body => body.GetFaces()).ToDictionary(face => face.Tag);
            }
            finally { resolveTicks += Stopwatch.GetTimestamp() - start; }
        }

        private static long ToMilliseconds(long ticks)
        {
            return ticks * 1000 / Stopwatch.Frequency;
        }

        private int BodyFaultCount(Body body)
        {
            int count;
            int[] faults;
            Tag[] objects;
            long start = Stopwatch.GetTimestamp();
            consistencyChecks++;
            try { context.UF.Modl.AskBodyConsistency(body.Tag, out count, out faults, out objects); }
            finally { consistencyTicks += Stopwatch.GetTimestamp() - start; }
            return count;
        }

        private bool TryDeleteConnectedChain(Face seed)
        {
            return TryDeleteChainForMode(seed, 3, null);
        }

        private bool TryDeleteChainForMode(Face seed, int mode, HashSet<string> attemptedRegions)
        {
            Tag[] recognized;
            return TryDeleteChainCore(seed, mode, attemptedRegions, out recognized);
        }

        private bool TryDeleteChainCore(Face seed, int mode, HashSet<string> attemptedRegions, out Tag[] recognized)
        {
            recognized = new Tag[0];
            committedFaces = null;
            DeleteFaceBuilder builder = null;
            FaceConnectedBlendRule liveRule = null;
            int recognizedCount = 0;
            string regionKey = null;
            Tag seedTag = seed.Tag;
            Session.UndoMarkId mark = context.Session.SetUndoMark(Session.MarkVisibility.Invisible,
                "NX Refine - Native Connected Blend Faces");
            try
            {
                long recognitionStart = Stopwatch.GetTimestamp();
                Body body = seed.GetBody();
                builder = context.WorkPart.Features.CreateDeleteFaceBuilder(null);
                builder.Type = DeleteFaceBuilder.SelectTypes.Face;
                builder.Heal = true;
                // Same selection intent as Delete Face > Connected Blend Faces.
                // Keep the live native rule in the actual builder, not a FaceDumbRule.
                liveRule = CreateConnectedBlendRuleForMode(seed, mode);
                builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] { liveRule }, false);
                Face[] chain = builder.FaceCollector.GetObjects().OfType<Face>().ToArray();
                recognitionTicks += Stopwatch.GetTimestamp() - recognitionStart;
                recognized = chain.Select(face => face.Tag).ToArray();
                recognizedCount = chain.Length;
                if (chain.Length == 0) return false;
                var owners = deleteOwners ?? new Dictionary<Tag, Tag>();
                if (chain.Any(face => FaceOwner(face, owners) != body.Tag))
                    throw new InvalidOperationException("Connected rule crossed the selected body.");
                if (excludedFaces != null && chain.Any(face => excludedFaces.Contains(face.Tag)))
                {
                    context.Log("Remove Blends skipped a connected chain containing a manually deselected face.");
                    return false;
                }
                regionKey = string.Join(",", chain.Select(face => face.Tag).OrderBy(tag => tag)
                    .Select(tag => tag.ToString()));
                if (attemptedRegions != null && attemptedRegions.Contains(regionKey)) return false;
                if (!BeginDeleteAttempt()) return false;
                if (attemptedRegions != null) attemptedRegions.Add(regionKey);
                Tag[] selected = chain.Select(face => face.Tag).ToArray();
                int originalFaults;
                if (validatedBodyFaults == null || !validatedBodyFaults.TryGetValue(body.Tag, out originalFaults))
                {
                    originalFaults = BodyFaultCount(body);
                    if (validatedBodyFaults != null) validatedBodyFaults[body.Tag] = originalFaults;
                }
                SelectionScan.Checkpoint();
                context.Log("Remove Blends native attempt: seed=" + seedTag + ", mode=" + mode +
                    ", faces=" + selected.Length + ".");
                long commitStart = Stopwatch.GetTimestamp();
                if (activeValidationGroup != null) activeValidationGroup.NativeAttempts++;
                try { builder.CommitFeature(); }
                finally { commitTicks += Stopwatch.GetTimestamp() - commitStart; }
                Dictionary<Tag, Face> after = CurrentFaceMap();
                if (selected.Any(after.ContainsKey))
                    throw new InvalidOperationException("NX did not remove the full currently selected native chain.");
                if (!body.IsSolidBody)
                    throw new InvalidOperationException("NX healing introduced body consistency errors.");
                if (activeValidationGroup != null) activeValidationGroup.Bodies[body.Tag] = body;
                else
                {
                    int currentFaults = BodyFaultCount(body);
                    if (currentFaults > originalFaults)
                        throw new InvalidOperationException("NX healing introduced body consistency errors.");
                    if (validatedBodyFaults != null) validatedBodyFaults[body.Tag] = currentFaults;
                }
                committedFaces = after;
                context.Log("Remove Blends " + (activeValidationGroup == null ? "committed" : "tentatively healed") +
                    " native connected chain: " + selected.Length +
                    " face(s), recognition mode=" + mode + ".");
                return true;
            }
            catch (Exception ex)
            {
                long rollbackStart = Stopwatch.GetTimestamp();
                try { context.Session.UndoToMark(mark, null); }
                finally { rollbackTicks += Stopwatch.GetTimestamp() - rollbackStart; }
                if (ex is OperationCanceledException)
                {
                    if (attemptedRegions != null && regionKey != null) attemptedRegions.Remove(regionKey);
                    throw;
                }
                context.Log("Remove Blends " + (ex is NXException ? "NX deletion failed" : "post-check rollback") +
                    " (seed=" + seedTag + ", mode=" + mode + ", faces=" + recognizedCount +
                    (ex is NXException ? ", code=" + ((NXException)ex).ErrorCode : "") + "): " + ex.Message);
                return false;
            }
            finally
            {
                try { if (builder != null) builder.Destroy(); }
                catch (NXException) { /* Rollback can already invalidate the builder. */ }
                finally
                {
                    try { if (liveRule != null) liveRule.Dispose(); }
                    catch (NXException) { /* Undo may already release the native rule. */ }
                    finally { context.Session.DeleteUndoMark(mark, null); }
                }
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
            if (continuousDelete) return true;
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
                if (!context.Session.IsBatch) context.UI.SelectionManager.ClearGlobalSelectionList();
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
            primaryJobs = null;
            retryJobs = null;
            previewChains = null;
            previewOwners = null;
            blendCache = null;
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
