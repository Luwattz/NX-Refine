using NXOpen;

namespace NXRefine.Core
{
    internal enum IssueKind
    {
        TinyObject,
        MisalignedObject,
        InvalidBodyStructure,
        InconsistentBody,
        FaceIntersection,
        OpenSheetBoundary,
        NonSmoothFace,
        SelfIntersectingFace,
        SpikeOrCut,
        NonSmoothEdge,
        EdgeTolerance,
        SmallFace,
        ShortEdge,
        SmallBlend,
        SmallHole,
        EngravingFeature
    }

    internal sealed class Issue
    {
        public Issue(IssueKind kind, DisplayableObject entity, string message)
        {
            Kind = kind;
            Entity = entity;
            Message = message;
        }

        public IssueKind Kind { get; private set; }
        public DisplayableObject Entity { get; private set; }
        public string Message { get; private set; }
    }
}
