using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Runtime.InteropServices;
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
        private volatile string progressText;
        private volatile int progressCompleted;
        private volatile int progressTotal;

        internal SelectionScan(NxContext context, string message = null)
        {
            if (current != null) throw new InvalidOperationException("A selection scan is already running.");
            this.context = context;
            progressText = message ?? "单击“停止”可中断\r\n此操作。";
            if (context.Session.IsBatch) { current = this; return; }
            windowThread = new Thread(delegate() {
                try { ShowProgress(); }
                catch (Exception ex) { windowFailure = ex; }
            }) { IsBackground = true, Name = "NX Refine Stop" };
            windowThread.SetApartmentState(ApartmentState.STA);
            windowThread.Start();
            current = this;
        }

        // Only managed state crosses threads; all NX calls stay on the NX thread.
        internal void RequestStop() { stopped = true; }
        internal void SetProgress(string message) { progressText = message; }
        internal void SetProgress(string message, int completed, int total)
        {
            progressCompleted = completed;
            progressTotal = total;
            progressText = message;
        }
        internal static void ReportProgress(string message, int completed = 0, int total = 0)
        {
            if (current != null) current.SetProgress(message, completed, total);
        }

        private void ShowProgress()
        {
            // Avoid flashing a window for quick selections. This wait never blocks NX.
            while (!disposed && clock.ElapsedMilliseconds < 700) Thread.Sleep(25);
            if (disposed) return;
            using (var form = CreateProgressForm())
                if (!disposed) Application.Run(form);
        }

        // Construct without showing so the actual window can be rendered and
        // measured in an isolated UI regression without an NX session.
        private Form CreateProgressForm()
        {
            var form = new Form();
            var font = new Font("Microsoft YaHei UI", 18F, FontStyle.Regular, GraphicsUnit.Pixel);
            var hourglass = CreateHourglass();
            var timer = new System.Windows.Forms.Timer { Interval = 50 };
            form.Disposed += delegate { timer.Dispose(); hourglass.Dispose(); font.Dispose(); };
            {
                form.Text = "工作进行中";
                form.Font = font;
                form.AutoScaleDimensions = new SizeF(96F, 96F);
                form.AutoScaleMode = AutoScaleMode.Dpi;
                form.ClientSize = new Size(360, 113);
                form.BackColor = Color.FromArgb(243, 243, 241);
                form.ForeColor = Color.FromArgb(0, 0, 80);
                form.FormBorderStyle = FormBorderStyle.FixedSingle;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ShowIcon = false;
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.TopMost = true;
                form.HandleCreated += delegate { StyleCaption(form.Handle); };
                var icon = new PictureBox { Image = hourglass, SizeMode = PictureBoxSizeMode.StretchImage,
                    Location = new Point(16, 15), Size = new Size(36, 48), TabStop = false };
                var label = new Label { Text = progressText, AutoSize = true,
                    Location = new Point(80, 14), Size = new Size(264, 52), UseMnemonic = false };
                var progress = new ProgressBar { Minimum = 0, Maximum = 1000,
                    Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 35, Height = 16 };
                var stop = new Button { Text = "停止(&S)", Location = new Point(125, 75), Size = new Size(95, 32),
                    FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 128, 153), ForeColor = Color.White,
                    UseVisualStyleBackColor = false, Cursor = Cursors.Hand, TabIndex = 0 };
                stop.FlatAppearance.BorderSize = 0;
                stop.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 112, 136);
                stop.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 95, 117);
                Action requestStop = delegate {
                    RequestStop();
                    stop.Enabled = false;
                    label.Text = "正在停止…\r\n等待当前计算结束。";
                };
                stop.Click += delegate { requestStop(); };
                form.FormClosing += delegate(object sender, FormClosingEventArgs args) {
                    if (!disposed) { args.Cancel = true; requestStop(); }
                };
                form.Controls.Add(icon);
                form.Controls.Add(label);
                form.Controls.Add(progress);
                form.Controls.Add(stop);
                form.CancelButton = stop;
                // Match the compact NX progress window in physical pixels. Point
                // fonts plus font-height scaling enlarged this twice under NX DPI.
                // Still measure text so neither message state can overlap the button.
                Action layout = delegate {
                    const int margin = 16;
                    icon.SetBounds(margin, 15, 36, 48);
                    label.Location = new Point(80, 14);
                    Size textSize = label.GetPreferredSize(Size.Empty);
                    label.Size = textSize;
                    Size buttonText = TextRenderer.MeasureText(stop.Text, stop.Font);
                    stop.Size = new Size(Math.Max(95, buttonText.Width + margin),
                        Math.Max(32, buttonText.Height + 6));
                    int width = Math.Max(360, label.Left + textSize.Width + margin);
                    int progressTop = Math.Max(icon.Bottom, label.Top + textSize.Height) + 10;
                    progress.SetBounds(margin, progressTop, width - 2 * margin, 16);
                    int buttonTop = progress.Bottom + 10;
                    stop.Location = new Point((width - stop.Width) / 2, buttonTop);
                    form.ClientSize = new Size(width, stop.Bottom + 8);
                };
                form.Shown += delegate { layout(); };
                form.FontChanged += delegate { layout(); };
                label.TextChanged += delegate { layout(); };
                layout();
                timer.Tick += delegate {
                    if (disposed) { form.Close(); return; }
                    stop.Enabled = !stopped;
                    string elapsed = "已用时 " + ((int)clock.Elapsed.TotalMinutes).ToString() + " 分 " + clock.Elapsed.Seconds.ToString("00") + " 秒";
                    label.Text = (stopped ? "正在停止…\r\n等待当前计算结束。" : progressText) + "\r\n" + elapsed;
                    int total = progressTotal;
                    if (total > 0)
                    {
                        progress.Style = ProgressBarStyle.Continuous;
                        progress.Value = (int)Math.Max(0, Math.Min(1000, (long)progressCompleted * 1000 / total));
                    }
                    else progress.Style = ProgressBarStyle.Marquee;
                };
                timer.Start();
                return form;
            }
        }

        // Keep the real Windows caption (dragging, accessibility and close behavior).
        // Caption colors are supported on Windows 11; older systems keep their theme.
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        private static void StyleCaption(IntPtr handle)
        {
            try
            {
                int background = ColorTranslator.ToWin32(Color.FromArgb(35, 35, 58));
                int foreground = ColorTranslator.ToWin32(Color.White);
                DwmSetWindowAttribute(handle, 35, ref background, sizeof(int));
                DwmSetWindowAttribute(handle, 36, ref foreground, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static Bitmap CreateHourglass()
        {
            // Draw an original compact hourglass; no Siemens image assets are bundled.
            var bitmap = new Bitmap(36, 48);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var outline = new Pen(Color.FromArgb(20, 10, 145), 4F))
            using (var shadow = new SolidBrush(Color.FromArgb(140, 140, 140)))
            using (var sand = new SolidBrush(Color.FromArgb(213, 194, 46)))
            {
                graphics.FillRectangle(shadow, 6, 7, 29, 40);
                graphics.FillRectangle(Brushes.WhiteSmoke, 5, 5, 24, 36);
                graphics.FillPolygon(sand, new[] { new Point(9, 11), new Point(25, 11), new Point(17, 21) });
                graphics.FillPolygon(sand, new[] { new Point(8, 38), new Point(17, 28), new Point(26, 38) });
                graphics.DrawLines(outline, new[] { new Point(5, 5), new Point(5, 13), new Point(15, 23),
                    new Point(5, 33), new Point(5, 41) });
                graphics.DrawLines(outline, new[] { new Point(29, 5), new Point(29, 13), new Point(19, 23),
                    new Point(29, 33), new Point(29, 41) });
                graphics.DrawLine(outline, 1, 3, 33, 3);
                graphics.DrawLine(outline, 1, 42, 33, 42);
            }
            return bitmap;
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
