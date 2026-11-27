using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TYBIM.AutoBuild
{
    internal sealed class BeamPreview : Form
    {
        private readonly CheckedListBox candidates = new CheckedListBox { CheckOnClick = true, Dock = DockStyle.Fill, HorizontalScrollbar = true };
        private BeamGeometry.Result result;
        internal IEnumerable<BeamGeometry.Candidate> Approved =>
            candidates.CheckedIndices.Cast<int>().Select(index => result.Candidates[index]);

        internal BeamPreview(string settings, string[] sources, HashSet<string> initialLayers,
            Func<int, IReadOnlyList<string>> getLayers, Func<int, IReadOnlyList<LayersForm.CadWallLine>> getBackground,
            Func<int, HashSet<string>, bool, double, double, BeamGeometry.Result> calculate)
        {
            Text = "自動翻樑預覽";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1080, 690);
            MinimumSize = new Size(800, 560);

            var canvas = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.White, Cursor = Cursors.Hand };
            var layers = new CheckedListBox { CheckOnClick = true, Dock = DockStyle.Fill, HorizontalScrollbar = true };
            var source = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            source.Items.AddRange(sources);
            var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
            mode.Items.AddRange(new object[] { "雙線取中", "單線中心線" });
            mode.SelectedIndex = 0;
            var minWidth = new NumericUpDown { Minimum = 10, Maximum = 5000, Value = 100, Width = 68 };
            var maxWidth = new NumericUpDown { Minimum = 10, Maximum = 5000, Value = 1500, Width = 68 };
            var create = new Button { Text = "建立已勾選樑", AutoSize = true, DialogResult = DialogResult.OK };
            var back = new Button { Text = "返回設定", AutoSize = true, DialogResult = DialogResult.Cancel };
            var fit = new Button { Text = "全圖", AutoSize = true };
            var selectArea = new CheckBox { Text = "框選本次要建立的樑", Appearance = Appearance.Button, AutoSize = true };
            var count = Label("");
            var status = Label("");
            var range = Flow(Label("判讀方式"), mode, Label("CAD 樑寬（mm）"), minWidth, Label("至"), maxWidth);
            var footer = Flow(create, back, fit, selectArea, count);
            var side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 37));
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 63));
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            side.Controls.Add(Label("計算圖層（勾選即更新）"), 0, 0);
            side.Controls.Add(layers, 0, 1);
            side.Controls.Add(Label("建立清單（點整列切換，空白鍵亦可）"), 0, 2);
            side.Controls.Add(candidates, 0, 3);
            var checkAll = new Button { Text = "全勾", AutoSize = true };
            var clearAll = new Button { Text = "全取消", AutoSize = true };
            var clearArea = new Button { Text = "清除框選", AutoSize = true };
            side.Controls.Add(Flow(checkAll, clearAll, clearArea), 0, 4);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 2, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290));
            for (int row = 0; row < 7; row++)
                layout.RowStyles.Add(row == 4 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            void AddWide(Control control, int row)
            {
                layout.Controls.Add(control, 0, row);
                layout.SetColumnSpan(control, 2);
            }
            AddWide(Label(settings), 0);
            AddWide(Label("使用目前視圖的 CAD 連結"), 1);
            AddWide(source, 2);
            AddWide(range, 3);
            layout.Controls.Add(canvas, 0, 4);
            layout.Controls.Add(side, 1, 4);
            AddWide(status, 5);
            AddWide(footer, 6);
            Controls.Add(layout);
            CancelButton = back;

            IReadOnlyList<LayersForm.CadWallLine> background = Array.Empty<LayersForm.CadWallLine>();
            IReadOnlyList<string> names = Array.Empty<string>();
            var selected = new HashSet<string>();
            var selectionsBySource = new Dictionary<int, HashSet<string>>();
            var overrides = new Dictionary<(int, int, string, double, double, double, double, double), bool>();
            var areas = new Dictionary<int, (double Left, double Right, double Bottom, double Top)>();
            bool updating = false;
            double centerX = 0, centerY = 0, spanX = 1, spanY = 1, zoom = 1;
            PointF pan = PointF.Empty;
            System.Drawing.Point drag = System.Drawing.Point.Empty;
            System.Drawing.Point areaEnd = System.Drawing.Point.Empty;
            double Scale() => zoom * Math.Min(Math.Max(1, canvas.Width - 32) / spanX, Math.Max(1, canvas.Height - 32) / spanY);
            PointF Map(double x, double y, double scale) => new PointF(
                (float)(canvas.Width * .5 + (x - centerX) * scale + pan.X),
                (float)(canvas.Height * .5 - (y - centerY) * scale + pan.Y));

            (int, int, string, double, double, double, double, double) Key(BeamGeometry.Candidate item) =>
                (source.SelectedIndex, mode.SelectedIndex, item.Layer, item.Axis.A.X, item.Axis.A.Y, item.Axis.B.X, item.Axis.B.Y, item.Width);

            bool IsChecked(BeamGeometry.Candidate item)
            {
                if (overrides.TryGetValue(Key(item), out bool check)) return check;
                if (!areas.TryGetValue(source.SelectedIndex, out var area)) return true;
                bool Contains(WallRegionGeometry.Point p) => p.X >= area.Left && p.X <= area.Right &&
                    p.Y >= area.Bottom && p.Y <= area.Top;
                return Contains(item.Axis.A) && Contains(item.Axis.B);
            }

            void UpdateCount(int approved)
            {
                create.Enabled = approved > 0;
                count.Text = $"已勾選 {approved} / {result.Candidates.Count} 支 ｜ 綠線為已勾選樑軸";
                canvas.Invalidate();
            }

            void Recalculate()
            {
                bool pairEdges = mode.SelectedIndex == 0;
                minWidth.Enabled = maxWidth.Enabled = pairEdges;
                bool valid = !pairEdges || minWidth.Value <= maxWidth.Value;
                result = valid ? calculate(source.SelectedIndex, selected, pairEdges,
                    (double)minWidth.Value / 304.8, (double)maxWidth.Value / 304.8) : new BeamGeometry.Result();
                status.Text = !valid ? "最小寬度須小於或等於最大寬度。" : result.Summary +
                    (selected.Count == 0 ? " ｜ 請勾選樑圖層。" :
                    result.Candidates.Count == 0 && pairEdges ? " ｜ 無可建立的雙線配對，請檢查圖層、單位與寬度。" : "");
                updating = true;
                candidates.BeginUpdate();
                candidates.Items.Clear();
                for (int i = 0; i < result.Candidates.Count; i++)
                {
                    var item = result.Candidates[i];
                    string width = pairEdges ? $" ｜ CAD 寬 {item.Width * 304.8:0} mm" : "";
                    candidates.Items.Add($"{i + 1}. {item.Layer} ｜ 長 {item.Axis.Length * 304.8:0} mm{width}", IsChecked(item));
                }
                candidates.EndUpdate();
                updating = false;
                clearArea.Enabled = areas.ContainsKey(source.SelectedIndex);
                UpdateCount(candidates.CheckedItems.Count);
            }

            void ResetView() { zoom = 1; pan = PointF.Empty; canvas.Invalidate(); }
            source.SelectedIndexChanged += (sender, args) =>
            {
                int index = source.SelectedIndex;
                names = getLayers(index);
                background = getBackground(index);
                if (!selectionsBySource.TryGetValue(index, out selected))
                {
                    selected = new HashSet<string>(initialLayers.Where(names.Contains));
                    selectionsBySource.Add(index, selected);
                }
                updating = true;
                layers.Items.Clear();
                foreach (string name in names) layers.Items.Add(name, selected.Contains(name));
                updating = false;
                if (background.Count > 0)
                {
                    double left = background.Min(line => Math.Min(line.Start.X, line.End.X));
                    double right = background.Max(line => Math.Max(line.Start.X, line.End.X));
                    double bottom = background.Min(line => Math.Min(line.Start.Y, line.End.Y));
                    double top = background.Max(line => Math.Max(line.Start.Y, line.End.Y));
                    centerX = (left + right) / 2;
                    centerY = (bottom + top) / 2;
                    spanX = Math.Max(right - left, 0.01);
                    spanY = Math.Max(top - bottom, 0.01);
                }
                ResetView();
                Recalculate();
            };
            mode.SelectedIndexChanged += (sender, args) => Recalculate();
            minWidth.ValueChanged += (sender, args) => Recalculate();
            maxWidth.ValueChanged += (sender, args) => Recalculate();
            layers.ItemCheck += (sender, e) =>
            {
                if (updating) return;
                if (e.NewValue == CheckState.Checked) selected.Add(names[e.Index]);
                else selected.Remove(names[e.Index]);
                Recalculate();
            };
            candidates.ItemCheck += (sender, e) =>
            {
                if (updating) return;
                overrides[Key(result.Candidates[e.Index])] = e.NewValue == CheckState.Checked;
                UpdateCount(candidates.CheckedItems.Count + (e.NewValue == CheckState.Checked ? 1 : -1));
            };
            void SetAll(bool check)
            {
                candidates.BeginUpdate();
                for (int i = 0; i < candidates.Items.Count; i++)
                    if (candidates.GetItemChecked(i) != check) candidates.SetItemChecked(i, check);
                candidates.EndUpdate();
            }
            Rectangle Area() => Rectangle.FromLTRB(Math.Min(drag.X, areaEnd.X), Math.Min(drag.Y, areaEnd.Y),
                Math.Max(drag.X, areaEnd.X), Math.Max(drag.Y, areaEnd.Y));
            checkAll.Click += (sender, args) => SetAll(true);
            clearAll.Click += (sender, args) => SetAll(false);
            void ResetOverrides()
            {
                foreach (var key in overrides.Keys.Where(key => key.Item1 == source.SelectedIndex).ToList())
                    overrides.Remove(key);
            }
            clearArea.Click += (sender, args) =>
            {
                areas.Remove(source.SelectedIndex);
                ResetOverrides();
                Recalculate();
            };
            candidates.SelectedIndexChanged += (sender, args) => canvas.Invalidate();
            fit.Click += (sender, args) => ResetView();
            selectArea.CheckedChanged += (sender, args) => canvas.Cursor = selectArea.Checked ? Cursors.Cross : Cursors.Hand;
            canvas.Resize += (sender, args) => canvas.Invalidate();
            MouseWheel += (sender, args) =>
            {
                var mouse = canvas.PointToClient(MousePosition);
                if (!canvas.ClientRectangle.Contains(mouse)) return;
                double next = Math.Max(0.5, Math.Min(100, zoom * Math.Pow(1.25, args.Delta / 120.0)));
                double factor = next / zoom;
                pan = new PointF((float)(mouse.X - canvas.Width * .5 + (pan.X - mouse.X + canvas.Width * .5) * factor),
                    (float)(mouse.Y - canvas.Height * .5 + (pan.Y - mouse.Y + canvas.Height * .5) * factor));
                zoom = next;
                canvas.Invalidate();
            };
            canvas.MouseDown += (sender, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                canvas.Capture = true;
                drag = areaEnd = e.Location;
            };
            canvas.MouseMove += (sender, e) =>
            {
                if (!canvas.Capture || e.Button != MouseButtons.Left) return;
                if (selectArea.Checked) areaEnd = e.Location;
                else
                {
                    pan = new PointF(pan.X + e.X - drag.X, pan.Y + e.Y - drag.Y);
                    drag = e.Location;
                }
                canvas.Invalidate();
            };
            canvas.MouseUp += (sender, e) =>
            {
                canvas.Capture = false;
                if (!selectArea.Checked || e.Button != MouseButtons.Left) return;
                areaEnd = e.Location;
                RectangleF area = Area();
                if (area.Width >= 4 && area.Height >= 4)
                {
                    double scale = Scale();
                    double ToX(float x) => centerX + (x - canvas.Width * .5 - pan.X) / scale;
                    double ToY(float y) => centerY - (y - canvas.Height * .5 - pan.Y) / scale;
                    areas[source.SelectedIndex] = (ToX(area.Left), ToX(area.Right), ToY(area.Bottom), ToY(area.Top));
                    ResetOverrides();
                    Recalculate();
                    selectArea.Checked = false;
                }
                canvas.Invalidate();
            };
            canvas.Paint += (sender, e) =>
            {
                double scale = Scale();
                foreach (var line in background)
                    e.Graphics.DrawLine(Pens.Gainsboro, Map(line.Start.X, line.Start.Y, scale), Map(line.End.X, line.End.Y, scale));
                using var approvedPen = new Pen(Color.SeaGreen, 2);
                using var focusPen = new Pen(Color.DarkOrange, 3);
                for (int i = 0; i < result.Candidates.Count; i++)
                {
                    var item = result.Candidates[i];
                    bool focused = i == candidates.SelectedIndex;
                    if (!focused && !candidates.GetItemChecked(i)) continue;
                    var axis = item.Axis;
                    if (item.Width > 0)
                    {
                        var normal = new WallRegionGeometry.Point(axis.A.Y - axis.B.Y, axis.B.X - axis.A.X) *
                            (item.Width / (2 * axis.Length));
                        var corners = new[] { axis.A + normal, axis.B + normal, axis.B - normal, axis.A - normal };
                        e.Graphics.FillPolygon(Brushes.Honeydew, corners.Select(p => Map(p.X, p.Y, scale)).ToArray());
                    }
                    e.Graphics.DrawLine(focused ? focusPen : approvedPen, Map(axis.A.X, axis.A.Y, scale), Map(axis.B.X, axis.B.Y, scale));
                }
                if (areas.TryGetValue(source.SelectedIndex, out var area))
                {
                    var a = Map(area.Left, area.Top, scale);
                    var b = Map(area.Right, area.Bottom, scale);
                    e.Graphics.DrawRectangle(Pens.SteelBlue, a.X, a.Y, b.X - a.X, b.Y - a.Y);
                }
                if (selectArea.Checked && canvas.Capture)
                    e.Graphics.DrawRectangle(Pens.SteelBlue, Area());
            };
            source.SelectedIndex = 0;
        }

        private static Label Label(string text) => new Label { AutoSize = true, Text = text, Margin = new Padding(3, 6, 3, 5) };

        private static FlowLayoutPanel Flow(params Control[] controls)
        {
            var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            panel.Controls.AddRange(controls);
            return panel;
        }
    }
}
