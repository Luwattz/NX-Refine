using System;
using System.Collections.Generic;
using System.Linq;
using NXOpen;
using NXOpen.GeometricAnalysis;
using NXRefine.Core;

namespace NXRefine.Analysis
{
    internal sealed class GeometryAnalyzer
    {
        private readonly NxContext context;
        private readonly CleanupSettings settings;

        public GeometryAnalyzer(NxContext context, CleanupSettings settings)
        {
            this.context = context;
            this.settings = settings;
        }

        public IList<Issue> Analyze(bool highlight)
        {
            context.RequireWorkPart();
            var issues = new List<Issue>();
            Body[] bodies = context.WorkPart.Bodies.ToArray();
            if (bodies.Length == 0) return issues;

            ExamineGeometry examine = context.WorkPart.AnalysisManager.CreateExamineGeometryObject();
            examine.SetAllChecks();
            examine.CheckCriteriaDistance = settings.ShortEdgeLength;
            examine.CheckCriteriaAngle = settings.SharpAngle;
            examine.ObjectsToExamine.Add(bodies.Cast<DisplayableObject>().ToArray());
            examine.Examine();

            AddExamineIssues(examine, ExamineGeometry.Check.ObjectTiny, IssueKind.TinyObject, issues, highlight);
            AddExamineIssues(examine, ExamineGeometry.Check.ObjectMisaligned, IssueKind.MisalignedObject, issues, highlight);
            AddExamineIssues(examine, ExamineGeometry.Check.BodyDataStructures, IssueKind.InvalidBodyStructure, issues, highlight);
            AddExamineIssues(examine, ExamineGeometry.Check.BodyConsistency, IssueKind.InconsistentBody, issues, highlight);
            AddExamineIssues(examine, ExamineGeometry.Check.BodyFaceIntersections, IssueKind.FaceIntersection, issues, highlight);
            AddExamineIssues(examine, ExamineGeometry.Check.BodySheetBoundaries, IssueKind.OpenSheetBoundary, issues, highlight);
            AddExamineIssues(examine, ExamineGeometry.Check.FaceSmoothness, IssueKind.NonSmoothFace, issues, highlight);
            AddExamineIssues(examine, ExamineGeometry.Check.FaceSelfIntersection, IssueKind.SelfIntersectingFace, issues, highlight);
            AddExamineIssues(examine, ExamineGeometry.Check.FaceSpikesCuts, IssueKind.SpikeOrCut, issues, highlight);
            AddExamineIssues(examine, ExamineGeometry.Check.EdgeSmoothness, IssueKind.NonSmoothEdge, issues, highlight);
            AddExamineIssues(examine, ExamineGeometry.Check.EdgeTolerances, IssueKind.EdgeTolerance, issues, highlight);

            foreach (Body body in bodies)
            {
                foreach (Edge edge in body.GetEdges())
                {
                    if (edge.GetLength() <= settings.ShortEdgeLength)
                        AddUnique(issues, new Issue(IssueKind.ShortEdge, edge, "Short edge: " + edge.GetLength().ToString("0.####")), highlight);
                }

                foreach (Face face in body.GetFaces())
                {
                    double radius;
                    bool isBlend;
                    face.GetBlendData(out radius, out isBlend);
                    if (isBlend && radius <= settings.MaxBlendRadius)
                        AddUnique(issues, new Issue(IssueKind.SmallBlend, face, "Blend radius: " + radius.ToString("0.####")), highlight);

                    double cylinderRadius;
                    if (TryGetCylinderRadius(face, out cylinderRadius) && cylinderRadius * 2.0 <= settings.MaxHoleDiameter)
                        AddUnique(issues, new Issue(IssueKind.SmallHole, face, "Cylindrical candidate diameter: " + (cylinderRadius * 2.0).ToString("0.####")), highlight);

                    double area;
                    if (TryGetFaceArea(face, out area) && area <= settings.SmallFaceArea)
                        AddUnique(issues, new Issue(IssueKind.SmallFace, face, "Small face area: " + area.ToString("0.####")), highlight);
                }
            }

            return issues;
        }

        public IList<Face> FindBlendFaces() { return FindFaces(issue => issue.Kind == IssueKind.SmallBlend); }
        public IList<Face> FindHoleFaces() { return FindFaces(issue => issue.Kind == IssueKind.SmallHole); }
        public IList<Face> FindSmallFaces() { return FindFaces(issue => issue.Kind == IssueKind.SmallFace); }

        private IList<Face> FindFaces(Func<Issue, bool> predicate)
        {
            return Analyze(false)
                .Where(predicate)
                .Select(issue => issue.Entity as Face)
                .Where(face => face != null)
                .GroupBy(face => face.Tag)
                .Select(group => group.First())
                .ToList();
        }

        private void AddExamineIssues(ExamineGeometry examine, ExamineGeometry.Check check, IssueKind kind, IList<Issue> issues, bool highlight)
        {
            NXObject[] failed = examine.GetFailedObjects(check);
            foreach (NXObject item in failed)
            {
                DisplayableObject displayable = item as DisplayableObject;
                if (displayable != null) AddUnique(issues, new Issue(kind, displayable, check.ToString()), false);
            }
            if (highlight && failed.Length > 0) examine.HighlightResult(check);
        }

        private bool TryGetCylinderRadius(Face face, out double radius)
        {
            radius = 0.0;
            if (face.SolidFaceType != Face.FaceType.Cylindrical) return false;
            try
            {
                int type;
                int normalDirection;
                double radiusData;
                var point = new double[3];
                var direction = new double[3];
                var box = new double[6];
                context.UF.Modl.AskFaceData(face.Tag, out type, point, direction, box, out radius, out radiusData, out normalDirection);
                return radius > 0.0;
            }
            catch (NXException)
            {
                return false;
            }
        }

        private bool TryGetFaceArea(Face face, out double area)
        {
            area = 0.0;
            try
            {
                bool metric = context.WorkPart.PartUnits == BasePart.Units.Millimeters;
                Unit areaUnit = context.WorkPart.UnitCollection.FindObject(metric ? "SquareMilliMeter" : "SquareInch");
                Unit lengthUnit = context.WorkPart.UnitCollection.FindObject(metric ? "MilliMeter" : "Inch");
                MeasureFaces measure = context.WorkPart.MeasureManager.NewFaceProperties(
                    areaUnit,
                    lengthUnit,
                    0.999,
                    new IParameterizedSurface[] { face });
                area = measure.Area;
                return true;
            }
            catch (NXException)
            {
                return false;
            }
        }

        private static void AddUnique(IList<Issue> issues, Issue issue, bool highlight)
        {
            if (issues.Any(existing => existing.Kind == issue.Kind && existing.Entity.Tag == issue.Entity.Tag)) return;
            issues.Add(issue);
            if (highlight) issue.Entity.Highlight();
        }
    }
}
