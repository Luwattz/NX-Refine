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
using SelectObject = NXOpen.BlockStyler.SelectObject;

namespace NXRefine.UI
{
    // Native Block Styler workflow for selecting bodies, recognizing hole
    // faces with local proofs and NX's native Hole Faces fallback, reviewing regions,
    // excluding connected groups, and healing the exact retained faces on
    // Apply/OK.
    internal sealed class FillHolesDialog : IDisposable
    {
        private readonly NxContext context;
        private readonly CleanupSettings settings;
        private readonly BlockDialog dialog;
        private readonly System.Windows.Forms.Timer radiusTimer;
        private readonly System.Windows.Forms.Timer previewTimer;
        private SelectObject bodySelect;
        private StringBlock maxRadius;
        private FaceCollector faceSelect;
        private string radiusText;
        private readonly Dictionary<Tag, Body> bodies = new Dictionary<Tag, Body>();
        private readonly List<Face[]> groups = new List<Face[]>();
        private readonly HashSet<Tag> retained = new HashSet<Tag>();
        private double activeMaxRadius;
        private bool previewValid;
        private bool radiusEditPending;
        private bool updating;
        private bool ready;
        private SelectionScan activeDeleteScan;
        private volatile int deleteSucceeded;
        private Dictionary<Tag, int> validatedBodyFaults;
        private Dictionary<Tag, double[][]> rimSampleCache;
        private FaceSurfaceSampler.Counters surfaceSampleCounters;
        private Dictionary<Tag,Face.FaceType> scanFaceTypes;
        private Dictionary<Tag,Edge[]> scanEdges;
        private Dictionary<Tag,Face[]> scanNeighbors;

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
            radiusEditPending = false;
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
                if (blockName == "radiusInput")
                {
                    if (radiusEditPending) return 0;
                    if (previewValid && ReadMaximumRadius() == activeMaxRadius) return 0;
                    RebuildCandidates();
                }
                else
                {
                    if (radiusEditPending && !CommitRadiusInput()) return 0;
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
            groups.Clear();
            retained.Clear();
            if (faceSelect != null) faceSelect.SetSelectedObjects(new TaggedObject[0]);
            activeMaxRadius = ReadMaximumRadius();
            settings.FillHolesMaxRadius = activeMaxRadius;
            settings.Save();
            foreach (Body body in bodies.Values)
                groups.AddRange(FindGroups(body, activeMaxRadius));
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
            if (!ready || updating) return 0;
            // Enter is reported without changing the text. Treat that
            // unchanged callback as an explicit commit request and defer one
            // UI tick so the native edit can settle.
            if (radiusText == uncommittedValue)
            {
                if (radiusEditPending) ScheduleRadiusCommit();
                return 0;
            }
            radiusText = uncommittedValue;
            radiusEditPending = true;
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
            if (!ready || updating || radiusEditPending || !previewValid || radiusTimer.Enabled || retained.Count == 0) return false;
            try { return ReadMaximumRadius() == activeMaxRadius; }
            catch (InvalidOperationException) { return false; }
        }

