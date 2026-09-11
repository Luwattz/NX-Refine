using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using NXOpen;
using NXOpen.Features;
using NXRefine.Core;

namespace NXRefine.UI
{
    // Connected islands on a carrier face are candidates, not semantic text recognition.
    internal sealed class MarkingsForm : Form
    {
        private readonly NxContext context;
        private Body body;
        private Face carrier;
        private readonly List<Face[]> groups = new List<Face[]>();
        private readonly CheckedListBox list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        private readonly NumericUpDown size = new NumericUpDown { Minimum = 0.01m, Maximum = 100000, DecimalPlaces = 2, Value = 20, Width = 100 };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(470, 0) };
        private bool rebuilding;

        public MarkingsForm(NxContext context)
        {
            this.context = context;
            context.RequireWorkPart();
            Text = "Remove Markings - Geometry Preview";
            ClientSize = new Size(520, 510);
            StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(470, 0), Text = "Select a body and the large face carrying the lettering. Review connected candidates: bosses and other details can also be detected. Only checked groups will be deleted and healed." });
            var selection = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            AddButton(selection, "Select Body / Face", SelectTarget);
            AddButton(selection, "Find Candidates", Scan);
            layout.Controls.Add(selection);
            var limits = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            limits.Controls.Add(new Label { AutoSize = true, Text = "Max group diagonal (part units)" });
            limits.Controls.Add(size);
            layout.Controls.Add(limits);
            layout.Controls.Add(list);
            var review = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            AddButton(review, "Pick Groups to Exclude", Exclude);
            AddButton(review, "Check All", () => SetAll(true));
            AddButton(review, "Uncheck All", () => SetAll(false));
            layout.Controls.Add(review);
            var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            bottom.Controls.Add(status);
            AddButton(bottom, "Apply", () => Apply(false));
            AddButton(bottom, "OK", () => Apply(true));
            var cancel = AddButton(bottom, "Cancel", Close);
            CancelButton = cancel;
            layout.Controls.Add(bottom);
            Controls.Add(layout);
            list.ItemCheck += (sender, e) => { if (!rebuilding) BeginInvoke(new Action(() => Run(Preview))); };
            FormClosed += (sender, e) => ClearPreview();
            status.Text = "Choose a carrier face to begin.";
        }

        private Button AddButton(Control parent, string label, Action action)
        {
            var button = new Button { Text = label, AutoSize = true };
            button.Click += (sender, e) => Run(action);
            parent.Controls.Add(button);
            return button;
        }

        private void Run(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                ClearPreview();
                context.Log(ex.ToString());
                status.Text = "Failed: " + ex.Message;
            }
        }

        private TaggedObject Pick(string prompt)
        {
            TaggedObject selected;
            Point3d cursor;
            var result = context.UI.SelectionManager.SelectTaggedObject(prompt, "Remove Markings",
                Selection.SelectionScope.WorkPart, false, false, out selected, out cursor);
            return result == Selection.Response.ObjectSelected || result == Selection.Response.ObjectSelectedByName ||
                result == Selection.Response.Ok ? selected : null;
        }

        private void SelectTarget()
        {
            ClearPreview();
            Hide();
            try
            {
                var selected = Pick("Select the lettering carrier face or its solid body");
                if (selected == null) return;
                Face face = selected as Face;
                Body selectedBody = selected as Body;
                if (face != null) selectedBody = face.GetBody();
                if (selectedBody == null || selectedBody.IsOccurrence || !selectedBody.IsSolidBody)
                    throw new InvalidOperationException("Select a solid body in the work part.");
                if (face == null) face = Pick("Select the large carrier face on the selected body") as Face;
                if (face == null) return;
                if (face.GetBody().Tag != selectedBody.Tag) throw new InvalidOperationException("Carrier face must belong to the selected body.");
                body = selectedBody;
                carrier = face;
                Scan();
            }
            finally { Show(); Preview(); }
        }

        private void Scan()
        {
            ClearPreview();
            groups.Clear();
            list.Items.Clear();
            if (carrier == null || body == null) { status.Text = "Select a body and carrier face first."; return; }
            var faces = body.GetFaces().Where(f => f.Tag != carrier.Tag).ToDictionary(f => f.Tag);
            var adjacency = faces.Keys.ToDictionary(tag => tag, tag => new HashSet<Tag>());
            var boundary = new HashSet<Tag>();
            foreach (Edge edge in body.GetEdges())
            {
                Face[] touching = edge.GetFaces();
                bool atCarrier = touching.Any(f => f.Tag == carrier.Tag);
                foreach (Face face in touching.Where(f => faces.ContainsKey(f.Tag)))
                {
                    if (atCarrier) boundary.Add(face.Tag);
                    foreach (Face other in touching.Where(f => faces.ContainsKey(f.Tag) && f.Tag != face.Tag))
                        adjacency[face.Tag].Add(other.Tag);
                }
            }
            var seen = new HashSet<Tag>();
            foreach (Tag seed in boundary)
            {
                if (seen.Contains(seed)) continue;
                var component = new List<Face>();
                var queue = new Queue<Tag>();
                queue.Enqueue(seed);
                seen.Add(seed);
                while (queue.Count > 0)
                {
                    Tag tag = queue.Dequeue();
                    component.Add(faces[tag]);
                    foreach (Tag next in adjacency[tag]) if (seen.Add(next)) queue.Enqueue(next);
                }
                double diagonal = Diagonal(component);
                if (diagonal <= (double)size.Value && component.Count < faces.Count)
                    groups.Add(component.ToArray());
            }
            rebuilding = true;
            try
            {
                for (int i = 0; i < groups.Count; i++)
                    list.Items.Add("Group " + (i + 1) + " - " + groups[i].Length + " faces; diagonal " + Diagonal(groups[i]).ToString("0.###"), true);
            }
            finally { rebuilding = false; }
            Preview();
            status.Text = groups.Count == 0 ? "No isolated groups within the size limit. Check the carrier face or increase the limit." :
                groups.Count + " groups. Review before applying.";
        }

        private double Diagonal(IEnumerable<Face> faces)
        {
            double[] low = { double.MaxValue, double.MaxValue, double.MaxValue };
            double[] high = { double.MinValue, double.MinValue, double.MinValue };
            foreach (Face face in faces)
            {
                var box = new double[6];
                context.UF.Modl.AskBoundingBox(face.Tag, box);
                for (int i = 0; i < 3; i++) { low[i] = Math.Min(low[i], box[i]); high[i] = Math.Max(high[i], box[i + 3]); }
            }
            return Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (high[i] - low[i]) * (high[i] - low[i])));
        }

        private void SetAll(bool value)
        {
            rebuilding = true;
            try { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, value); }
            finally { rebuilding = false; }
            Preview();
        }

        private void Preview()
        {
            if (IsDisposed) return;
            ClearPreview();
            for (int i = 0; i < groups.Count; i++)
                if (list.GetItemChecked(i)) foreach (Face face in groups[i]) context.UF.Disp.SetHighlight(face.Tag, 1);
            context.WorkPart.ModelingViews.WorkView.UpdateDisplay();
        }

        private void ClearPreview()
        {
            foreach (Face face in groups.SelectMany(g => g))
                try { face.Unhighlight(); context.UF.Disp.SetHighlight(face.Tag, 0); } catch (NXException) { }
            context.UF.Disp.Refresh();
        }

        private void Exclude()
        {
            Hide();
            try
            {
                while (true)
                {
                    var face = Pick("Pick any candidate face to exclude its connected group; Cancel to finish") as Face;
                    if (face == null) break;
                    int index = groups.FindIndex(g => g.Any(f => f.Tag == face.Tag));
                    if (index >= 0)
                    {
                        rebuilding = true;
                        try { list.SetItemChecked(index, false); }
                        finally { rebuilding = false; }
                    }
                    Preview();
                }
            }
            finally { Show(); Preview(); }
        }

        private void Apply(bool close)
        {
            var selected = groups.Where((g, i) => list.GetItemChecked(i)).SelectMany(g => g).ToArray();
            if (selected.Length == 0) { if (close) Close(); else status.Text = "No checked candidates."; return; }
            ClearPreview();
            var mark = context.Session.SetUndoMark(Session.MarkVisibility.Visible, "NX Refine - Remove Marking Faces");
            DeleteFaceBuilder builder = null;
            try
            {
                builder = context.WorkPart.Features.CreateDeleteFaceBuilder(null);
                builder.Type = DeleteFaceBuilder.SelectTypes.Face;
                builder.Heal = true;
                var rule = context.WorkPart.ScRuleFactory.CreateRuleFaceDumb(selected);
                builder.FaceCollector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                builder.CommitFeature();
            }
            catch
            {
                context.Session.UndoToMark(mark, "NX Refine - Remove Marking Faces");
                // Rollback can replace face handles. Require a fresh selection and scan.
                groups.Clear();
                list.Items.Clear();
                carrier = null;
                body = null;
                throw;
            }
            finally { if (builder != null) builder.Destroy(); }
            groups.Clear();
            list.Items.Clear();
            carrier = null;
            body = null;
            context.UF.Disp.Refresh();
            status.Text = "Applied. Select a carrier face for another pass. Cancel does not undo an applied pass; use NX Undo.";
            if (close) Close();
        }
    }
}
