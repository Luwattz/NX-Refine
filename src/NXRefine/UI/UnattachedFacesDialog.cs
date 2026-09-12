using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using NXOpen;
using NXOpen.BlockStyler;
using NXOpen.Features;
using NXOpen.UF;
using NXRefine.Core;
using NXRefine.Analysis;
using SelectObject = NXOpen.BlockStyler.SelectObject;

namespace NXRefine.UI
{
    // Native Block Styler workflow for finding faces that are close in space but
    // not joined by topology. Separate solid bodies are repaired with native
    // Sew; gaps inside one body use native Delete Face/Heal on the smaller
    // face so that the surrounding faces can close the gap.
    internal sealed class UnattachedFacesDialog : IDisposable
    {
        private const int MaximumPairChecks = 1000000;

        private readonly NxContext context;
        private readonly CleanupSettings settings;
        private readonly BlockDialog dialog;
        private readonly System.Windows.Forms.Timer gapTimer;
        private readonly System.Windows.Forms.Timer previewTimer;
        private SelectObject bodySelect;
        private StringBlock maxGap;
        private FaceCollector faceSelect;
        private string gapText;
        private double activeGap;
        private double gapResolution;
        private readonly Dictionary<Tag, Body> bodies = new Dictionary<Tag, Body>();
        private readonly Dictionary<Tag, FaceInfo> faces = new Dictionary<Tag, FaceInfo>();
        private readonly List<GapGroup> groups = new List<GapGroup>();
        private readonly HashSet<Tag> retained = new HashSet<Tag>();
        private bool previewValid;
        private bool gapEditPending;
        private bool updating;
        private bool ready;
        private bool scanCurrent;
        private int distanceQueries;
        private int containmentQueries;

        public UnattachedFacesDialog(NxContext context, CleanupSettings settings)
        {
            this.context = context;
            this.settings = settings;
            context.RequireWorkPart();
            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "NXRefine.RepairUnattached.dlx");
            dialog = context.UI.CreateDialog(path);
            dialog.AddInitializeHandler(Initialize);
            dialog.AddUpdateHandler(Update);
            dialog.AddApplyHandler(Apply);
            dialog.AddOkHandler(Apply);
            dialog.AddCancelHandler(Cancel);
            dialog.AddFocusNotifyHandler(FocusChanged);
            dialog.AddKeyboardFocusNotifyHandler(KeyboardFocusChanged);
            dialog.AddEnableOKButtonHandler(CanApply);
            gapTimer = new System.Windows.Forms.Timer { Interval = 200 };
            gapTimer.Tick += RefreshGap;
            previewTimer = new System.Windows.Forms.Timer { Interval = 50 };
            previewTimer.Tick += RefreshPreview;
        }

        public void ShowDialog()
        {
            try { dialog.Launch(); }
            finally
            {
                gapTimer.Stop();
                previewTimer.Stop();
                try { ClearPreview(); }
                catch (Exception ex) { context.Log("Repair Unattached Faces preview cleanup: " + ex); }
                try { context.UI.SelectionManager.ClearGlobalSelectionList(); }
                catch (Exception ex) { context.Log("Repair Unattached Faces selection cleanup: " + ex); }
                ready = false;
            }
        }

        private void Initialize()
        {
            bodySelect = (SelectObject)dialog.TopBlock.FindBlock("bodies");
            bodySelect.AddFilter(SelectObject.FilterType.SolidBodies);
            bodySelect.SelectModeAsString = "Multiple";
            bodySelect.MaximumScopeAsString = "Within Work Part Only";

            maxGap = (StringBlock)dialog.TopBlock.FindBlock("maxGap");
            gapText = settings.SewTolerance.ToString("0.########", CultureInfo.InvariantCulture);
            maxGap.RetainValue = false;
            maxGap.Value = gapText;
            maxGap.SetKeystrokeCallback(GapEdited);

            faceSelect = (FaceCollector)dialog.TopBlock.FindBlock("faces");
            faceSelect.EntityType = 16; // Faces
            faceSelect.MaximumScopeAsString = "Within Work Part Only";
            faceSelect.FaceRules = 1; // Single Face; connected candidate grouping is handled by the model
            faceSelect.DefaultFaceRulesAsString = "Single Face";
            faceSelect.PopupMenuEnabled = true;

            activeGap = ReadMaximumGap();
            gapEditPending = false;
            ready = true;
        }