        private bool CommitRadiusInput()
        {
            if (!radiusEditPending) return true;
            try { ReadMaximumRadius(); }
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

        private Face[][] FindGroups(Body body, double maximumRadius)
        {
            rimSampleCache = new Dictionary<Tag, double[][]>();
            surfaceSampleCounters = new FaceSurfaceSampler.Counters();
            scanFaceTypes=new Dictionary<Tag,Face.FaceType>();
            scanEdges=new Dictionary<Tag,Edge[]>();
            scanNeighbors=new Dictionary<Tag,Face[]>();
            try { return FindGroupsCore(body, maximumRadius); }
            finally
            {
                context.Log("Fill Holes surface sampling: "+surfaceSampleCounters.BatchCalls+" batches / "+
                    surfaceSampleCounters.BatchPoints+" points; "+surfaceSampleCounters.FallbackFaces+" legacy fallback faces.");
                rimSampleCache = null;surfaceSampleCounters=null;
                scanFaceTypes=null;scanEdges=null;scanNeighbors=null;
            }
        }

        // These snapshots exist only during one read-only body scan. Never
        // reuse them across healing, Undo, a body change, or another scan.
        private Face.FaceType FaceTypeOf(Face face)
        {
            if(scanFaceTypes==null)return face.SolidFaceType;
            Face.FaceType value;
            if(!scanFaceTypes.TryGetValue(face.Tag,out value))scanFaceTypes[face.Tag]=value=face.SolidFaceType;
            return value;
        }

        private Edge[] EdgesOf(Face face)
        {
            if(scanEdges==null)return face.GetEdges();
            Edge[] value;
            if(!scanEdges.TryGetValue(face.Tag,out value))scanEdges[face.Tag]=value=face.GetEdges();
            return value;
        }

        private Face[] NeighborsOf(Edge edge)
        {
            if(scanNeighbors==null)return edge.GetFaces();
            Face[] value;
            if(!scanNeighbors.TryGetValue(edge.Tag,out value))scanNeighbors[edge.Tag]=value=edge.GetFaces();
            return value;
        }

        private Face[][] FindGroupsCore(Body body, double maximumRadius)
        {
            Face[] allFaces = body.GetFaces();
            var bodyFaces = allFaces.ToDictionary(face => face.Tag);
            var result = new List<Face[]>();
            var seen = new HashSet<Tag>();
            int nonInnerSeeds = 0;
            int partialSeeds = 0;
            int nativeCalls = 0;
            int simpleGroups = 0;
            var scanClock = Stopwatch.StartNew();
            foreach (Face face in allFaces)
            {
                SelectionScan.Checkpoint();
                if (seen.Contains(face.Tag) || !IsHoleWallType(face)) continue;

                // Every accepted native region must contain this seed and
                // every analytic member must pass the same angular check
                // below. Reject an ineligible seed before the expensive rule;
                // do not relax the reviewed region or infer split hole walls.
                if (!HasFullAngularSpan(face))
                {
                    partialSeeds++;
                    continue;
                }
                HoleSeed eligible;
                if(!TryGetInnerRevolvedFace(face,body,out eligible)) { nonInnerSeeds++; continue; }
                if(maximumRadius>0 && eligible.Radius>maximumRadius)continue;

                // Prove a simple complete wall locally; complex regions use
                // the same native Hole Faces intent as NX's Delete Face.
                Face[] holeFaces;
                if (TryFindSimpleAnalyticHole(eligible, body, maximumRadius, out holeFaces)) simpleGroups++;
                else { nativeCalls++; holeFaces = FindNativeHoleFaces(face); }
                // Neither local nor native regions may escape the selected body.
                if (holeFaces.Any(item => item == null || !bodyFaces.ContainsKey(item.Tag))) continue;
                Face[] holeRegion = holeFaces.Where(item => item != null)
                    .GroupBy(item => item.Tag).Select(items => items.First()).ToArray();
                if (!holeRegion.Any(item => item.Tag == face.Tag) || holeRegion.Length >= allFaces.Length)
                    continue;
                // Hole Faces defines the reviewed deletion boundary. Boss/Pocket
                // describes a broader feature and can add ribs, carrier faces
                // and edge fillets even when its face count looks reasonable.
                // A successful heal is not evidence that those faces are holes.
                Face[] region = holeRegion;

                // Revalidate every analytic member, regardless of which path
                // established the complete region.
                HoleSeed seed = FindInnerSeed(holeRegion, body);
                if (seed == null)
                {
                    nonInnerSeeds++;
                    continue;
                }
                bool valid = true;
                foreach (Face item in region.Where(IsHoleWallType))
                {
                    SelectionScan.Checkpoint();
                    double radius = GetRevolvedMaximumRadius(item);
                    HoleSeed inner;
                    bool closed = HasFullAngularSpan(item);
                    if (!closed || radius <= 0.0 || (maximumRadius > 0.0 && radius > maximumRadius) ||
                        !TryGetInnerRevolvedFace(item, body, out inner) || !IsCoaxial(seed, inner))
                    {
                        valid = false;
                        break;
                    }
                }
                if (!valid) continue;

                // Native regions from different cylindrical/conical wall seeds can overlap.
                // Merge them so review exclusions always remove the whole hole.
                var merged = region.ToDictionary(item => item.Tag);
                for (int i = result.Count - 1; i >= 0; i--)
                {
                    SelectionScan.Checkpoint();
                    if (!result[i].Any(item => merged.ContainsKey(item.Tag))) continue;
                    foreach (Face item in result[i]) merged[item.Tag] = item;
                    result.RemoveAt(i);
                    i = result.Count; // Recheck earlier disjoint regions after the union grows.
                }
                Face[] group = merged.Values.ToArray();
                result.Add(group);
                foreach (Face item in group) seen.Add(item.Tag);
            }
            long primaryMilliseconds = scanClock.ElapsedMilliseconds;
            foreach(Face[] recovered in FindCompositeHoleGroupsCore(allFaces,body,maximumRadius,seen))
            {
                var merged=recovered.ToDictionary(face=>face.Tag);
                for(int i=result.Count-1;i>=0;i--)
                {
                    if(!result[i].Any(face=>merged.ContainsKey(face.Tag))) continue;
                    foreach(Face face in result[i]) merged[face.Tag]=face;
                    result.RemoveAt(i); i=result.Count;
                }
                result.Add(merged.Values.ToArray());
            }
            context.Log("Fill Holes: " + result.Count + " reviewed hole groups in body " + body.Tag +
                "; rejected " + nonInnerSeeds + " regions without an inner seed; skipped " +
                partialSeeds + " partial-angle seeds before recognition; " + nativeCalls +
                " primary native rules; " + simpleGroups + " local analytic groups; primary=" + primaryMilliseconds +
                " ms; composite=" + (scanClock.ElapsedMilliseconds-primaryMilliseconds) + " ms; total=" + scanClock.ElapsedMilliseconds + " ms.");
            return result.ToArray();
        }

        private bool TryFindSimpleAnalyticHole(HoleSeed seed, Body body, double maximumRadius, out Face[] region)
        {
            region = null;
            var walls = new Dictionary<Tag, Face> { { seed.Face.Tag, seed.Face } };
            double radius = seed.Radius;
            foreach (Face neighbor in EdgesOf(seed.Face).SelectMany(edge => NeighborsOf(edge)))
            {
                SelectionScan.Checkpoint();
                if (walls.ContainsKey(neighbor.Tag) || !IsHoleWallType(neighbor) || !HasFullAngularSpan(neighbor)) continue;
                HoleSeed inner;
                if (!TryGetInnerRevolvedFace(neighbor, body, out inner) || !IsCoaxial(seed, inner) ||
                    (maximumRadius > 0 && inner.Radius > maximumRadius)) continue;
                // Keep multistep/coaxial wall grouping under native authority;
                // touching revolved faces can belong to separately reviewed holes.
                return false;
            }
            var guide = new HoleSeed(seed.Face, seed.AxisPoint, seed.Axis, radius);
            Edge[] rims = RegionBoundary(walls.Values.ToArray());
            if (!HasWindingRims(rims, guide, false)) return false;
            var rimTags = new HashSet<Tag>(rims.Select(edge => edge.Tag));
            var curves = new Dictionary<Tag, double[][]>();
            foreach (Face cap in rims.SelectMany(edge => NeighborsOf(edge)).Where(face => !walls.ContainsKey(face.Tag))
                .GroupBy(face => face.Tag).Select(group => group.First()).ToArray())
            {
                Edge[] edges = EdgesOf(cap);
                if (edges.Length > 0 && edges.All(edge => rimTags.Contains(edge.Tag)) &&
                    WithinCylinder(cap, guide, radius, curves) && HasWindingRims(RegionBoundary(new[] { cap }), guide, false, 1, 1))
                    walls[cap.Tag] = cap;
            }
            // Every remaining rim must open away from the bore interior.
            // A bottom fillet points radially and an unresolved floor points
            // inward along the axis; both require native recognition instead.
            Edge[] boundary = RegionBoundary(walls.Values.ToArray());
            foreach (Edge edge in boundary)
                foreach (Face carrier in NeighborsOf(edge).Where(face => !walls.ContainsKey(face.Tag)))
                    if (!IsOpeningRim(edge, carrier, guide)) return false;
            if (!HasWindingRims(boundary, guide, false, 1) || IsUnsafeExpansion(walls.Count, body.GetFaces().Length)) return false;
            region = walls.Values.ToArray();
            return true;
        }

        private bool IsOpeningRim(Edge edge, Face carrier, HoleSeed seed)
        {
            double[][] samples;
            if (rimSampleCache == null || !rimSampleCache.TryGetValue(edge.Tag, out samples)) return false;
            double axisLength = Math.Sqrt(seed.Axis.Sum(value => value * value));
            try
            {
                foreach (int index in new[] { 8, 16, 24 })
                {
                    double[] uv = new double[2], point = new double[3], normal = new double[3];
                    context.UF.Modl.AskFaceParm(carrier.Tag, samples[index], uv, point);
                    context.UF.Modl.AskFaceProps(carrier.Tag, uv, point, new double[3], new double[3],
                        new double[3], new double[3], normal, new double[2]);
                    double along = Enumerable.Range(0, 3).Sum(i => (point[i] - seed.AxisPoint[i]) * seed.Axis[i]) / axisLength;
                    double axialNormal = Enumerable.Range(0, 3).Sum(i => normal[i] * seed.Axis[i]) / axisLength;
                    if (along * axialNormal <= 1e-7 || Math.Abs(axialNormal) < 0.25 * Math.Sqrt(normal.Sum(value => value * value))) return false;
                }
                return true;
            }
            catch (NXException) { return false; }
        }

        private static bool IsUnsafeExpansion(int regionFaceCount, int bodyFaceCount)
        {
            if (bodyFaceCount <= 0 || regionFaceCount >= bodyFaceCount) return true;
            // Reject at least 80% of any body, and preserve the existing
            // large-model budget that rejects broad expansions above 128
            // faces once they exceed one fifth of the body.
            long region = regionFaceCount;
            long body = bodyFaceCount;
            return region * 5 >= body * 4 || (regionFaceCount > 128 && region * 5 > body);
        }

        private Face[] FindNativeHoleFaces(Face seed)
        {
            ScCollector collector = context.WorkPart.ScCollectors.CreateCollector();
            // NX 2512 dereferences this parameter in CreateRuleFaceHole:
            // passing null raises an internal memory-access NXException.
            SelectionIntentRuleOptions options = context.WorkPart.ScRuleFactory.CreateRuleOptions();
            var clock = Stopwatch.StartNew();
            try
            {
                FaceHoleFacesRule rule = context.WorkPart.ScRuleFactory.CreateRuleFaceHole(seed, options);
                collector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                return collector.GetObjects().OfType<Face>()
                    .GroupBy(face => face.Tag).Select(group => group.First()).ToArray();
            }
            catch (NXException ex)
            {
                context.Log("Fill Holes native hole-face recognition failed for " + seed.Tag + ": " + ex.Message);
                return new Face[0];
            }
            finally
            {
                collector.Destroy();
                if (clock.ElapsedMilliseconds >= 1000)
                    context.Log("Fill Holes slow native recognition: seed=" + seed.Tag +
                        ", " + clock.ElapsedMilliseconds + " ms.");
            }
        }

        private Face[] ExpandNativeBossPocketFaces(Face seed)
        {
            ScCollector collector = context.WorkPart.ScCollectors.CreateCollector();
            try
            {
                FaceBossPocketFacesRule rule =
                    context.WorkPart.ScRuleFactory.CreateRuleFaceBossPocket(seed, false);
                collector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                return collector.GetObjects().OfType<Face>()
                    .GroupBy(face => face.Tag).Select(group => group.First()).ToArray();
            }
            catch (NXException ex)
            {
                context.Log("Fill Holes native expansion failed for " + seed.Tag + ": " + ex.Message);
                return new Face[0];
            }
            finally { collector.Destroy(); }
        }

        private HoleSeed FindInnerSeed(IEnumerable<Face> faces, Body body)
        {
            foreach (Face face in faces)
            {
                SelectionScan.Checkpoint();
                HoleSeed seed;
                if (TryGetInnerRevolvedFace(face, body, out seed)) return seed;
            }
            return null;
        }

        private bool IsHoleWallType(Face face)
        {
            return face != null && (FaceTypeOf(face) == Face.FaceType.Cylindrical ||
                FaceTypeOf(face) == Face.FaceType.Conical);
        }

        private bool HasFullAngularSpan(Face face)
        {
            // NX can return a concave edge fillet as a singleton Hole Faces
            // rule. Its inward normal and empty axis also pass the inner-wall
            // checks. Analytic cylinders/cones use U as the angular parameter;
            // require a full revolution to exclude these open circular arcs.
            // Split or interrupted hole walls are deliberately not inferred.
            try
            {
                double[] uv = new double[4];
                context.UF.Modl.AskFaceUvMinmax(face.Tag, uv);
                double span = uv[1] - uv[0];
                return !double.IsNaN(span) && !double.IsInfinity(span) &&
                    Math.Abs(span - 2.0 * Math.PI) <= 1e-6;
            }
            catch (NXException) { return false; }
        }

        private double GetRevolvedMaximumRadius(Face face)
        {
            if (!IsHoleWallType(face)) return 0.0;
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
                if (radius <= 0.0 || double.IsNaN(radius) || double.IsInfinity(radius)) return 0.0;
                if (FaceTypeOf(face) == Face.FaceType.Cylindrical) return radius;

                double axisLength = Math.Sqrt(axisDirection.Sum(value => value * value));
                if (axisLength < 1e-12 || double.IsNaN(radiusData) || double.IsInfinity(radiusData)) return 0.0;
                double[] uv = new double[4];
                context.UF.Modl.AskFaceUvMinmax(face.Tag, uv);
                double maximum = 0.0;
                // Cone radius depends only on its axial parameter. Evaluate
                // the trimmed V extremes on the actual surface; projecting a
                // world-axis box overestimates tilted entrance chamfers.
                foreach (double v in new[] { uv[2], uv[3] })
                {
                    double[] p = SurfacePoint(face, (uv[0] + uv[1]) * 0.5, v);
                    double along = 0.0, distance = 0.0;
                    for (int i = 0; i < 3; i++) { double d=p[i]-axisPoint[i]; along+=d*axisDirection[i]/axisLength; distance+=d*d; }
                    maximum = Math.Max(maximum, Math.Sqrt(Math.Max(0.0, distance-along*along)));
                }
                return maximum > 0.0 && !double.IsNaN(maximum) && !double.IsInfinity(maximum)
                    ? maximum : 0.0;
            }
            catch (NXException) { return 0.0; }
        }

