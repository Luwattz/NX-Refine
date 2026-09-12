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
    // Native Block Styler workflow for selecting bodies, automatically finding
    // inner-hole seeds, reviewing native Boss/Pocket regions, excluding
    // connected groups, and healing the exact retained faces on Apply/OK.
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
            ready = true;
        }

        private int Update(UIBlock block)
        {
            if (updating) return 0;
            try
            {
                updating = true;
                string blockName = block == null ? string.Empty : block.Name;
                if (blockName == "radiusInput" && previewValid &&
                    !radiusTimer.Enabled && ReadMaximumRadius() == activeMaxRadius) return 0;
                if (blockName == "radiusInput") RebuildCandidates();
                else if (blockName == "bodies") UpdateBodies();
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

        private Face[][] FindGroups(Body body, double maximumRadius)
        {
            Face[] allFaces = body.GetFaces();
            var result = new List<Face[]>();
            var seen = new HashSet<Tag>();
            foreach (Face face in allFaces)
            {
                HoleSeed seed;
                if (seen.Contains(face.Tag) || !TryGetInnerCylinder(face, body, out seed) ||
                    (maximumRadius > 0.0 && seed.Radius > maximumRadius)) continue;

                // Evaluate NX's actual selection-intent rule for each detected
                // inner cylinder. Never flood through the body's adjacency graph.
                ScCollector collector = context.WorkPart.ScCollectors.CreateCollector();
                Face[] region;
                try
                {
                    FaceBossPocketFacesRule rule =
                        context.WorkPart.ScRuleFactory.CreateRuleFaceBossPocket(face, false);
                    collector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                    region = collector.GetObjects().OfType<Face>()
                        .GroupBy(item => item.Tag).Select(items => items.First()).ToArray();
                }
                catch (NXException ex)
                {
                    context.Log("Fill Holes native expansion failed for " + face.Tag + ": " + ex.Message);
                    continue;
                }
                finally { collector.Destroy(); }

                if (!region.Any(item => item.Tag == face.Tag) ||
                    region.Length >= allFaces.Length ||
                    region.Any(item => item.GetBody().Tag != body.Tag)) continue;

                // Reject the whole region if NX returns an exterior cylinder
                // or a stepped hole that exceeds the limit. Do not trim native
                // groups into incomplete face sets before healing.
                bool valid = true;
                foreach (Face item in region.Where(item => item.SolidFaceType == Face.FaceType.Cylindrical))
                {
                    HoleSeed inner;
                    if (!TryGetInnerCylinder(item, body, out inner) || !IsCoaxial(seed, inner) ||
                        (maximumRadius > 0.0 && inner.Radius > maximumRadius))
                    {
                        valid = false;
                        break;
                    }
                }
                if (!valid) continue;

                // Native regions from different seed cylinders can overlap.
                // Merge them so review exclusions always remove the whole hole.
                var merged = region.ToDictionary(item => item.Tag);
                for (int i = result.Count - 1; i >= 0; i--)
                {
                    if (!result[i].Any(item => merged.ContainsKey(item.Tag))) continue;
                    foreach (Face item in result[i]) merged[item.Tag] = item;
                    result.RemoveAt(i);
                    i = result.Count; // Recheck earlier disjoint regions after the union grows.
                }
                Face[] group = merged.Values.ToArray();
                result.Add(group);
                foreach (Face item in group) seen.Add(item.Tag);
            }
            context.Log("Fill Holes: " + result.Count + " native hole groups in body " + body.Tag);
            return result.ToArray();
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
                // AskFaceData's axis origin need not lie within the trimmed
                // cylinder (e.g. it can be below a blind hole's floor). Project
                // the sampled wall point onto the axis before testing the void.
                double axisLengthSquared = axisDirection.Sum(value => value * value);
                if (axisLengthSquared < 1e-12) return false;
                double station = 0.0;
                for (int i = 0; i < 3; i++)
                    station += (surfacePoint[i] - axisPoint[i]) * axisDirection[i];
                for (int i = 0; i < 3; i++)
                    axisPoint[i] += station * axisDirection[i] / axisLengthSquared;
                int containment;
                context.UF.Modl.AskPointContainment(axisPoint, body.Tag, out containment);
                // The cylinder axis point is in the void for an inner hole;
                // an exterior boss has its axis point inside the solid.
                if (containment == 1) return false;
                seed = new HoleSeed(face, axisPoint, axisDirection, radius);
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
