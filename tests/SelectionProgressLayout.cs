using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

// Render the production form offscreen without calling NX APIs.
internal static class SelectionProgressLayout
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e) {
            string path = Path.Combine(args[2], new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        Assembly assembly = Assembly.LoadFrom(args[0]);
        Type type = assembly.GetType("NXRefine.Core.SelectionScan", true);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Directory.CreateDirectory(args[1]);
        string[] messages = {
            "阶段 1/3：筛选倒圆角\r\n已检查 3072/6120 个面",
            "阶段 2/3：识别连接倒圆角\r\n已检查或覆盖 2899/4181 个源面\r\n已找到 3000 个面；正在计算连接链",
            "阶段 3/3：显示选择结果\r\n正在显示 4361 个面",
            "正在清理倒圆角\r\n已删除 1234/4361 个选面；已尝试 230 条链\r\n单击“停止”保留已完成的清理"
        };
        for (int i = 0; i < messages.Length + 1; i++)
        {
            object scan = FormatterServices.GetUninitializedObject(type);
            type.GetField("clock", flags).SetValue(scan, Stopwatch.StartNew());
            type.GetMethod("SetProgress", flags, null, new[] { typeof(string), typeof(int), typeof(int) }, null)
                .Invoke(scan, new object[] { messages[Math.Min(i, messages.Length - 1)], i == 2 ? 0 : 50, i == 2 ? 0 : 100 });
            using (Form form = (Form)type.GetMethod("CreateProgressForm", flags).Invoke(scan, null))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.Show();
                Thread.Sleep(80);
                Application.DoEvents();
                Label label = form.Controls.OfType<Label>().Single();
                ProgressBar progress = form.Controls.OfType<ProgressBar>().Single();
                Button stop = form.Controls.OfType<Button>().Single();
                if (i == messages.Length)
                    typeof(Button).GetMethod("OnClick", flags).Invoke(stop, new object[] { EventArgs.Empty });
                if (label.Bottom >= progress.Top || progress.Bottom >= stop.Top || stop.Bottom > form.ClientSize.Height)
                    throw new Exception("Progress text/bar/Stop overlap or leave the client area.");
                if (label.Right > form.ClientSize.Width || progress.Right > form.ClientSize.Width)
                    throw new Exception("Progress text or bar is clipped horizontally.");
                if (i != 2 && progress.Value != 500) throw new Exception("Determinate percentage is incorrect.");
                if (i == 2 && progress.Style != ProgressBarStyle.Marquee) throw new Exception("Unknown-duration stage must animate.");
                if (i == messages.Length && (!(bool)type.GetField("stopped", flags).GetValue(scan) || stop.Enabled))
                    throw new Exception("Stop must request cancellation and disable the button.");
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(args[1], "progress-" + i + ".png"));
                }
            }
        }
        Console.WriteLine("PASS: production progress form, all phases, elapsed text, determinate/marquee, Stop, no clipping; rendered offscreen.");
    }
}