        private double[] SurfacePoint(Face face, double u, double v)
        {
            double[] point = new double[3];
            context.UF.Modl.AskFaceProps(face.Tag, new[] { u, v }, point, new double[3], new double[3],
                new double[3], new double[3], new double[3], new double[2]);
            return point;
        }

        private bool HasWindingRims(Edge[] boundary, HoleSeed seed, bool constantRadius = true, int minimumLoops = 2, int maximumLoops = 2)
        {
            if(boundary.Length==0) return false;
            double tolerance=1e-5*Math.Max(1,seed.Radius);
            double partTolerance;
            context.UF.Modl.AskDistanceToleranceOfPart(context.WorkPart.Tag,out partTolerance);
            // Imported edge curves may end slightly away from their common
            // topological vertex. Use the part tolerance only to join them;
            // keep the radius bound independent of this stitching allowance.
            double joinTolerance=Math.Max(tolerance,Math.Min(0.01,partTolerance));
            double length=Math.Sqrt(seed.Axis.Sum(x=>x*x));
            var axis=seed.Axis.Select(x=>x/length).ToArray();
            double[] helper=Math.Abs(axis[2])<0.9?new[]{0.0,0.0,1.0}:new[]{1.0,0.0,0.0};
            double[] xAxis={axis[1]*helper[2]-axis[2]*helper[1],axis[2]*helper[0]-axis[0]*helper[2],axis[0]*helper[1]-axis[1]*helper[0]};
            double xLength=Math.Sqrt(xAxis.Sum(x=>x*x)); for(int i=0;i<3;i++) xAxis[i]/=xLength;
            double[] yAxis={axis[1]*xAxis[2]-axis[2]*xAxis[1],axis[2]*xAxis[0]-axis[0]*xAxis[2],axis[0]*xAxis[1]-axis[1]*xAxis[0]};
            var curves=new List<double[][]>();
            foreach(Edge edge in boundary)
            {
                SelectionScan.Checkpoint();
                double[][] cached;
                if (rimSampleCache != null && rimSampleCache.TryGetValue(edge.Tag, out cached))
                { curves.Add(cached); continue; }
                IntPtr evaluator=IntPtr.Zero;
                try
                {
                    context.UF.Eval.Initialize(edge.Tag,out evaluator);
                    double[] limits=new double[2]; context.UF.Eval.AskLimits(evaluator,limits);
                    var points=new List<double[]>();
                    for(int k=0;k<=32;k++)
                    {
                        var p=new double[3]; context.UF.Eval.Evaluate(evaluator,0,limits[0]+(limits[1]-limits[0])*k/32.0,p,new double[3]);
                        points.Add(p);
                    }
                    double[][] samples = points.ToArray();
                    curves.Add(samples);
                    if (rimSampleCache != null) rimSampleCache[edge.Tag] = samples;
                }
                catch(NXException){return false;}
                finally {if(evaluator!=IntPtr.Zero) context.UF.Eval.Free(evaluator);}
            }
            int loops=0;
            while(curves.Count>0)
            {
                var path=new List<double[]>(curves[0]); curves.RemoveAt(0);
                while(DistanceSquared(path[0],path[path.Count-1])>joinTolerance*joinTolerance)
                {
                    var matches=curves.Select((samples,index)=>new{points=samples,index})
                        .Where(item=>DistanceSquared(path[path.Count-1],item.points[0])<=joinTolerance*joinTolerance ||
                            DistanceSquared(path[path.Count-1],item.points[item.points.Length-1])<=joinTolerance*joinTolerance).ToArray();
                    if(matches.Length!=1) return false;
                    var match=matches[0]; var points=match.points;
                    if(DistanceSquared(path[path.Count-1],points[0])>joinTolerance*joinTolerance) points=points.Reverse().ToArray();
                    path.AddRange(points.Skip(1)); curves.RemoveAt(match.index);
                }
                path.Add(path[0]);
                double winding=0, previous=0; bool first=true;
                foreach(double[] p in path)
                {
                    double x=0,y=0;
                    for(int i=0;i<3;i++){x+=(p[i]-seed.AxisPoint[i])*xAxis[i]; y+=(p[i]-seed.AxisPoint[i])*yAxis[i];}
                    double radial=Math.Sqrt(x*x+y*y);
                    if(constantRadius ? Math.Abs(radial-seed.Radius)>tolerance*10 : radial>seed.Radius+tolerance*10 || radial<tolerance) return false;
                    double angle=Math.Atan2(y,x);
                    if(!first){double delta=angle-previous; while(delta>Math.PI)delta-=2*Math.PI; while(delta< -Math.PI)delta+=2*Math.PI;
                        if(Math.Abs(delta)>Math.PI/2) return false; winding+=delta;}
                    first=false; previous=angle;
                }
                if(Math.Abs(Math.Abs(winding)-2*Math.PI)>1e-3 || ++loops>maximumLoops) return false;
            }
            return loops>=minimumLoops && loops<=maximumLoops;
        }

        private Face[][] FindCompositeHoleGroups(Face[] allFaces, Body body, double maximumRadius)
        {
            return FindCompositeHoleGroupsCore(allFaces, body, maximumRadius, new HashSet<Tag>());
        }