        private int Update(UIBlock block)
        {
            if (updating) return 0;
            try
            {
                updating = true;
                string blockName = block == null ? string.Empty : block.Name;
                // StringBlock can raise Update for every keystroke. Keep the
                // text provisional until focus loss, Enter, or an explicit
                // command commits it; never scan an incomplete value.
                if (blockName == "maxGap")
                {
                    if (gapEditPending) return 0;
                    if (scanCurrent && ReadMaximumGap() == activeGap) return 0;
                    RebuildCandidates();
                }
                else
                {
                    if (gapEditPending && !CommitGapInput()) return 0;
                    if (blockName == "bodies") UpdateBodies();
                    else if (blockName == "faces") UpdateFaceSelection();
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
            var selectedTags = new HashSet<Tag>(selected.Select(body => body.Tag));
            if (selectedTags.SetEquals(bodies.Keys)) return;
            ClearPreview();
            bodies.Clear();
            foreach (Body body in selected) bodies[body.Tag] = body;
            RebuildCandidates();
        }

        private void UpdateFaceSelection()
        {
            var selectedTags = new HashSet<Tag>(faceSelect.GetSelectedObjects().OfType<Face>()
                .Select(face => face.Tag));
            retained.Clear();
            // A candidate gap is a connected group. Deselecting one face
            // removes the complete group from the pending repair.
            foreach (GapGroup group in groups.Where(item => item.Faces.All(face => selectedTags.Contains(face.Tag))))
                foreach (Face face in group.Faces) retained.Add(face.Tag);
            SyncFaceCollector();
            previewValid = groups.Count > 0;
        }

        private void RebuildCandidates()
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            distanceQueries = containmentQueries = 0;
            scanCurrent = false;
            gapTimer.Stop();
            previewTimer.Stop();
            previewValid = false;
            ClearPreview();
            faces.Clear();
            groups.Clear();
            retained.Clear();
            if (faceSelect != null) faceSelect.SetSelectedObjects(new TaggedObject[0]);
            activeGap = ReadMaximumGap();

            // Numerical zero in part units, independent of the user's search limit.
            gapResolution = context.WorkPart.PartUnits == BasePart.Units.Inches ? 1e-6 / 25.4 : 1e-6;
            // A zero/sub-resolution limit cannot contain any positive gap.
            if (activeGap <= gapResolution) { scanCurrent = true; return; }
            foreach (Body body in bodies.Values) AddBodyFaces(body);
            BuildTopologicalAdjacency();
            groups.AddRange(FindGapGroups());
            foreach (Face face in groups.SelectMany(group => group.Faces)) retained.Add(face.Tag);
            SyncFaceCollector();
            previewValid = groups.Count > 0;
            scanCurrent = true;
            context.Log("Repair Unattached Faces scan: " + elapsed.ElapsedMilliseconds +
                " ms; " + faces.Count + " faces; " + distanceQueries +
                " distance queries; " + containmentQueries + " containment queries.");
        }

        private double ReadMaximumGap()
        {
            double value;
            if ((!double.TryParse(gapText, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
                 !double.TryParse(gapText, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) ||
                double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 100000.0)
                throw new InvalidOperationException(
                    "Enter a non-negative maximum gap in the current part units (for example, 0.05). ");
            return value;
        }

        private int GapEdited(StringBlock block, string uncommittedValue)
        {
            if (!ready || updating) return 0;
            // Enter is reported without changing the text. Treat that
            // unchanged callback as an explicit commit request and defer one
            // UI tick so the native edit can settle.
            if (gapText == uncommittedValue)
            {
                if (gapEditPending) ScheduleGapCommit();
                return 0;
            }
            gapText = uncommittedValue;
            gapEditPending = true;
            scanCurrent = false;
            gapTimer.Stop();
            previewTimer.Stop();
            previewValid = false;
            updating = true;
            try
            {
                ClearPreview();
                faces.Clear();
                groups.Clear();
                retained.Clear();
                if (faceSelect != null) faceSelect.SetSelectedObjects(new TaggedObject[0]);
            }
            catch (Exception ex) { Error(ex); }
            finally { updating = false; }
            return 0;
        }

        private void RefreshGap(object sender, EventArgs args)
        {
            gapTimer.Stop();
            if (!ready || updating) return;
            try
            {
                if (CommitGapInput())
                {
                    // Use the dialog update guard while changing collectors.
                    Update(maxGap);
                }
            }
            catch (Exception ex) { Error(ex); }
        }

        private void AddBodyFaces(Body body)
        {
            foreach (Face face in body.GetFaces())
            {
                FaceInfo info;
                if (TryGetFaceInfo(body, face, out info)) faces[face.Tag] = info;
            }
        }

        private bool TryGetFaceInfo(Body body, Face face, out FaceInfo info)
        {
            info = null;
            try
            {
                double[] box = new double[6];
                context.UF.Modl.AskBoundingBox(face.Tag, box);
                double[] point;
                double[] normal;
                TryGetFacePointNormal(face, box, out point, out normal);
                info = new FaceInfo(body, face, box, point, normal);
                return true;
            }
            catch (NXException)
            {
                return false;
            }
        }

        private void BuildTopologicalAdjacency()
        {
            foreach (Body body in bodies.Values)
            {
                foreach (Edge edge in body.GetEdges())
                {
                    Face[] touching = edge.GetFaces()
                        .Where(face => faces.ContainsKey(face.Tag))
                        .GroupBy(face => face.Tag).Select(group => group.First()).ToArray();
                    for (int i = 0; i < touching.Length; i++)
                        for (int j = i + 1; j < touching.Length; j++)
                        {
                            faces[touching[i].Tag].Adjacent.Add(touching[j].Tag);
                            faces[touching[j].Tag].Adjacent.Add(touching[i].Tag);
                        }
                }
            }
        }

        private GapGroup[] FindGapGroups()
        {
            int sweepAxis = GapCriteria.SweepAxis(faces.Values.Select(info => info.Box).ToArray(), activeGap);
            FaceInfo[] sorted = faces.Values.OrderBy(info => info.Box[sweepAxis]).ThenBy(info => info.Box[1])
                .ThenBy(info => info.Box[2]).ToArray();
            var pairs = new List<GapPair>();
            int checks = 0;
            bool limitReached = false;
            double searchTolerance = Math.Max(activeGap, 1e-9);

            for (int i = 0; i < sorted.Length && !limitReached; i++)
            {
                FaceInfo first = sorted[i];
                for (int j = i + 1; j < sorted.Length; j++)
                {
                    FaceInfo second = sorted[j];
                    if (second.Box[sweepAxis] > first.Box[sweepAxis + 3] + searchTolerance) break;
                    if (++checks > MaximumPairChecks)
                    {
                        limitReached = true;
                        context.Log("Repair Unattached Faces stopped after " + MaximumPairChecks +
                            " close-pair checks; use a smaller gap tolerance or select fewer bodies.");
                        break;
                    }
                    if (first.Body.Tag == second.Body.Tag && first.Adjacent.Contains(second.Face.Tag)) continue;
                    if (!BoxesWithin(first.Box, second.Box, searchTolerance)) continue;
                    // Planar normals are constant over the trimmed face. This
                    // is the same opposing-normal requirement as patch sampling.
                    if (first.IsPlanar && second.IsPlanar && first.Normal != null && second.Normal != null &&
                        GapCriteria.Dot(first.Normal, second.Normal) /
                        (Length(first.Normal) * Length(second.Normal)) > -0.95) continue;

                    GapPair pair;
                    if (TryMeasureGap(first, second, out pair)) pairs.Add(pair);
                }
            }

            if (pairs.Count == 0) return new GapGroup[0];
            var index = new Dictionary<Tag, int>();
            foreach (GapPair pair in pairs)
            {
                Tag source = SmallerFace(pair.First, pair.Second).Tag;
                if (!index.ContainsKey(source)) index[source] = index.Count;
            }
            var union = new UnionFind(index.Count);
            foreach (Tag source in index.Keys)
                foreach (Tag adjacent in faces[source].Adjacent)
                    if (index.ContainsKey(adjacent)) union.Join(index[source], index[adjacent]);

            var byRoot = new Dictionary<int, List<GapPair>>();
            foreach (GapPair pair in pairs)
            {
                int root = union.Find(index[SmallerFace(pair.First, pair.Second).Tag]);
                List<GapPair> list;
                if (!byRoot.TryGetValue(root, out list))
                {
                    list = new List<GapPair>();
                    byRoot.Add(root, list);
                }
                list.Add(pair);
            }

            var result = new List<GapGroup>();
            foreach (List<GapPair> pairList in byRoot.Values)
            {
                // Carrier faces are references, not selection bridges between
                // unrelated ribs. Highlight only the small problem-side faces.
                Face[] groupFaces = pairList.Select(pair => SmallerFace(pair.First, pair.Second))
                    .GroupBy(face => face.Tag).Select(group => group.First()).ToArray();
                result.Add(new GapGroup(groupFaces, pairList.ToArray()));
            }
            context.Log("Repair Unattached Faces: " + result.Count + " candidate gap groups, " +
                pairs.Count + " close face pairs in " + faces.Count + " scanned faces.");
            return result.OrderBy(group => group.Faces.Min(face => face.Tag.ToString()), StringComparer.Ordinal).ToArray();
        }

        private bool TryMeasureGap(FaceInfo first, FaceInfo second, out GapPair pair)
        {
            pair = null;
            double distance;
            double accuracy;
            double[] pointFirst = new double[3];
            double[] pointSecond = new double[3];
            double[] guessFirst = first.Point ?? first.Center;
            double[] guessSecond = second.Point ?? second.Center;
            try
            {
                distanceQueries++;
                context.UF.Modl.AskMinimumDist3(2, first.Face.Tag, second.Face.Tag, 1, guessFirst,
                    1, guessSecond, out distance, pointFirst, pointSecond, out accuracy);
            }
            catch (NXException)
            {
                try
                {
                    distanceQueries++;
                    context.UF.Modl.AskMinimumDist(first.Face.Tag, second.Face.Tag, 1, guessFirst,
                        1, guessSecond, out distance, pointFirst, pointSecond);
                    accuracy = 0.0;
                }
                catch (NXException) { return false; }
            }
            if (double.IsNaN(distance) || double.IsInfinity(distance) || distance < 0.0) return false;
            if (double.IsNaN(accuracy) || double.IsInfinity(accuracy) || accuracy < 0.0) return false;
            if (!GapCriteria.PositiveGap(distance, activeGap, gapResolution, accuracy)) return false;
            // A minimum distance alone also accepts shared vertices, edge-only
            // proximity, thin material and intentional nearby details. Require
            // a two-dimensional patch of opposing faces across empty space.
            if (!HasGapPatch(first, second, pointFirst, pointSecond, distance)) return false;

            // For separate bodies, normalize target/tool orientation so a
            // connected candidate is repaired in one deterministic sew.
            if (first.Body.Tag != second.Body.Tag &&
                StringComparer.Ordinal.Compare(first.Body.Tag.ToString(), second.Body.Tag.ToString()) > 0)
            {
                FaceInfo swap = first;
                first = second;
                second = swap;
                double[] swapPoint = pointFirst;
                pointFirst = pointSecond;
                pointSecond = swapPoint;
            }
            pair = new GapPair(first, second, distance, pointFirst, pointSecond);
            return true;
        }

        private static bool BoxesWithin(double[] first, double[] second, double tolerance)
        {
            double x = AxisGap(first[0], first[3], second[0], second[3]);
            double y = AxisGap(first[1], first[4], second[1], second[4]);
            double z = AxisGap(first[2], first[5], second[2], second[5]);
            return x * x + y * y + z * z <= tolerance * tolerance;
        }

        private static double AxisGap(double firstMin, double firstMax, double secondMin, double secondMax)
        {
            if (firstMax < secondMin) return secondMin - firstMax;
            if (secondMax < firstMin) return firstMin - secondMax;
            return 0.0;
        }

        private bool HasGapPatch(FaceInfo first, FaceInfo second, double[] closestFirst,
            double[] closestSecond, double distance)
        {
            // Sample the smaller contact face, never the whole carrier's box.
            if (SmallerFace(first, second).Tag != first.Face.Tag)
            {
                FaceInfo swap = first; first = second; second = swap;
                double[] swapPoint = closestFirst; closestFirst = closestSecond; closestSecond = swapPoint;
            }
            try
            {
                double[] normal = NormalAt(first, closestFirst);
                double length = Length(normal);
                if (length < 1e-12) return false;
                normal = normal.Select(value => value / length).ToArray();
                double[] axis = Math.Abs(normal[0]) < 0.8 ? new[] { 1.0, 0.0, 0.0 } : new[] { 0.0, 1.0, 0.0 };
                double[] u = Cross(normal, axis);
                double uLength = Length(u);
                u = u.Select(value => value / uLength).ToArray();
                double[] v = Cross(normal, u);
                double step = Math.Max(gapResolution * 20,
                    Math.Min(Math.Sqrt(ApproximateFaceSize(first.Box)) * 0.02, distance * 2));
                var samples = new List<double[]>();
                foreach (double[] anchor in new[] { closestFirst, first.Point ?? closestFirst })
                {
                    for (int i = -1; i <= 1; i++)
                        for (int j = -1; j <= 1; j++)
                        {
                            double[] guess = Enumerable.Range(0, 3)
                                .Select(k => anchor[k] + step * (i * u[k] + j * v[k])).ToArray();
                            double[] p, q;
                            double projectionError, measured;
                            if (!ClosestPoint(first.Face, guess, out p, out projectionError) || projectionError > step * 0.25) continue;
                            if (!ClosestPoint(second.Face, p, out q, out measured)) continue;
                            if (!GapCriteria.PositiveGap(measured, activeGap, gapResolution, gapResolution * 0.1)) continue;
                            double[] displacement = Enumerable.Range(0, 3).Select(k => q[k] - p[k]).ToArray();
                            if (!GapCriteria.Facing(NormalAt(first, p), NormalAt(second, q), displacement)) continue;
                            if (!EmptyGap(p, q)) continue;
                            samples.Add(p);
                            if (GapCriteria.NewSampleFormsArea(samples, step * step * 0.25)) return true;
                        }
                }
                return false;
            }
            catch (NXException ex)
            {
                context.Log("Gap verification skipped uncertain pair: " + ex.Message);
                return false;
            }
        }

        private bool ClosestPoint(Face face, double[] reference, out double[] point, out double distance)
        {
            point = new double[3];
            double accuracy;
            distanceQueries++;
            context.UF.Modl.AskMinimumDist3(2, Tag.Null, face.Tag, 1, reference, 0, new double[3],
                out distance, new double[3], point, out accuracy);
            return !double.IsNaN(distance) && !double.IsInfinity(distance) &&
                !double.IsNaN(accuracy) && accuracy >= 0 && accuracy <= gapResolution * 0.1;
        }

        private double[] NormalAt(FaceInfo info, double[] reference)
        {
            if (info.IsPlanar && info.Normal != null) return info.Normal;
            Face face = info.Face;
            double[] uv = new double[2], point = new double[3], normal = new double[3];
            context.UF.Modl.AskFaceParm(face.Tag, reference, uv, point);
            context.UF.Modl.AskFaceProps(face.Tag, uv, point, new double[3], new double[3],
                new double[3], new double[3], normal, new double[2]);
            return normal;
        }

        private bool EmptyGap(double[] first, double[] second)
        {
            foreach (double fraction in new[] { 0.25, 0.5, 0.75 })
            {
                double[] point = Enumerable.Range(0, 3).Select(k => first[k] + fraction * (second[k] - first[k])).ToArray();
                foreach (Body body in bodies.Values)
                {
                    int status;
                    containmentQueries++;
                    context.UF.Modl.AskPointContainment(point, body.Tag, out status);
                    if (status != 2) return false; // Inside or on material is not an open gap.
                }
            }
            return true;
        }

        private static double[] Cross(double[] a, double[] b)
        {
            return new[] { a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0] };
        }

        private static double Length(double[] vector)
        {
            return Math.Sqrt(vector[0] * vector[0] + vector[1] * vector[1] + vector[2] * vector[2]);
        }

        private bool TryGetFacePointNormal(Face face, double[] box, out double[] point, out double[] normal)
        {
            point = null;
            normal = null;
            try
            {
                double[] reference = {
                    (box[0] + box[3]) * 0.5,
                    (box[1] + box[4]) * 0.5,
                    (box[2] + box[5]) * 0.5 };
                double[] parameter = new double[2];
                point = new double[3];
                context.UF.Modl.AskFaceParm(face.Tag, reference, parameter, point);
                double[] u1 = new double[3];
                double[] v1 = new double[3];
                double[] u2 = new double[3];
                double[] v2 = new double[3];
                double[] radii = new double[2];
                normal = new double[3];
                context.UF.Modl.AskFaceProps(face.Tag, parameter, point, u1, v1, u2, v2, normal, radii);
                if (Length(normal) < 1e-9)
                {
                    point = null;
                    normal = null;
                    return false;
                }
                return true;
            }
            catch (NXException)
            {
                point = null;
                normal = null;
                return false;
            }
        }

        private void SyncFaceCollector()
        {
            if (faceSelect == null) return;
            faceSelect.SetSelectedObjects(RetainedFaces().Cast<TaggedObject>().ToArray());
        }

        private Face[] RetainedFaces()
        {
            return groups.SelectMany(group => group.Faces).Where(face => retained.Contains(face.Tag)).ToArray();
        }

        private bool CanApply()
        {
            if (!ready || updating || gapEditPending || !previewValid || gapTimer.Enabled || retained.Count == 0) return false;
            try { return ReadMaximumGap() == activeGap; }
            catch (InvalidOperationException) { return false; }
        }

        private bool CommitGapInput()
        {
            if (!gapEditPending) return true;
            try { ReadMaximumGap(); }
            catch (InvalidOperationException) { return false; }
            gapEditPending = false;
            gapTimer.Stop();
            return true;
        }

        private void ScheduleGapCommit()
        {
            gapTimer.Stop();
            gapTimer.Start();
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
            if (!isFocus && block != null && block.Name == "maxGap" && gapEditPending)
            {
                ScheduleGapCommit();
                return;
            }
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
            Face[] candidateFaces = groups.SelectMany(group => group.Faces).ToArray();
            var candidateTags = new HashSet<Tag>(candidateFaces.Select(face => face.Tag));
            var retainedTags = new HashSet<Tag>(RetainedFaces().Select(face => face.Tag));
            SetHighlights(candidateTags.ToArray(), 0);
            SetHighlights(bodies.Keys.ToArray(), 0);
            SetHighlights(retainedTags.ToArray(), 1);
            context.UF.Disp.Refresh();
        }

        private void ClearPreview()
        {
            SetHighlights(groups.SelectMany(group => group.Faces).Select(face => face.Tag).Distinct().ToArray(), 0);
            SetHighlights(bodies.Keys.ToArray(), 0);
            context.UF.Disp.Refresh();
        }

        private void SetHighlights(Tag[] tags, int highlight)
        {
            if (tags == null || tags.Length == 0) return;
            try { context.UF.Disp.SetHighlights(tags.Length, tags, highlight); }
            catch (NXException)
            {
                foreach (Tag tag in tags)
                    try { context.UF.Disp.SetHighlight(tag, highlight); } catch (NXException) { }
            }
        }

        private void Reset()
        {
            scanCurrent = false;
            gapTimer.Stop();
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
                faces.Clear();
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
            if (gapEditPending)
            {
                if (!CommitGapInput()) return 1;
                // Rebuild now, then require a second click so the user can
                // review the new gap candidates before repairing them.
                try
                {
                    Update(maxGap);
                }
                catch (Exception ex) { return Error(ex); }
                return 1;
            }
            if (!previewValid || gapTimer.Enabled || retained.Count == 0) return 1;
            try
            {
                if (ReadMaximumGap() != activeGap) return 1;
            }
            catch (InvalidOperationException) { return 1; }

            GapGroup[] selected = groups.Where(group => group.Faces.All(face => retained.Contains(face.Tag))).ToArray();
            if (selected.Length == 0) return 1;
            Session.UndoMarkId mark = context.Session.SetUndoMark(Session.MarkVisibility.Visible,
                "NX Refine - Repair Unattached Faces");
            try
            {
                Reset(); // Clear highlights while face tags are still valid.
                RepairSelectedGroups(selected);
                context.Log("Repaired " + selected.Length + " unattached face groups.");
                return 0;
            }
            catch (Exception ex)
            {
                context.Session.UndoToMark(mark, "NX Refine - Repair Unattached Faces");
                return Error(ex);
            }
            finally { context.UF.Disp.Refresh(); }
        }

        private void RepairSelectedGroups(IEnumerable<GapGroup> selectedGroups)
        {
            var sameBodyFaces = new Dictionary<Tag, Dictionary<Tag, Face>>();
            var crossBodyPairs = new Dictionary<string, List<GapPair>>();
            foreach (GapGroup group in selectedGroups)
            {
                foreach (GapPair pair in group.Pairs)
                {
                    if (pair.First.Body.Tag == pair.Second.Body.Tag)
                    {
                        Face source = SmallerFace(pair.First, pair.Second);
                        Dictionary<Tag, Face> bodyFaces;
                        if (!sameBodyFaces.TryGetValue(pair.First.Body.Tag, out bodyFaces))
                        {
                            bodyFaces = new Dictionary<Tag, Face>();
                            sameBodyFaces.Add(pair.First.Body.Tag, bodyFaces);
                        }
                        bodyFaces[source.Tag] = source;
                    }
                    else
                    {
                        string key = pair.First.Body.Tag + "|" + pair.Second.Body.Tag;
                        List<GapPair> pairs;
                        if (!crossBodyPairs.TryGetValue(key, out pairs))
                        {
                            pairs = new List<GapPair>();
                            crossBodyPairs.Add(key, pairs);
                        }
                        pairs.Add(pair);
                    }
                }
            }

            foreach (List<GapPair> pairs in crossBodyPairs.Values) SewFaces(pairs);
            foreach (KeyValuePair<Tag, Dictionary<Tag, Face>> item in sameBodyFaces)
                DeleteAndHealFaces(item.Value.Values.ToArray());
        }

        private static Face SmallerFace(FaceInfo first, FaceInfo second)
        {
            return ApproximateFaceSize(first.Box) <= ApproximateFaceSize(second.Box) ? first.Face : second.Face;
        }

        private static double ApproximateFaceSize(double[] box)
        {
            double x = Math.Max(0.0, box[3] - box[0]);
            double y = Math.Max(0.0, box[4] - box[1]);
            double z = Math.Max(0.0, box[5] - box[2]);
            return x * y + y * z + z * x;
        }

        private void SewFaces(IList<GapPair> pairs)
        {
            Face[] targets = pairs.Select(pair => pair.First.Face).GroupBy(face => face.Tag)
                .Select(group => group.First()).ToArray();
            Face[] tools = pairs.Select(pair => pair.Second.Face).GroupBy(face => face.Tag)
                .Select(group => group.First()).ToArray();
            if (targets.Length == 0 || tools.Length == 0) return;

            SewBuilder builder = null;
            try
            {
                builder = context.WorkPart.Features.CreateSewBuilder(null);
                builder.Type = SewBuilder.Types.Solid;
                builder.Tolerance = activeGap;
                builder.BodyPreference = SewBuilder.BodyPreferenceTypes.Solid;
                builder.IsCommonFacesSearched = true;
                builder.OptimizeFaces = true;
                builder.TargetFaces.Add(targets);
                builder.ToolFaces.Add(tools);
                builder.CommitFeature();
            }
            finally
            {
                if (builder != null) builder.Destroy();
            }
        }

        private void DeleteAndHealFaces(Face[] facesToHeal)
        {
            if (facesToHeal == null || facesToHeal.Length == 0) return;
            DeleteFaceBuilder builder = null;
            try
            {
                builder = context.WorkPart.Features.CreateDeleteFaceBuilder(null);
                builder.Type = DeleteFaceBuilder.SelectTypes.Face;
                builder.Heal = true;
                FaceDumbRule rule = context.WorkPart.ScRuleFactory.CreateRuleFaceDumb(facesToHeal);
                builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                builder.CommitFeature();
            }
            finally
            {
                if (builder != null) builder.Destroy();
            }
        }

        private int Error(Exception ex)
        {
            scanCurrent = false;
            gapTimer.Stop();
            previewTimer.Stop();
            previewValid = false;
            ClearPreview();
            context.Log(ex.ToString());
            return 1;
        }

        public void Dispose()
        {
            gapTimer.Stop();
            gapTimer.Dispose();
            previewTimer.Stop();
            previewTimer.Dispose();
            try { ClearPreview(); }
            catch (Exception ex) { context.Log("Repair Unattached Faces preview cleanup: " + ex); }
            try { context.UI.SelectionManager.ClearGlobalSelectionList(); }
            catch (Exception ex) { context.Log("Repair Unattached Faces selection cleanup: " + ex); }
            try { dialog.Dispose(); }
            finally
            {
                try { context.UI.SelectionManager.ClearGlobalSelectionList(); }
                catch (Exception ex) { context.Log("Repair Unattached Faces selection cleanup: " + ex); }
            }
        }

        private sealed class FaceInfo
        {
            public FaceInfo(Body body, Face face, double[] box, double[] point, double[] normal)
            {
                Body = body;
                Face = face;
                Box = box;
                Point = point;
                Normal = normal;
                IsPlanar = face.SolidFaceType == Face.FaceType.Planar;
                Center = new[] { (box[0] + box[3]) * 0.5, (box[1] + box[4]) * 0.5, (box[2] + box[5]) * 0.5 };
                Adjacent = new HashSet<Tag>();
            }

            public Body Body { get; private set; }
            public Face Face { get; private set; }
            public double[] Box { get; private set; }
            public double[] Point { get; private set; }
            public double[] Normal { get; private set; }
            public bool IsPlanar { get; private set; }
            public double[] Center { get; private set; }
            public HashSet<Tag> Adjacent { get; private set; }
        }

        private sealed class GapPair
        {
            public GapPair(FaceInfo first, FaceInfo second, double distance, double[] pointFirst, double[] pointSecond)
            {
                First = first;
                Second = second;
                Distance = distance;
                PointFirst = pointFirst;
                PointSecond = pointSecond;
            }

            public FaceInfo First { get; private set; }
            public FaceInfo Second { get; private set; }
            public double Distance { get; private set; }
            public double[] PointFirst { get; private set; }
            public double[] PointSecond { get; private set; }
        }

        private sealed class GapGroup
        {
            public GapGroup(Face[] faces, GapPair[] pairs)
            {
                Faces = faces;
                Pairs = pairs;
            }

            public Face[] Faces { get; private set; }
            public GapPair[] Pairs { get; private set; }
        }

        private sealed class UnionFind
        {
            private readonly int[] parent;
            private readonly byte[] rank;

            public UnionFind(int count)
            {
                parent = Enumerable.Range(0, count).ToArray();
                rank = new byte[count];
            }

            public int Find(int value)
            {
                if (parent[value] == value) return value;
                parent[value] = Find(parent[value]);
                return parent[value];
            }

            public void Join(int first, int second)
            {
                int rootFirst = Find(first);
                int rootSecond = Find(second);
                if (rootFirst == rootSecond) return;
                if (rank[rootFirst] < rank[rootSecond]) parent[rootFirst] = rootSecond;
                else if (rank[rootFirst] > rank[rootSecond]) parent[rootSecond] = rootFirst;
                else
                {
                    parent[rootSecond] = rootFirst;
                    rank[rootFirst]++;
                }
            }
        }
    }
}
