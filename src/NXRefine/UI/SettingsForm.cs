using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using NXRefine.Core;

namespace NXRefine.UI
{
    internal sealed class SettingsForm : Form
    {
        private readonly CleanupSettings settings;
        private readonly TextBox smallFace = NewTextBox();
        private readonly TextBox shortEdge = NewTextBox();
        private readonly TextBox blendRadius = NewTextBox();
        private readonly TextBox holeDiameter = NewTextBox();
        private readonly TextBox sewTolerance = NewTextBox();
        private readonly TextBox sharpAngle = NewTextBox();
        private readonly CheckBox confirm = new CheckBox { Text = "Confirm before modifying geometry", AutoSize = true };

        public SettingsForm(CleanupSettings settings)
        {
            this.settings = settings;
            Text = "NX Refine Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(430, 310);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 8 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            AddRow(layout, 0, "Small face area", smallFace);
            AddRow(layout, 1, "Short edge length", shortEdge);
            AddRow(layout, 2, "Maximum blend radius", blendRadius);
            AddRow(layout, 3, "Maximum hole diameter", holeDiameter);
            AddRow(layout, 4, "Sew tolerance", sewTolerance);
            AddRow(layout, 5, "Sharp angle (degrees)", sharpAngle);
            layout.Controls.Add(confirm, 0, 6);
            layout.SetColumnSpan(confirm, 2);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            save.Click += SaveClicked;
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons, 0, 7);
            layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout);
            AcceptButton = save;
            CancelButton = cancel;

            smallFace.Text = Format(settings.SmallFaceArea);
            shortEdge.Text = Format(settings.ShortEdgeLength);
            blendRadius.Text = Format(settings.MaxBlendRadius);
            holeDiameter.Text = Format(settings.MaxHoleDiameter);
            sewTolerance.Text = Format(settings.SewTolerance);
            sharpAngle.Text = Format(settings.SharpAngle);
            confirm.Checked = settings.ConfirmBeforeRepair;
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            double a, b, c, d, f, g;
            if (!Read(smallFace, out a) || !Read(shortEdge, out b) || !Read(blendRadius, out c) ||
                !Read(holeDiameter, out d) || !Read(sewTolerance, out f) || !Read(sharpAngle, out g))
            {
                MessageBox.Show("Enter non-negative numbers using a period as the decimal separator.", "NX Refine", MessageBoxButtons.OK, MessageBoxIcon.Error);
                DialogResult = DialogResult.None;
                return;
            }
            settings.SmallFaceArea = a;
            settings.ShortEdgeLength = b;
            settings.MaxBlendRadius = c;
            settings.MaxHoleDiameter = d;
            settings.SewTolerance = f;
            settings.SharpAngle = g;
            settings.ConfirmBeforeRepair = confirm.Checked;
            settings.Save();
        }

        private static TextBox NewTextBox() { return new TextBox { Dock = DockStyle.Fill }; }
        private static string Format(double value) { return value.ToString("0.########", CultureInfo.InvariantCulture); }
        private static bool Read(TextBox box, out double value) { return double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0; }
        private static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            layout.Controls.Add(control, 1, row);
        }
    }
}