        private Face[][] FindCompositeHoleGroupsCore(Face[] allFaces, Body body, double maximumRadius, HashSet<Tag> knownFaces)
        {
            var compositeClock = Stopwatch.StartNew();
            var result=new List<Face[]>();
            var processed=new HashSet<Tag>(knownFaces);
            var curves=new Dictionary<Tag,double[][]>();
            var nativeCache=new Dictionary<Tag,Face[]>();
            var cachedPoints=new Dictionary<Tag,double[]>();
            var cachedNormals=new Dictionary<Tag,double[]>();
            var invalidNormals=new HashSet<Tag>();
            double tolerance; context.UF.Modl.AskDistanceToleranceOfPart(context.WorkPart.Tag,out tolerance);
            tolerance=Math.Max(1e-5,Math.Min(tolerance,0.01));
            var seeds=new List<HoleSeed>();
            foreach(Face face in allFaces)
            {
                SelectionScan.Checkpoint();
                HoleSeed seed;
                if(FaceTypeOf(face)==Face.FaceType.Planar)
                {
                    var bounds=new double[6];context.UF.Modl.AskBoundingBox(face.Tag,bounds);
                    if(maximumRadius>0 && Enumerable.Range(0,3).Sum(i=>Math.Pow(bounds[i+3]-bounds[i],2))>12*maximumRadius*maximumRadius)continue;
                    if(TryGetCircularFloor(face,tolerance,curves,out seed)) seeds.Add(seed);
                }
                else if(FaceTypeOf(face)==Face.FaceType.Cylindrical && !HasFullAngularSpan(face) && TryGetRevolvedWallCore(face,body,out seed,false)) seeds.Add(seed);
                else if(!IsHoleWallType(face) && FaceTypeOf(face)!=Face.FaceType.Spherical && EdgesOf(face).Length<=8 &&
                    TryFitWallForSearch(face,body,tolerance,maximumRadius,out seed)) seeds.Add(seed);
            }
            double[] bodyBox=new double[6]; context.UF.Modl.AskBoundingBox(body.Tag,bodyBox);
            long seedMilliseconds = compositeClock.ElapsedMilliseconds;
            double bodyLimit=Math.Sqrt(Enumerable.Range(0,3).Sum(i=>Math.Pow(bodyBox[i+3]-bodyBox[i],2)));
            var fittedWalls=seeds.Where(s=>FaceTypeOf(s.Face)!=Face.FaceType.Planar).ToDictionary(s=>s.Face.Tag);
            foreach(HoleSeed seed in seeds)
            {
                Face face=seed.Face;
                if(processed.Contains(face.Tag) || (maximumRadius>0 && seed.Radius>maximumRadius)) continue;
                // Limit fallback growth around the observed bore, rather than
                // letting a tiny corner fillet absorb a whole nearby pocket.
                // Larger analytic counterbores remain handled by NX above.
                double searchLimit=Math.Min(maximumRadius>0?maximumRadius:bodyLimit,2*seed.Radius+tolerance);
                var walls=new Dictionary<Tag,Face>{{face.Tag,face}};
                var queue=new Queue<Face>(); queue.Enqueue(face);
                while(queue.Count>0 && walls.Count<=128)
                {
                    foreach(Face neighbor in EdgesOf(queue.Dequeue()).SelectMany(e=>NeighborsOf(e)))
                    {
                        if(walls.ContainsKey(neighbor.Tag)) continue;
                        double[] point,normal;
                        if(invalidNormals.Contains(neighbor.Tag))continue;
                        if(!cachedPoints.TryGetValue(neighbor.Tag,out point))
                        {
                            if(!TryGetFacePointNormal(neighbor,out point,out normal)){invalidNormals.Add(neighbor.Tag);continue;}
                            cachedPoints[neighbor.Tag]=point;cachedNormals[neighbor.Tag]=normal;
                        }
                        normal=cachedNormals[neighbor.Tag];
                        if(FaceTypeOf(neighbor)==Face.FaceType.Planar && Math.Abs(normal.Select((x,i)=>x*seed.Axis[i]).Sum())>
                            0.5*Math.Sqrt(normal.Sum(x=>x*x)*seed.Axis.Sum(x=>x*x)) &&
                            WithinCylinder(neighbor,seed,searchLimit+tolerance,curves) &&
                            HasWindingRims(RegionBoundary(new[]{neighbor}),new HoleSeed(face,seed.AxisPoint,seed.Axis,searchLimit+tolerance),false,1,1))continue;
                        double along=0,length=seed.Axis.Sum(x=>x*x); var radial=new double[3];
                        for(int i=0;i<3;i++) along+=(point[i]-seed.AxisPoint[i])*seed.Axis[i]/length;
                        for(int i=0;i<3;i++) radial[i]=point[i]-seed.AxisPoint[i]-along*seed.Axis[i];
                        if(radial.Select((x,i)=>-x*normal[i]).Sum()<=1e-7 || Math.Sqrt(radial.Sum(x=>x*x))>searchLimit+tolerance) continue;
                        if(!WithinCylinder(neighbor,seed,searchLimit+tolerance,curves)) continue;
                        HoleSeed fittedNeighbor;
                        if(fittedWalls.TryGetValue(neighbor.Tag,out fittedNeighbor) && !IsCoaxial(seed,fittedNeighbor,tolerance) &&
                            IsVoidSeed(fittedNeighbor,body))continue;
                        walls[neighbor.Tag]=neighbor; queue.Enqueue(neighbor);
                    }
                }
                if(walls.Count<2 || walls.Count>128) continue;
                var guide=new HoleSeed(face,seed.AxisPoint,seed.Axis,searchLimit+tolerance);
                if(!HasWindingRims(RegionBoundary(walls.Values.ToArray()),guide,false,FaceTypeOf(face)==Face.FaceType.Planar?1:2))
                {
                    if(allFaces.Length<100)context.Log("Fill Holes rejected open guide: seed="+face.Tag+"; walls="+string.Join(",",walls.Keys));
                    continue;
                }
                if (FaceTypeOf(face) != Face.FaceType.Planar && !IsVoidSeed(seed,body)) continue;
                // Oblique blind-hole disks are bounded by a proven wall rim;
                // annular carrier faces have additional edges and are rejected.
                var rimTags=new HashSet<Tag>(RegionBoundary(walls.Values.ToArray()).Select(e=>e.Tag));
                foreach(Face cap in walls.Values.SelectMany(f=>EdgesOf(f)).SelectMany(e=>NeighborsOf(e))
                    .Where(f=>FaceTypeOf(f)==Face.FaceType.Planar && !walls.ContainsKey(f.Tag)).GroupBy(f=>f.Tag).Select(g=>g.First()).ToArray())
                {
                    Edge[] capEdges=EdgesOf(cap);
                    if(capEdges.Length==0 || capEdges.Any(e=>!rimTags.Contains(e.Tag)))continue;
                    if(!WithinCylinder(cap,seed,searchLimit+tolerance,curves) || !HasWindingRims(RegionBoundary(new[]{cap}),guide,false,1,1))continue;
                    walls[cap.Tag]=cap;
                }
                context.Log("Fill Holes composite guide: seed="+face.Tag+"; walls="+string.Join(",",walls.Keys));
                Face[] local = walls.Values.ToArray();
                // The rounded-cap signature is already a complete local region.
                // Reject it before an expensive whole-body native query, using
                // the same bore-wall requirement enforced again before deletion.
                if(local.Length==2 && local.Count(f=>FaceTypeOf(f)==Face.FaceType.Planar)==1 &&
                    local.Any(f=>FaceTypeOf(f)==Face.FaceType.SurfaceOfRevolution) &&
                    !HasRoundBoreWall(local,body,maximumRadius,seed,fittedWalls))
                {
                    context.Log("Fill Holes excluded rounded end before native recognition: "+string.Join(",",local.Select(f=>f.Tag)));
                    continue;
                }
                // A complete fitted bore needs no whole-body native rescan.
                // Unfitted transitions, faceted walls and uncertain opening
                // normals still use NX to supply/validate additional members.
                Face[] profileWalls = local.Where(f=>FaceTypeOf(f)!=Face.FaceType.Planar).ToArray();
                bool fittedRegion = profileWalls.Length > 0 && profileWalls.All(f=>
                {
                    HoleSeed fitted;
                    return fittedWalls.TryGetValue(f.Tag,out fitted) && IsCoaxial(seed,fitted,tolerance) && IsVoidSeed(fitted,body);
                });
                Edge[] openings = RegionBoundary(local);
                bool localOnly = fittedRegion && openings.All(edge=>NeighborsOf(edge)
                    .Where(f=>!walls.ContainsKey(f.Tag)).All(f=>IsOpeningRim(edge,f,guide)));
                Face[] native = local;
                if (!localOnly)
                {
                    Face[] recognized;
                    if(!nativeCache.TryGetValue(face.Tag,out recognized))
                    {recognized=FindNativeHoleFaces(face);nativeCache[face.Tag]=recognized;}
                    if(!recognized.Any(f=>f.Tag==face.Tag))continue;
                    native=local.Concat(recognized).GroupBy(f=>f.Tag).Select(g=>g.First()).ToArray();
                }
                var nativeTags=new HashSet<Tag>(native.Select(f=>f.Tag));
                if(IsUnsafeExpansion(native.Length,body.GetFaces().Length) || native.Any(f=>f.GetBody().Tag!=body.Tag) || walls.Keys.Any(tag=>!nativeTags.Contains(tag))) continue;
                double limit=searchLimit;
                if(native.Any(f=>!WithinCylinder(f,seed,limit,curves))) continue;
                if(native.Where(IsHoleWallType).Any(f=>{HoleSeed inner; return !TryGetInnerRevolvedFace(f,body,out inner)||!IsCoaxial(seed,inner)||GetRevolvedMaximumRadius(f)>limit;})) continue;
                var regionSeed=new HoleSeed(face,seed.AxisPoint,seed.Axis,limit);
                if(!HasWindingRims(RegionBoundary(native),regionSeed,false,1)) continue;
                // A circular disk and its rounded rib end can enclose the axis
                // without forming a bore. Healing that blend extends the rib
                // into a spike. Require a real inward cylindrical/conical wall.
                if (!HasRoundBoreWall(native,body,maximumRadius,seed,fittedWalls))
                {
                    context.Log("Fill Holes excluded rounded end without a bore wall: " + string.Join(",",native.Select(f=>f.Tag)));
                    continue;
                }
                foreach(Tag tag in nativeTags)processed.Add(tag);
                result.Add(native);
                context.Log("Fill Holes composite hole: radius="+seed.Radius+"; faces="+string.Join(",",native.Select(f=>f.Tag)));
            }
            context.Log("Fill Holes composite: " + seeds.Count + " fitted seeds in " + seedMilliseconds +
                " ms; regions in " + (compositeClock.ElapsedMilliseconds-seedMilliseconds) + " ms.");
            return result.ToArray();
        }

