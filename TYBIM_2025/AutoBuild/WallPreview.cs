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
        internal WallPreview(List<LayersForm.CadWallLine> background, List<WallRegionGeometry.Segment> axes, int stories, List<WallRegionGeometry.Segment> additions)
        {
            Text = $"自動翻牆預覽 ｜ 每層 {axes.Count} 道 × {stories} 層，預計建立 {axes.Count * stories} 道";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 650);
            MinimumSize = new Size(540, 360);
            var canvas = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.White };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
            var create = new Button { Text = "建立牆", DialogResult = DialogResult.OK, AutoSize = true };
            var back = new Button { Text = "返回調整圖層", DialogResult = DialogResult.Cancel, AutoSize = true };
            footer.Controls.AddRange(new Control[] { create, back, new Label { Text = "灰線為 CAD，紅線為將建立的牆中心線", AutoSize = true, Margin = new Padding(12, 8, 0, 0) } });
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 1, Margin = Padding.Empty };
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            canvas.Margin = Padding.Empty;
            body.Controls.Add(canvas, 0, 0);
            Controls.Add(body);
            Controls.Add(footer);
            CancelButton = back;
            var choices = new CheckedListBox { Dock = DockStyle.Right, Width = 190, CheckOnClick = true };
            if (additions.Count > 0)
            {
                body.ColumnCount = 2;
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, choices.Width));
                choices.Dock = DockStyle.Fill;
                choices.Margin = Padding.Empty;
                body.Controls.Add(choices, 1, 0);
                for (int i = 0; i < additions.Count; i++)
                    choices.Items.Add($"柱面補判 {i + 1}（{additions[i].WidthMm} mm）", true);
                choices.ItemCheck += (sender, e) =>
                {
                    if (e.NewValue == CheckState.Checked) Excluded.Remove(additions[e.Index]);
                    else Excluded.Add(additions[e.Index]);
                    int count = axes.Count - Excluded.Count;
                    create.Enabled = count > 0;
                    Text = $"自動翻牆預覽 ｜ 每層 {count} 道 × {stories} 層，預計建立 {count * stories} 道";
                    canvas.Invalidate();
                };
            }
            var points = axes.SelectMany(s => new[] { s.A, s.B }).ToList();
            double left = points.Min(p => p.X), right = points.Max(p => p.X);
            double bottom = points.Min(p => p.Y), top = points.Max(p => p.Y);
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
                foreach (var axis in axes)
                {
                    int index = additions.IndexOf(axis);
                    var a = Map(axis.A.X, axis.A.Y);
                    var b = Map(axis.B.X, axis.B.Y);
                    if (Excluded.Contains(axis)) continue;
                    args.Graphics.DrawLine(index >= 0 ? Pens.DarkOrange : pen, a, b);
                    if (index >= 0) args.Graphics.DrawString((index + 1).ToString(), Font, Brushes.DarkOrange,
                        (a.X + b.X) / 2, (a.Y + b.Y) / 2);
                }
            };
        }
    }
}


