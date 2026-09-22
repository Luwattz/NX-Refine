using System;
using System.Threading;
using System.Windows.Forms;
using NXRefine.Core;

// Standalone cancellation lifecycle test: no NX process or model required.
namespace NXRefine.Core
{
    internal sealed class NxContext { public FakeUF UF = new FakeUF(); public void Log(string text) { throw new Exception(text); } }
    internal sealed class FakeUF { public FakeAbort Abort = new FakeAbort(); }
    internal sealed class FakeAbort
    {
        public bool Requested;
        public int Clears;
        public void AskFlagStatus(out bool flag) { flag = Requested; }
        public void ClearAbort() { Requested = false; Clears++; }
    }
}
internal static class SelectionScanTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void ExpectStop()
    {
        try { SelectionScan.Checkpoint(); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Cancellation was not observed.");
    }
    public static int Main()
    {
        var context = new NxContext();
        using (var scan = new SelectionScan(context))
        {
            Thread.Sleep(65);
            context.UF.Abort.Requested = true;
            ExpectStop();
            ExpectStop(); // Cancellation remains latched after the native flag is cleared.
            Assert(context.UF.Abort.Clears == 1, "Native stop must be consumed once.");
        }
        SelectionScan.Checkpoint(); // A disposed operation cannot cancel unrelated work.
        using (var scan = new SelectionScan(context)) SelectionScan.Checkpoint();
        using (var scan = new SelectionScan(context))
        {
            Form progress = null;
            for (int i = 0; i < 100 && progress == null; i++)
            {
                Thread.Sleep(25);
                if (Application.OpenForms.Count > 0) progress = Application.OpenForms[0];
            }
            Assert(progress != null, "Long scans must show Stop even while the main thread is blocked.");
            progress.Invoke(new Action(delegate {
                foreach (Control control in progress.Controls)
                    if (control is Button) ((Button)control).PerformClick();
            }));
            ExpectStop();
        }
        for (int i = 0; i < 100 && Application.OpenForms.Count > 0; i++) Thread.Sleep(25);
        Assert(Application.OpenForms.Count == 0, "Progress window leaked after cancellation.");
        using (var scan = new SelectionScan(context)) SelectionScan.Checkpoint();
        Console.WriteLine("PASS: native stop, latched cancellation, responsive Stop button, disposal, restart.");
        return 0;
    }
}