        private bool HasRoundBoreWall(Face[] region, Body body, double maximumRadius,
            HoleSeed axis = null, Dictionary<Tag,HoleSeed> fittedWalls = null)
        {
            double tolerance;
            context.UF.Modl.AskDistanceToleranceOfPart(context.WorkPart.Tag,out tolerance);
            tolerance=Math.Max(1e-5,Math.Min(tolerance,0.01));
            foreach (Face face in region)
            {
                SelectionScan.Checkpoint();
                if (FaceTypeOf(face) == Face.FaceType.Planar || FaceTypeOf(face) == Face.FaceType.Spherical) continue;
                HoleSeed wall;
                if (IsHoleWallType(face))
                {
                    if (!TryGetInnerRevolvedFace(face,body,out wall)) continue;
                }
                else if (fittedWalls != null && fittedWalls.TryGetValue(face.Tag,out wall))
                {
                    if (!IsVoidSeed(wall,body)) continue;
                }
                else if (!TryFitInnerRevolvedWall(face,body,tolerance,maximumRadius,out wall)) continue;
                if ((maximumRadius<=0 || wall.Radius<=maximumRadius) && (axis==null || IsCoaxial(axis,wall,tolerance))) return true;
            }
            return false;
        }

        private void RequireRoundBoreWall(Face[] reviewed)
        {
            var tags=new HashSet<Tag>(reviewed.Select(face=>face.Tag));
            Face[] live=context.WorkPart.Bodies.ToArray().SelectMany(body=>body.GetFaces()).Where(face=>tags.Contains(face.Tag)).ToArray();
            if(live.Length!=tags.Count)throw new InvalidOperationException("The reviewed hole faces are no longer present.");
            Body[] source=live.Select(face=>face.GetBody()).GroupBy(body=>body.Tag).Select(group=>group.First()).ToArray();
            if(source.Length!=1 || !HasRoundBoreWall(live,source[0],activeMaxRadius))
                throw new InvalidOperationException("The reviewed region has no cylindrical/conical bore wall; rounded rib ends cannot be filled as holes.");
        }

        private bool TryGetCircularFloor(Face face,double tolerance,Dictionary<Tag,double[][]> curves,out HoleSeed seed)
        {
            seed=null;
            int type,direction;double radius,data;var origin=new double[3];var axis=new double[3];
            context.UF.Modl.AskFaceData(face.Tag,out type,origin,axis,new double[6],out radius,out data,out direction);
            var provisional=new HoleSeed(face,origin,axis,1);
            if(!WithinCylinder(face,provisional,double.MaxValue,curves))return false;
            double[][] samples=EdgesOf(face).SelectMany(edge=>curves[edge.Tag]).ToArray();
            if(samples.Length<3)return false;
            double[] a=samples[0];double[] b=samples.OrderByDescending(p=>DistanceSquared(p,a)).First();
            double[] ab=b.Select((x,i)=>x-a[i]).ToArray();double abLength=Math.Sqrt(ab.Sum(x=>x*x));if(abLength<1e-6)return false;
            var xAxis=ab.Select(x=>x/abLength).ToArray();double axisLength=Math.Sqrt(axis.Sum(x=>x*x));if(axisLength<1e-9)return false;
            axis=axis.Select(x=>x/axisLength).ToArray();
            double[] yAxis={axis[1]*xAxis[2]-axis[2]*xAxis[1],axis[2]*xAxis[0]-axis[0]*xAxis[2],axis[0]*xAxis[1]-axis[1]*xAxis[0]};
            double[] c=samples.OrderByDescending(p=>Math.Abs(Enumerable.Range(0,3).Sum(i=>(p[i]-a[i])*yAxis[i]))).First();
            double bx=abLength,cx=Enumerable.Range(0,3).Sum(i=>(c[i]-a[i])*xAxis[i]),cy=Enumerable.Range(0,3).Sum(i=>(c[i]-a[i])*yAxis[i]);
            if(Math.Abs(cy)<1e-6)return false;
            double centerX=bx/2,centerY=(cx*cx+cy*cy-bx*cx)/(2*cy);
            var center=Enumerable.Range(0,3).Select(i=>a[i]+centerX*xAxis[i]+centerY*yAxis[i]).ToArray();
            double fitted=Math.Sqrt(centerX*centerX+centerY*centerY);
            if(fitted<=tolerance || samples.Any(p=>Math.Abs(Math.Sqrt(DistanceSquared(p,center))-fitted)>tolerance))return false;
            var candidate=new HoleSeed(face,center,axis,fitted+tolerance);
            if(!HasWindingRims(RegionBoundary(new[]{face}),candidate,false,1))return false;
            seed=new HoleSeed(face,center,axis,fitted);return true;
        }

        private bool TryFitInnerRevolvedWall(Face face,Body body,double tolerance,double maximumRadius,out HoleSeed seed)
        {
            try{return TryFitInnerRevolvedWallCore(face,body,tolerance,maximumRadius,out seed);}
            catch(NXException){seed=null;return false;}
        }

