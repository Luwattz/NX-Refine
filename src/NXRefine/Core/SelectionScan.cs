using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace NXRefine.Core
{
    // Only the small progress window runs on another thread. All NX calls stay
    // on the calling NX thread; no DoEvents/reentrant Block Styler callbacks.
    internal sealed class SelectionScan : IDisposable
    {
        [ThreadStatic] private static SelectionScan current;
        private readonly NxContext context;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Thread windowThread;
        private volatile bool stopped;
        private volatile bool disposed;
        private volatile Exception windowFailure;
        private long lastPoll;

        internal SelectionScan(NxContext context)
        {
            if (current != null) throw new InvalidOperationException("A selection scan is already running.");
            this.context = context;
            windowThread = new Thread(delegate() {
                try { ShowProgress(); }
                catch (Exception ex) { windowFailure = ex; }
            }) { IsBackground = true, Name = "NX Refine Stop" };
            windowThread.SetApartmentState(ApartmentState.STA);
            windowThread.Start();
            current = this;
        }

        private void ShowProgress()
        {
            // Avoid flashing a window for quick selections. This wait never blocks NX.
            while (!disposed && clock.ElapsedMilliseconds < 700) Thread.Sleep(25);
            if (disposed) return;
            using (var form = new Form())
            using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
            {
                form.Text = "工作进行中 — NX Refine";
                form.ClientSize = new Size(350, 118);
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.TopMost = true;
                var label = new Label { Text = "正在识别候选面，单击“停止”可中断操作。", AutoSize = false,
                    Location = new Point(18, 16), Size = new Size(318, 38) };
                var stop = new Button { Text = "停止(&S)", Location = new Point(125, 68), Size = new Size(100, 30) };
                Action requestStop = delegate {
                    stopped = true;
                    stop.Enabled = false;
                    label.Text = "正在停止，等待当前几何计算返回…";
                };
                stop.Click += delegate { requestStop(); };
                form.FormClosing += delegate(object sender, FormClosingEventArgs args) {
                    if (!disposed) { args.Cancel = true; requestStop(); }
                };
                form.Controls.Add(label);
                form.Controls.Add(stop);
                form.CancelButton = stop;
                timer.Tick += delegate { if (disposed) form.Close(); };
                timer.Start();
                if (!disposed) Application.Run(form);
            }
        }

        internal static void Checkpoint()
        {
            SelectionScan scan = current;
            if (scan == null) return;
            if (scan.stopped) throw new OperationCanceledException("Selection scan stopped.");
            if (scan.clock.ElapsedMilliseconds - scan.lastPoll < 50) return;
            scan.lastPoll = scan.clock.ElapsedMilliseconds;
            bool abort;
            scan.context.UF.Abort.AskFlagStatus(out abort);
            if (abort)
            {
                scan.stopped = true;
                scan.context.UF.Abort.ClearAbort();
                throw new OperationCanceledException("Selection scan stopped.");
            }
        }

        public void Dispose()
        {
            current = null;
            disposed = true;
            if (windowFailure != null) context.Log("Selection progress window: " + windowFailure);
            // The progress thread closes itself, without waiting on the NX thread.
        }
    }
}
