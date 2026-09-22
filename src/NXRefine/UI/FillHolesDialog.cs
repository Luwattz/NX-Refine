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
    // Native Block Styler workflow for selecting bodies, recognizing hole
    // faces with NX's native Hole Faces rule, reviewing validated regions,
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
            Face[] allFaces = body.GetFaces();
            var bodyFaces = allFaces.ToDictionary(face => face.Tag);
            var result = new List<Face[]>();
            var seen = new HashSet<Tag>();
            int nonInnerSeeds = 0;
            foreach (Face face in allFaces)
            {
                SelectionScan.Checkpoint();
                if (seen.Contains(face.Tag) || !IsHoleWallType(face)) continue;

                // This is the same native Hole Faces selection intent exposed
                // by NX's Delete Face command. It recognizes the hole first;
                // the geometric checks below are only safety/radius filters.
                Face[] holeFaces = FindNativeHoleFaces(face);
                // Never accept a rule that escaped the selected body. NX's
                // hole rule is the authority for which faces form the hole.
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

                // Retain the inner-wall/coaxial checks after native Hole Faces
                // recognition. Do not use
                // them to replace the official hole-face recognition above.
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
                    if (!HasFullAngularSpan(item) || radius <= 0.0 || (maximumRadius > 0.0 && radius > maximumRadius) ||
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
            context.Log("Fill Holes: " + result.Count + " native hole groups in body " + body.Tag +
                "; rejected " +
                nonInnerSeeds + " exterior cylindrical/conical seeds.");
            return result.ToArray();
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

        private static bool IsHoleWallType(Face face)
        {
            return face != null && (face.SolidFaceType == Face.FaceType.Cylindrical ||
                face.SolidFaceType == Face.FaceType.Conical);
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
                if (face.SolidFaceType == Face.FaceType.Cylindrical) return radius;

                double axisLength = Math.Sqrt(axisDirection.Sum(value => value * value));
                if (axisLength < 1e-12 || double.IsNaN(radiusData) || double.IsInfinity(radiusData)) return 0.0;
                double maxStation = 0.0;
                for (int mask = 0; mask < 8; mask++)
                {
                    SelectionScan.Checkpoint();
                    double station = 0.0;
                    for (int i = 0; i < 3; i++)
                    {
                        SelectionScan.Checkpoint();
                        double coordinate = box[((mask >> i) & 1) == 0 ? i : i + 3];
                        station += (coordinate - axisPoint[i]) * axisDirection[i] / axisLength;
                    }
                    maxStation = Math.Max(maxStation, Math.Abs(station));
                }
                double maximum = radius + Math.Abs(Math.Tan(radiusData)) * maxStation;
                return maximum > 0.0 && !double.IsNaN(maximum) && !double.IsInfinity(maximum)
                    ? maximum : 0.0;
            }
            catch (NXException) { return 0.0; }
        }

        private bool TryGetInnerRevolvedFace(Face face, Body body, out HoleSeed seed)
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
                int containment;
                context.UF.Modl.AskPointContainment(axisPoint, body.Tag, out containment);
                // The local axis point is in the void for an inner revolved
                // wall; an exterior boss has its axis point inside the solid.
                if (containment == 1) return false;
                seed = new HoleSeed(face, axisPoint, axisDirection, maximumRadius);
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

        private static bool IsCoaxial(HoleSeed first, HoleSeed second)
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
            double tolerance = 1e-5 * Math.Max(1.0, Math.Max(first.Radius, second.Radius));
            return Math.Abs(dot) >= 1.0 - 1e-8 &&
                Math.Max(0.0, distanceSquared - along * along) <= tolerance * tolerance;
        }

        private sealed class HoleSeed
        {
            public HoleSeed(Face face, double[] axisPoint, double[] axis, double radius)
            {
                Face = face;
                AxisPoint = axisPoint;
                Axis = axis;
                Radius = radius;
            }

            public Face Face { get; private set; }
            public double[] AxisPoint { get; private set; }
            public double[] Axis { get; private set; }
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
            // Delete one connected hole at a time so one unhealable group does
            // not prevent NX from processing other selected groups.
            Face[][] selectedGroups = groups.Where(group => group.All(face => retained.Contains(face.Tag)))
                .Select(group => group.ToArray()).ToArray();
            if (selectedGroups.Length == 0) return 1;
            int succeeded = 0;
            int failed = 0;
            int filledFaces = 0;
            try
            {
                Reset();
                foreach (Face[] holeFaces in selectedGroups)
                {
                    SelectionScan.Checkpoint();
                    Session.UndoMarkId mark = context.Session.SetUndoMark(
                        Session.MarkVisibility.Visible, "NX Refine - Fill Hole");
                    Exception failure = null;
                    try
                    {
                        DeleteReviewedHoleFaces(holeFaces);
                        succeeded++;
                        filledFaces += holeFaces.Length;
                    }
                    catch (Exception ex) { failure = ex; }
                    if (failure != null)
                    {
                        failed++;
                        context.Log("Fill Holes skipped group " + failed + ": " + failure);
                        try { context.Session.UndoToMark(mark, "NX Refine - Fill Hole"); }
                        catch (Exception ex) { context.Log("Fill Holes rollback failed: " + ex); return Error(ex); }
                    }
                }
                context.Log("Fill Holes: filled " + succeeded + " groups (" + filledFaces +
                    " faces), skipped " + failed + " groups.");
                if (failed > 0) return Error(new InvalidOperationException(
                    "部分孔无法由剩余面闭合：已填充 " + succeeded + " 个，跳过 " + failed + " 个。详情见 NX 日志。"));
                return succeeded > 0 ? 0 : 1;
            }
            catch (Exception ex) { return Error(ex); }
            finally
            {
                try { context.UF.Disp.Refresh(); }
                catch (Exception ex) { context.Log("Fill Holes display refresh: " + ex); }
            }
        }

        private void DeleteReviewedHoleFaces(Face[] reviewedFaces)
        {
            Tag[] tags = reviewedFaces.Select(face => face.Tag).Distinct().ToArray();
            Exception firstFailure;
            if (TryDeleteReviewedFaces(tags, DeleteFaceBuilder.SelectTypes.Face, out firstFailure))
                return;

            // Face + Heal matches the normal native Delete Face workflow and
            // is preferred. Hole mode remains a compatibility fallback for
            // regions that rely on NX's specialized hole processing.
            Exception holeFailure;
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
                builder = context.WorkPart.Features.CreateDeleteFaceBuilder(null);
                builder.Type = type;
                builder.Heal = true;
                builder.UseHoleDiameter = false;
                FaceDumbRule rule = context.WorkPart.ScRuleFactory.CreateRuleFaceDumb(currentFaces);
                builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                builder.CommitFeature();
                committed = true;
                return true;
            }
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