        private bool TryFitWallForSearch(Face face,Body body,double tolerance,double maximumRadius,out HoleSeed seed)
        {
            try{return TryFitInnerRevolvedWallCore(face,body,tolerance,maximumRadius,out seed,true);}
            catch(NXException){seed=null;return false;}
        }

        private bool TryFitInnerRevolvedWallCore(Face face,Body body,double tolerance,double maximumRadius,out HoleSeed seed, bool deferContainment = false)
        {
            using(var sampler=new FaceSurfaceSampler(context,face,surfaceSampleCounters))
                return FitRevolvedWallSamples(face,body,tolerance,maximumRadius,out seed,deferContainment,sampler);
        }

        private bool FitRevolvedWallSamples(Face face,Body body,double tolerance,double maximumRadius,out HoleSeed seed,
            bool deferContainment,FaceSurfaceSampler sampler)
        {
            seed=null;
            var candidates = new List<HoleSeed>();
            var uv=new double[4]; context.UF.Modl.AskFaceUvMinmax(face.Tag,uv);
            foreach(bool swapped in new[]{false,true})
            {
                var points=new double[21][];var normals=new double[21][];
                int[] initialIndices={8,10,12};
                double[][] initialParameters=initialIndices.Select(index=>
                {
                    int k=index-7;
                    double fu=swapped?0.5:k/6.0,fv=swapped?k/6.0:0.5;
                    return new[]{uv[0]+fu*(uv[1]-uv[0]),uv[2]+fv*(uv[3]-uv[2])};
                }).ToArray();
                double[][] sampledPoints,sampledNormals;
                sampler.Evaluate(initialParameters,out sampledPoints,out sampledNormals);
                for(int i=0;i<initialIndices.Length;i++){points[initialIndices[i]]=sampledPoints[i];normals[initialIndices[i]]=sampledNormals[i];}
                double[] a=normals[8].Select((x,i)=>x-normals[10][i]).ToArray(),b=normals[12].Select((x,i)=>x-normals[10][i]).ToArray();
                double[] axis={a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};
                double axisLength=Math.Sqrt(axis.Sum(x=>x*x));if(double.IsNaN(axisLength)||double.IsInfinity(axisLength)||axisLength<1e-6)continue;axis=axis.Select(x=>x/axisLength).ToArray();
                var origin=points[8];var delta=points[10].Select((x,i)=>x-origin[i]).ToArray();double along=delta.Select((x,i)=>x*axis[i]).Sum();
                var xAxis=delta.Select((x,i)=>x-along*axis[i]).ToArray();double xLength=Math.Sqrt(xAxis.Sum(x=>x*x));if(xLength<1e-6)continue;xAxis=xAxis.Select(x=>x/xLength).ToArray();
                double[] yAxis={axis[1]*xAxis[2]-axis[2]*xAxis[1],axis[2]*xAxis[0]-axis[0]*xAxis[2],axis[0]*xAxis[1]-axis[1]*xAxis[0]};
                double cx=Enumerable.Range(0,3).Sum(i=>(points[12][i]-origin[i])*xAxis[i]),cy=Enumerable.Range(0,3).Sum(i=>(points[12][i]-origin[i])*yAxis[i]);
                if(Math.Abs(cy)<1e-6)continue;
                double centerX=xLength/2,centerY=(cx*cx+cy*cy-xLength*cx)/(2*cy);
                var center=Enumerable.Range(0,3).Select(i=>origin[i]+centerX*xAxis[i]+centerY*yAxis[i]).ToArray();
                double estimate=Math.Sqrt(centerX*centerX+centerY*centerY);
                if(double.IsNaN(estimate)||double.IsInfinity(estimate)||(maximumRadius>0 && estimate>maximumRadius+tolerance))continue;
                int[] remaining=Enumerable.Range(0,21).Where(index=>points[index]==null).ToArray();
                double[][] remainingParameters=remaining.Select(index=>
                {
                    int j=index/7,k=index%7;
                    double fu=swapped?j/2.0:k/6.0,fv=swapped?k/6.0:j/2.0;
                    return new[]{uv[0]+fu*(uv[1]-uv[0]),uv[2]+fv*(uv[3]-uv[2])};
                }).ToArray();
                sampler.Evaluate(remainingParameters,out sampledPoints,out sampledNormals);
                for(int i=0;i<remaining.Length;i++){points[remaining[i]]=sampledPoints[i];normals[remaining[i]]=sampledNormals[i];}
                if(points.SelectMany(p=>p).Concat(normals.SelectMany(n=>n)).Any(x=>double.IsNaN(x)||double.IsInfinity(x)))continue;
                var stations=new double[21];var radii=new double[21];bool inward=true;
                for(int k=0;k<21;k++)
                {
                    var displacement=points[k].Select((x,i)=>x-center[i]).ToArray();stations[k]=displacement.Select((x,i)=>x*axis[i]).Sum();
                    var radial=displacement.Select((x,i)=>x-stations[k]*axis[i]).ToArray();radii[k]=Math.Sqrt(radial.Sum(x=>x*x));
                    double normalLength=Math.Sqrt(normals[k].Sum(x=>x*x));
                    if(radii[k]<1e-6 || normalLength<1e-9 || radial.Select((x,i)=>-x*normals[k][i]).Sum()<0.5*radii[k]*normalLength)inward=false;
                }
                if(!inward)continue;
                double stationMean=stations.Average(),radiusMean=radii.Average(),variance=stations.Sum(x=>(x-stationMean)*(x-stationMean));
                if(variance<1e-12)continue;
                double slope=Enumerable.Range(0,21).Sum(k=>(stations[k]-stationMean)*(radii[k]-radiusMean))/variance;
                if(Enumerable.Range(0,21).Any(k=>Math.Abs(radii[k]-radiusMean-slope*(stations[k]-stationMean))>tolerance))continue;
                double radius=radii.Max();if(maximumRadius>0 && radius>maximumRadius)continue;
                var stationPoint=center.Select((x,i)=>x+stationMean*axis[i]).ToArray();
                var candidate=new HoleSeed(face,center,axis,radius,stationPoint);
                if (deferContainment) { candidates.Add(candidate); continue; }
                if (!IsVoidSeed(candidate,body)) continue;
                seed=candidate;
                return true;
            }
            // If both parameter directions fit, resolve the original ordered
            // choice now. A single fit can defer the same point test until a
            // closed region (or an incompatible neighboring wall) needs it.
            if(candidates.Count==1){seed=candidates[0];return true;}
            foreach(HoleSeed candidate in candidates)
                if(IsVoidSeed(candidate,body)){seed=candidate;return true;}
            return false;
        }

        private bool IsVoidSeed(HoleSeed seed, Body body)
        {
            if (seed.VoidState.HasValue) return seed.VoidState.Value;
            try
            {
                int containment;
                context.UF.Modl.AskPointContainment(seed.ContainmentPoint,body.Tag,out containment);
                seed.VoidState = containment != 1;
            }
            catch (NXException) { seed.VoidState = false; }
            return seed.VoidState.Value;
        }

        private Edge[] RegionBoundary(Face[] faces)
        {
            var tags=new HashSet<Tag>(faces.Select(face=>face.Tag));
            return faces.SelectMany(f=>EdgesOf(f)).GroupBy(e=>e.Tag).Select(g=>g.First())
                // A periodic face's seam can report a single adjacent face.
                // It is internal, not a rim: a region boundary needs a face
                // outside the region on the other side of the edge.
                .Where(e=>NeighborsOf(e).Any(f=>!tags.Contains(f.Tag))).ToArray();
        }

