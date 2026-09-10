using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using NXOpen;
using NXOpen.Features;
using NXRefine.Analysis;
using NXRefine.Core;

namespace NXRefine.Repair
{
    internal sealed class CleanupEngine
    {
        private readonly NxContext context;
        private readonly CleanupSettings settings;
        private readonly GeometryAnalyzer analyzer;

        public CleanupEngine(NxContext context, CleanupSettings settings)
        {
            this.context = context;
            this.settings = settings;
            analyzer = new GeometryAnalyzer(context, settings);
        }

        public int RemoveSmallFaces()
        {
            IList<Face> faces = analyzer.FindSmallFaces();
            return DeleteFacesByBody(faces, DeleteFaceBuilder.SelectTypes.Face, "Remove Small Faces");
        }

        public int RemoveBlends()
        {
            IList<Face> faces = analyzer.FindBlendFaces();
            return DeleteFacesByBody(faces, DeleteFaceBuilder.SelectTypes.Blend, "Remove Small Blends");
        }

        public int FillHoles()
        {
            IList<Face> faces = analyzer.FindHoleFaces();
            return DeleteFacesByBody(faces, DeleteFaceBuilder.SelectTypes.Hole, "Fill Small Holes");
        }

        public int RepairSheets()
        {
            context.RequireWorkPart();
            Body[] sheets = context.WorkPart.Bodies.ToArray().Where(body => body.IsSheetBody).ToArray();
            if (sheets.Length < 2)
            {
                MessageBox.Show("At least two sheet bodies are required for automatic sewing.", "NX Refine", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            if (!Confirm("Sew " + sheets.Length + " sheet bodies using tolerance " + settings.SewTolerance + "?")) return 0;

            Session.UndoMarkId mark = context.Session.SetUndoMark(Session.MarkVisibility.Visible, "NX Refine - Repair Sheets");
            SewBuilder builder = null;
            try
            {
                builder = context.WorkPart.Features.CreateSewBuilder(null);
                builder.Type = SewBuilder.Types.Sheet;
                builder.Tolerance = settings.SewTolerance;
                builder.OptimizeFaces = true;
                BodyDumbRule targetRule = context.WorkPart.ScRuleFactory.CreateRuleBodyDumb(new[] { sheets[0] });
                BodyDumbRule toolRule = context.WorkPart.ScRuleFactory.CreateRuleBodyDumb(sheets.Skip(1).ToArray());
                builder.TargetBodiesCollector.ReplaceRules(new SelectionIntentRule[] { targetRule }, false);
                builder.ToolBodiesCollector.ReplaceRules(new SelectionIntentRule[] { toolRule }, false);
                builder.CommitFeature();
                return sheets.Length;
            }
            catch
            {
                context.Session.UndoToMark(mark, "NX Refine - Repair Sheets");
                throw;
            }
            finally
            {
                if (builder != null) builder.Destroy();
            }
        }

        public int RemoveEngravingFeatures()
        {
            context.RequireWorkPart();
            Feature[] candidates = context.WorkPart.Features.ToArray()
                .Where(IsEngravingFeature)
                .ToArray();
            if (candidates.Length == 0)
            {
                MessageBox.Show("No history-based engraving or text features were found. Dumb-solid marking recognition is planned for a later milestone.", "NX Refine", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            Face[] previewFaces = candidates.SelectMany(feature => feature.GetFaces())
                .GroupBy(face => face.Tag).Select(group => group.First()).ToArray();
            bool accepted;
            try
            {
                foreach (Face face in previewFaces) face.Highlight();
                context.UF.Disp.Refresh();
                accepted = Confirm("Delete " + candidates.Length + " history-based engraving/text features? Their available faces are highlighted.");
            }
            finally
            {
                ClearFeatureHighlights(candidates);
                ClearHighlights(previewFaces);
            }
            if (!accepted) return 0;

            Session.UndoMarkId mark = context.Session.SetUndoMark(Session.MarkVisibility.Visible, "NX Refine - Remove Markings");
            try
            {
                context.Session.UpdateManager.AddObjectsToDeleteList(candidates);
                context.Session.UpdateManager.DoUpdate(mark);
                return candidates.Length;
            }
            catch
            {
                context.Session.UndoToMark(mark, "NX Refine - Remove Markings");
                throw;
            }
        }

        private int DeleteFacesByBody(IList<Face> faces, DeleteFaceBuilder.SelectTypes type, string operation)
        {
            if (faces.Count == 0)
            {
                MessageBox.Show("No matching candidates were found.", "NX Refine", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            bool accepted;
            try
            {
                foreach (Face face in faces) face.Highlight();
                context.UF.Disp.Refresh();
                accepted = Confirm(operation + " will process " + faces.Count + " highlighted faces. Continue?");
            }
            finally
            {
                ClearHighlights(faces);
            }
            if (!accepted) return 0;

            Session.UndoMarkId mark = context.Session.SetUndoMark(Session.MarkVisibility.Visible, "NX Refine - " + operation);
            int processed = 0;
            try
            {
                foreach (IGrouping<Body, Face> bodyFaces in faces.GroupBy(face => face.GetBody()))
                {
                    DeleteFaceBuilder builder = null;
                    try
                    {
                        Face[] group = bodyFaces.ToArray();
                        builder = context.WorkPart.Features.CreateDeleteFaceBuilder(null);
                        builder.Type = type;
                        builder.Heal = true;
                        builder.UseHoleDiameter = type == DeleteFaceBuilder.SelectTypes.Hole;
                        if (type == DeleteFaceBuilder.SelectTypes.Hole)
                            builder.MaxHoleDiameter.RightHandSide = settings.MaxHoleDiameter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (type == DeleteFaceBuilder.SelectTypes.Blend)
                            builder.MaxBlendRadius.RightHandSide = settings.MaxBlendRadius.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        FaceDumbRule rule = context.WorkPart.ScRuleFactory.CreateRuleFaceDumb(group);
                        builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                        builder.CommitFeature();
                        processed += group.Length;
                    }
                    finally
                    {
                        if (builder != null) builder.Destroy();
                    }
                }
                return processed;
            }
            catch
            {
                context.Session.UndoToMark(mark, "NX Refine - " + operation);
                throw;
            }
        }

        private void ClearHighlights(IEnumerable<Face> faces)
        {
            foreach (Face face in faces)
            {
                try
                {
                    face.Unhighlight();
                    context.UF.Disp.SetHighlight(face.Tag, 0);
                }
                catch (NXException)
                {
                    // A successfully deleted face no longer has a valid display object.
                }
            }
            context.UF.Disp.Refresh();
        }

        private static void ClearFeatureHighlights(IEnumerable<Feature> features)
        {
            foreach (Feature feature in features)
            {
                try
                {
                    feature.Unhighlight();
                }
                catch (NXException)
                {
                    // A successfully deleted feature no longer has a valid display object.
                }
            }
        }

        private bool Confirm(string message)
        {
            return !settings.ConfirmBeforeRepair || MessageBox.Show(
                message + Environment.NewLine + Environment.NewLine + "The operation is protected by a single NX undo mark.",
                "NX Refine",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private static bool IsEngravingFeature(Feature feature)
        {
            string value = ((feature.FeatureType ?? string.Empty) + " " + (feature.Name ?? string.Empty)).ToUpperInvariant();
            return value.Contains("TEXT") || value.Contains("ENGRAV") || value.Contains("EMBOSS") || value.Contains("MARKING");
        }
    }
}
