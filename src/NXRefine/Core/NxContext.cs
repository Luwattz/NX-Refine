using System;
using NXOpen;
using NXOpen.UF;

namespace NXRefine.Core
{
    internal sealed class NxContext
    {
        private static readonly Lazy<NxContext> LazyInstance = new Lazy<NxContext>(() => new NxContext());

        private NxContext()
        {
            Session = Session.GetSession();
            UF = UFSession.GetUFSession();
            UI = NXOpen.UI.GetUI();
        }

        public static NxContext Current { get { return LazyInstance.Value; } }
        public Session Session { get; private set; }
        public UFSession UF { get; private set; }
        public NXOpen.UI UI { get; private set; }
        public Part WorkPart { get { return Session.Parts.Work; } }

        public void Log(string message)
        {
            ListingWindow window = Session.ListingWindow;
            if (!window.IsOpen) window.Open();
            window.WriteLine("[NX Refine] " + message);
        }

        public void RequireWorkPart()
        {
            if (WorkPart == null) throw new InvalidOperationException("Open a work part before running NX Refine.");
        }
    }
}