        private bool WithinCylinder(Face face,HoleSeed seed,double maximum,Dictionary<Tag,double[][]> cache)
        {
            double axisLength=seed.Axis.Sum(x=>x*x);
            foreach(Edge edge in EdgesOf(face))
            {
                double[][] points;
                if(!cache.TryGetValue(edge.Tag,out points))
                {
                    IntPtr evaluator=IntPtr.Zero;
                    try
                    {
                        context.UF.Eval.Initialize(edge.Tag,out evaluator); var limits=new double[2]; context.UF.Eval.AskLimits(evaluator,limits);
                        points=new double[17][];
                        for(int k=0;k<points.Length;k++){points[k]=new double[3]; context.UF.Eval.Evaluate(evaluator,0,limits[0]+(limits[1]-limits[0])*k/16.0,points[k],new double[3]);}
                        cache[edge.Tag]=points;
                    }
                    catch(NXException){return false;}
                    finally{if(evaluator!=IntPtr.Zero)context.UF.Eval.Free(evaluator);}
                }
                foreach(double[] p in points)
                {
                    double along=0,distance=0;
                    for(int i=0;i<3;i++){double d=p[i]-seed.AxisPoint[i];along+=d*seed.Axis[i]/Math.Sqrt(axisLength);distance+=d*d;}
                    if(Math.Sqrt(Math.Max(0,distance-along*along))>maximum+1e-5) return false;
                }
            }
            return true;
        }

        private static double DistanceSquared(double[] a,double[] b)
        {return Enumerable.Range(0,3).Sum(i=>(a[i]-b[i])*(a[i]-b[i]));}

        private bool TryGetInnerRevolvedFace(Face face, Body body, out HoleSeed seed)
        {
            return TryGetRevolvedWallCore(face, body, out seed, true);
        }

