using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TYBIM_2025.AutoBuild
{
    internal sealed class WallPreview : Form
    {
        internal HashSet<WallRegionGeometry.Segment> Excluded { get; } = new();
        internal WallPreview(List<LayersForm.CadWallLine> background, List<WallRegionGeometry.Segment> axes, int stories, List<WallRegionGeometry.Segment> additions, double? fixedWidth, HashSet<string> recommended, Action<HashSet<string>> recalculate)
        {
            Text = $"自動翻牆預覽 ｜ 每層 {axes.Count} 道 × {stories} 層，預計建立 {axes.Count * stories} 道";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 650);
            MinimumSize = new Size(540, 360);
            var canvas = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.White };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
            var create = new Button { Text = "建立牆", DialogResult = DialogResult.OK, AutoSize = true };
            var back = new Button { Text = "返回設定", DialogResult = DialogResult.Cancel, AutoSize = true };
            footer.Controls.AddRange(new Control[] { create, back, new Label { Text = "灰線為 CAD，紅色為牆體，橘色為柱面補判", AutoSize = true, Margin = new Padding(12, 8, 0, 0) } });
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 1, Margin = Padding.Empty };
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            canvas.Margin = Padding.Empty;
            body.Controls.Add(canvas, 0, 0);
            Controls.Add(body);
            Controls.Add(footer);
            CancelButton = back;
            var choices = new CheckedListBox { Dock = DockStyle.Right, Width = 190, CheckOnClick = true };
            body.ColumnCount = 2;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            var side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(side, 1, 0);
            side.Controls.Add(new Label { Text = "計算圖層（勾選即更新）", AutoSize = true }, 0, 0);
            var layers = new CheckedListBox { CheckOnClick = true, Dock = DockStyle.Fill, HorizontalScrollbar = true };
            var names = LayersForm.layers.OrderByDescending(recommended.Contains).ThenBy(name => name).ToList();
            foreach (string name in names)
                layers.Items.Add(recommended.Contains(name) ? name + "（推薦）" : name, recommended.Contains(name));
            side.Controls.Add(layers, 0, 1);
            choices.Dock = DockStyle.Fill;
            side.Controls.Add(choices, 0, 2);
            void UpdateCount()
            {
                int count = axes.Count - Excluded.Count;
                create.Enabled = count > 0;
                Text = $"自動翻牆預覽 ｜ 每層 {count} 道 × {stories} 層，預計建立 {count * stories} 道";
                canvas.Invalidate();
            }
            bool populating = false;
            void UpdateChoices()
            {
                populating = true;
                choices.Items.Clear();
                for (int i = 0; i < additions.Count; i++)
                    choices.Items.Add($"柱面補判 {i + 1}（{additions[i].WidthMm} mm）", !Excluded.Contains(additions[i]));
                choices.Visible = choices.Items.Count > 0;
                choices.Height = Math.Min(6, choices.Items.Count) * choices.ItemHeight + 6;
                populating = false;
                UpdateCount();
            }
            choices.ItemCheck += (sender, e) =>
            {
                if (populating) return;
                if (e.NewValue == CheckState.Checked) Excluded.Remove(additions[e.Index]);
                else Excluded.Add(additions[e.Index]);
                UpdateCount();
            };
            var selected = new HashSet<string>(recommended);
            layers.ItemCheck += (sender, e) =>
            {
                if (e.NewValue == CheckState.Checked) selected.Add(names[e.Index]);
                else selected.Remove(names[e.Index]);
                var previous = Excluded.Select(s => (s.A.X, s.A.Y, s.B.X, s.B.Y, s.WidthMm)).ToHashSet();
                try
                {
                    recalculate(selected);
                    Excluded.Clear();
                    foreach (var s in additions)
                        if (previous.Contains((s.A.X, s.A.Y, s.B.X, s.B.Y, s.WidthMm))) Excluded.Add(s);
                }
                catch (Exception ex)
                {
                    axes.Clear();
                    additions.Clear();
                    Excluded.Clear();
                    MessageBox.Show(this, ex.Message, "牆體預覽計算失敗");
                }
                UpdateChoices();
            };
            UpdateChoices();
            double left = double.PositiveInfinity, right = double.NegativeInfinity;
            double bottom = double.PositiveInfinity, top = double.NegativeInfinity;
            foreach (var line in background)
            {
                left = Math.Min(left, Math.Min(line.Start.X, line.End.X));
                right = Math.Max(right, Math.Max(line.Start.X, line.End.X));
                bottom = Math.Min(bottom, Math.Min(line.Start.Y, line.End.Y));
                top = Math.Max(top, Math.Max(line.Start.Y, line.End.Y));
            }
            canvas.Resize += (sender, args) => canvas.Invalidate();
            canvas.Paint += (sender, args) =>
            {
                if (canvas.ClientSize.Width <= 32 || canvas.ClientSize.Height <= 32) return;
                double scale = Math.Min((canvas.Width - 32) / Math.Max(right - left, 0.01),
                    (canvas.Height - 32) / Math.Max(top - bottom, 0.01));
                PointF Map(double x, double y) => new PointF((float)(canvas.Width / 2.0 + (x - (left + right) / 2) * scale),
                    (float)(canvas.Height / 2.0 - (y - (top + bottom) / 2) * scale));
                foreach (var line in background)
                    args.Graphics.DrawLine(Pens.LightGray, Map(line.Start.X, line.Start.Y), Map(line.End.X, line.End.Y));
                using var pen = new Pen(Color.Firebrick, 2);
                using var fill = new SolidBrush(Color.FromArgb(75, Color.Firebrick));
                using var inferredFill = new SolidBrush(Color.FromArgb(85, Color.DarkOrange));
                foreach (var axis in axes)
                {
                    int index = additions.IndexOf(axis);
                    var a = Map(axis.A.X, axis.A.Y);
                    var b = Map(axis.B.X, axis.B.Y);
                    if (Excluded.Contains(axis)) continue;
                    double halfWidth = (fixedWidth ?? axis.WidthMm / 304.8) / 2;
                    var normal = new WallRegionGeometry.Point(axis.A.Y - axis.B.Y, axis.B.X - axis.A.X) * (halfWidth / axis.Length);
                    var corners = new[] { axis.A + normal, axis.B + normal, axis.B - normal, axis.A - normal };
                    args.Graphics.FillPolygon(index >= 0 ? inferredFill : fill, corners.Select(p => Map(p.X, p.Y)).ToArray());
                    args.Graphics.DrawLine(index >= 0 ? Pens.DarkOrange : pen, a, b);
                    if (index >= 0) args.Graphics.DrawString((index + 1).ToString(), Font, Brushes.DarkOrange,
                        (a.X + b.X) / 2, (a.Y + b.Y) / 2);
                }
            };
        }
    }
}