        private bool TryGetRevolvedWallCore(Face face, Body body, out HoleSeed seed, bool requireVoid)
        {
            seed = null;
            if (!IsHoleWallType(face)) return false;
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
                double maximumRadius = GetRevolvedMaximumRadius(face);
                if (maximumRadius <= 0.0) return false;
                double[] surfacePoint;
                double[] surfaceNormal;
                if (!TryGetFacePointNormal(face, out surfacePoint, out surfaceNormal)) return false;
                double axisLengthSquared = axisDirection.Sum(value => value * value);
                if (axisLengthSquared < 1e-12) return false;
                double station = 0.0;
                for (int i = 0; i < 3; i++)
                    station += (surfacePoint[i] - axisPoint[i]) * axisDirection[i];
                for (int i = 0; i < 3; i++)
                    axisPoint[i] += station * axisDirection[i] / axisLengthSquared;
                double towardAxis = (axisPoint[0] - surfacePoint[0]) * surfaceNormal[0] +
                    (axisPoint[1] - surfacePoint[1]) * surfaceNormal[1] +
                    (axisPoint[2] - surfacePoint[2]) * surfaceNormal[2];
                // Solid-face normals point out of the material. Inner
                // cylindrical and conical walls point toward their axis;
                // exterior shafts point away and are rejected.
                if (towardAxis <= 1e-7) return false;
                // The local axis point is in the void for an inner revolved
                // wall; an exterior boss has its axis point inside the solid.
                seed = new HoleSeed(face, axisPoint, axisDirection, maximumRadius);
                if (requireVoid && !IsVoidSeed(seed,body)) { seed=null; return false; }
                return true;
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

        private static bool IsCoaxial(HoleSeed first, HoleSeed second, double fittingTolerance = 0.0)
        {
            double firstLength = Math.Sqrt(first.Axis.Sum(value => value * value));
            double secondLength = Math.Sqrt(second.Axis.Sum(value => value * value));
            double dot = 0.0;
            double along = 0.0;
            double distanceSquared = 0.0;
            for (int i = 0; i < 3; i++)
            {
                SelectionScan.Checkpoint();
                dot += first.Axis[i] * second.Axis[i] / (firstLength * secondLength);
                double delta = second.AxisPoint[i] - first.AxisPoint[i];
                along += delta * first.Axis[i] / firstLength;
                distanceSquared += delta * delta;
            }
            double tolerance = Math.Max(fittingTolerance,1e-5 * Math.Max(1.0, Math.Max(first.Radius, second.Radius)));
            return Math.Abs(dot) >= 1.0 - 1e-8 &&
                Math.Max(0.0, distanceSquared - along * along) <= tolerance * tolerance;
        }

        private sealed class HoleSeed
        {
            public HoleSeed(Face face, double[] axisPoint, double[] axis, double radius, double[] containmentPoint = null)
            {
                Face = face;
                AxisPoint = axisPoint;
                Axis = axis;
                Radius = radius;
                ContainmentPoint = containmentPoint ?? axisPoint;
            }

            public Face Face { get; private set; }
            public double[] AxisPoint { get; private set; }
            public double[] Axis { get; private set; }
            public double Radius { get; private set; }
            public double[] ContainmentPoint { get; private set; }
            public bool? VoidState { get; set; }
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
            if (!ready || updating) return;
            if (!isFocus && block != null && block.Name == "radiusInput" && radiusEditPending)
            {
                ScheduleRadiusCommit();
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
            radiusTimer.Stop();
            previewTimer.Stop();
            previewValid = false;
            updating = true;
            try
            {
                if (bodySelect != null) bodySelect.SetSelectedObjects(new TaggedObject[0]);
                if (faceSelect != null) faceSelect.SetSelectedObjects(new TaggedObject[0]);
                // SelectionManager is unavailable in an NX batch session.
                if (!context.Session.IsBatch) context.UI.SelectionManager.ClearGlobalSelectionList();
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
            if (!ready) return 1;
            if (radiusEditPending)
            {
                if (!CommitRadiusInput()) return 1;
                // Rebuild now, then require a second click so the user can
                // review the new hole candidates before deletion.
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
                if (!previewValid || radiusTimer.Enabled || ReadMaximumRadius() != activeMaxRadius)
                {
                    Update(maxRadius);
                    return 1;
                }
            }
            catch (Exception ex) { return Error(ex); }
            // Small disjoint batches share one native update. A failed batch
            // is rolled back before its groups are retried independently.
            Face[][] selectedGroups = groups.Where(group => group.All(face => retained.Contains(face.Tag)))
                .Select(group => group.ToArray()).ToArray();
            Tag[] selectedBodyTags = bodies.Keys.ToArray();
            if (selectedGroups.Length == 0) return 1;
            int succeeded = 0;
            int failed = 0;
            int filledFaces = 0;
            bool stopped = false;
            var deleteClock = Stopwatch.StartNew();
            try
            {
                Reset();
                validatedBodyFaults = new Dictionary<Tag, int>();
                deleteSucceeded = 0;
                using (var scan = new SelectionScan(context, "正在填孔。\r\n单击“停止”可中断。"))
                {
                    activeDeleteScan = scan;
                    try
                    {
                        for (int offset = 0; offset < selectedGroups.Length; offset += 4)
                        {
                            SelectionScan.Checkpoint();
                            Face[][] batch = selectedGroups.Skip(offset).Take(4).ToArray();
                            Face[] batchFaces = batch.SelectMany(group => group).ToArray();
                            Exception failure;
                            bool disjoint = batchFaces.Select(face => face.Tag).Distinct().Count() == batchFaces.Length;
                            if (batch.Length > 1 && disjoint && TryFillReviewedGroups(batch, true, out failure))
                            {
                                succeeded += batch.Length;
                                filledFaces += batchFaces.Length;
                                deleteSucceeded = succeeded;
                            }
                            else
                            {
                                foreach (Face[] holeFaces in batch)
                                {
                                    SelectionScan.Checkpoint();
                                    if (TryFillReviewedGroups(new[] { holeFaces }, false, out failure))
                                    {
                                        succeeded++;
                                        filledFaces += holeFaces.Length;
                                        deleteSucceeded = succeeded;
                                    }
                                    else
                                    {
                                        failed++;
                                        context.Log("Fill Holes skipped group " + failed + ": " + failure);
                                    }
                                    scan.SetProgress("已处理 " + (succeeded + failed) + "/" + selectedGroups.Length + " 组孔。\r\n单击“停止”可中断。");
                                }
                            }
                            scan.SetProgress("已处理 " + (succeeded + failed) + "/" + selectedGroups.Length + " 组孔。\r\n单击“停止”可中断。");
                        }
                    }
                    catch (OperationCanceledException) { stopped = true; }
                    finally { activeDeleteScan = null; }
                }
                if (stopped) RestorePendingHoles(selectedBodyTags, selectedGroups);
                context.Log("Fill Holes: filled " + succeeded + " groups (" + filledFaces +
                    " faces), skipped " + failed + " groups; " + (selectedGroups.Length - succeeded - failed) +
                    " unprocessed; stopped=" + stopped + "; " + deleteClock.ElapsedMilliseconds + " ms.");
                // Block Styler must accept the callback when any group was
                // committed. Returning an error for a partial success leaves
                // OK unaccepted and lets dialog cancellation undo those heals.
                // Failed groups were already rolled back independently above;
                // their count is diagnostic, not a failure of the whole action.
                return succeeded > 0 ? 0 : 1;
            }
            catch (Exception ex) { return Error(ex); }
            finally
            {
                activeDeleteScan = null;
                validatedBodyFaults = null;
                try { context.UF.Disp.Refresh(); }
                catch (Exception ex) { context.Log("Fill Holes display refresh: " + ex); }
            }
        }

        private void RestorePendingHoles(Tag[] selectedBodyTags, Face[][] reviewedGroups)
        {
            bool wasUpdating = updating;
            updating = true;
            try
            {
                var selected = new HashSet<Tag>(selectedBodyTags);
                Body[] currentBodies = context.WorkPart.Bodies.ToArray().Where(body => selected.Contains(body.Tag)).ToArray();
                var live = currentBodies.SelectMany(body => body.GetFaces()).ToDictionary(face => face.Tag);
                bodies.Clear(); groups.Clear(); retained.Clear();
                foreach (Body body in currentBodies) bodies[body.Tag] = body;
                foreach (Face[] reviewed in reviewedGroups)
                {
                    Tag[] tags = reviewed.Select(face => face.Tag).ToArray();
                    if (!tags.All(live.ContainsKey)) continue;
                    groups.Add(tags.Select(tag => live[tag]).ToArray());
                    foreach (Tag tag in tags) retained.Add(tag);
                }
                if (bodySelect != null) bodySelect.SetSelectedObjects(currentBodies.Cast<TaggedObject>().ToArray());
                SyncFaceCollector();
                previewValid = groups.Count > 0;
                Preview();
                QueuePreviewRefresh();
            }
            finally { updating = wasUpdating; }
        }

        private bool TryFillReviewedGroups(Face[][] reviewedGroups, bool batchOnly, out Exception failure)
        {
            failure = null;
            bool committed = false;
            Session.UndoMarkId mark = context.Session.SetUndoMark(Session.MarkVisibility.Visible, "NX Refine - Fill Holes");
            try
            {
                if (batchOnly)
                {
                    foreach (Face[] group in reviewedGroups) RequireRoundBoreWall(group);
                    Tag[] tags = reviewedGroups.SelectMany(group => group).Select(face => face.Tag).ToArray();
                    committed = TryDeleteReviewedFaces(tags, DeleteFaceBuilder.SelectTypes.Face, out failure);
                    if (!committed) context.Log("Fill Holes batch rolled back; retrying groups separately: " + failure.Message);
                }
                else { DeleteReviewedHoleFaces(reviewedGroups[0]); committed = true; }
                return committed;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failure = ex; return false; }
            finally
            {
                if (!committed)
                {
                    context.Session.UndoToMark(mark, null);
                    context.Session.DeleteUndoMark(mark, null);
                }
            }
        }

        private void DeleteReviewedHoleFaces(Face[] reviewedFaces)
        {
            SelectionScan.Checkpoint();
            RequireRoundBoreWall(reviewedFaces);
            Tag[] tags = reviewedFaces.Select(face => face.Tag).Distinct().ToArray();
            Exception firstFailure;
            if (TryDeleteReviewedFaces(tags, DeleteFaceBuilder.SelectTypes.Face, out firstFailure))
                return;

            // Face + Heal matches the normal native Delete Face workflow and
            // is preferred. Hole mode remains a compatibility fallback for
            // regions that rely on NX's specialized hole processing.
            Exception holeFailure;
            SelectionScan.Checkpoint();
            if (TryDeleteReviewedFaces(tags, DeleteFaceBuilder.SelectTypes.Hole, out holeFailure))
            {
                context.Log("Fill Holes used Hole-mode fallback after Face-mode failed: " + firstFailure.Message);
                return;
            }
            throw new InvalidOperationException("Face-mode and Hole-mode healing both failed. Face: " +
                firstFailure.Message + " Hole: " + holeFailure.Message, holeFailure);
        }

        private bool TryDeleteReviewedFaces(Tag[] faceTags, DeleteFaceBuilder.SelectTypes type,
            out Exception failure)
        {
            failure = null;
            bool committed = false;
            Session.UndoMarkId attemptMark = context.Session.SetUndoMark(
                Session.MarkVisibility.Invisible, "NX Refine - Fill Hole Attempt");
            DeleteFaceBuilder builder = null;
            try
            {
                var requested = new HashSet<Tag>(faceTags);
                Face[] currentFaces = context.WorkPart.Bodies.ToArray().SelectMany(body => body.GetFaces())
                    .Where(face => requested.Contains(face.Tag)).ToArray();
                if (currentFaces.Length != requested.Count)
                    throw new InvalidOperationException("The reviewed hole faces are no longer present.");
                Body[] sourceBodies=currentFaces.Select(face=>face.GetBody()).GroupBy(body=>body.Tag).Select(group=>group.First()).ToArray();
                int bodyCount=context.WorkPart.Bodies.ToArray().Length;
                var originalFaults=new Dictionary<Tag,int>();
                foreach(Body body in sourceBodies)
                {
                    int count;int[] faults;Tag[] objects;
                    if (validatedBodyFaults == null || !validatedBodyFaults.TryGetValue(body.Tag, out count))
                    {
                        context.UF.Modl.AskBodyConsistency(body.Tag,out count,out faults,out objects);
                        if (validatedBodyFaults != null) validatedBodyFaults[body.Tag] = count;
                    }
                    originalFaults[body.Tag]=count;
                }
                builder = context.WorkPart.Features.CreateDeleteFaceBuilder(null);
                builder.Type = type;
                builder.Heal = true;
                builder.UseHoleDiameter = false;
                FaceDumbRule rule = context.WorkPart.ScRuleFactory.CreateRuleFaceDumb(currentFaces);
                builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                SelectionScan.Checkpoint();
                builder.CommitFeature();
                if(context.WorkPart.Bodies.ToArray().Length!=bodyCount || context.WorkPart.Bodies.ToArray()
                    .SelectMany(body=>body.GetFaces()).Any(face=>requested.Contains(face.Tag)))
                    throw new InvalidOperationException("Healing did not preserve bodies and remove the complete reviewed hole group.");
                var currentFaults = new Dictionary<Tag, int>();
                foreach(Body body in sourceBodies)
                {
                    int count;int[] faults;Tag[] objects;
                    context.UF.Modl.AskBodyConsistency(body.Tag,out count,out faults,out objects);
                    if(!body.IsSolidBody || count>originalFaults[body.Tag])
                        throw new InvalidOperationException("Hole healing introduced solid-body consistency errors.");
                    currentFaults[body.Tag] = count;
                }
                if (validatedBodyFaults != null)
                    foreach (var pair in currentFaults) validatedBodyFaults[pair.Key] = pair.Value;
                committed = true;
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failure = ex;
                return false;
            }
            finally
            {
                // Builders must be destroyed before rollback; NX invalidates
                // a failed builder when UndoToMark restores the attempt.
                if (builder != null)
                {
                    try { builder.Destroy(); }
                    catch (Exception ex) { context.Log("Fill Holes builder cleanup: " + ex); }
                }
                if (!committed)
                {
                    try { context.Session.UndoToMark(attemptMark, null); }
                    finally { context.Session.DeleteUndoMark(attemptMark, null); }
                }
                else
                    context.Session.DeleteUndoMark(attemptMark, null);
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
