using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static TYBIM_2025.DataObject;
using ComboBox = System.Windows.Forms.ComboBox;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace TYBIM_2025.AutoBuild
{
    public partial class LayersForm : System.Windows.Forms.Form
    {
        //外部事件處理:讀取其他的cs檔
        ExternalEvent m_externalEvent_CreateColumns; // 自動翻柱
        ExternalEvent m_externalEvent_CreateBeams;
        ExternalEvent m_externalEvent_CreateWalls; // 自動翻牆

        Options options = new Options
        {
            ComputeReferences = true, // 讓 GeometryObject 產生 Reference
            IncludeNonVisibleObjects = true,
            DetailLevel = ViewDetailLevel.Fine
        };
        /// <summary>
        /// 自訂ListView滾輪只有上下滑動
        /// </summary>
        public class NativeMethods
        {
            public const int GWL_STYLE = -16;
            public const int WS_HSCROLL = 0x00100000;
            [DllImport("user32.dll")]
            public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
            [DllImport("user32.dll")]
            public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        }

        public static string b_level_name; // 基準樓層名稱
        public static string t_level_name; // 頂部樓層名稱
        private Dictionary<string, double> levelElevations = new Dictionary<string, double>();
        public static List<LineInfo> lineInfos = new List<LineInfo>();
        public static List<string> layers = new List<string>(); // 儲存圖層名稱
        public static List<string> selectedLayers = new List<string>(); // 選取的圖層名稱
        public static bool byLevel = false; // 是否依樓層建立
        public static ElementId wallTypeId;
        internal class CadWallLine
        {
            public string Layer;
            public ElementId ImportId;
            public XYZ Start, End;
        }
        // A swing arc has two possible leaf positions. Only the radius aligned with a wall can close its gap.
        internal static List<CadWallLine> doorRadii = new List<CadWallLine>();
        internal static List<CadWallLine> wallLines = new List<CadWallLine>();
        internal static Dictionary<string, int> unsupportedWallCurves = new Dictionary<string, int>();
        internal static Document cadDocument;
        private readonly Dictionary<string, ElementId> wallTypes = new Dictionary<string, ElementId>();
        private readonly Dictionary<string, ElementId> familyTypes = new Dictionary<string, ElementId>();
        private readonly string elementType;
        public static ElementId beamSymbolId;
        public static ElementId columnImportId;

        internal bool HasPendingEvent
        {
            get { return m_externalEvent_CreateColumns.IsPending || m_externalEvent_CreateWalls.IsPending || m_externalEvent_CreateBeams.IsPending; }
        }

        private void HideHorizontalScrollBar(ListView listView)
        {
            int style = NativeMethods.GetWindowLong(listView.Handle, NativeMethods.GWL_STYLE);
            NativeMethods.SetWindowLong(listView.Handle, NativeMethods.GWL_STYLE, style & ~NativeMethods.WS_HSCROLL);
        }
        internal static Result OpenForm(ExternalCommandData commandData, string elementType)
        {
            if (commandData.Application.ActiveUIDocument == null) return Result.Cancelled;
            // 共用 CAD 資料，因此切換指令時關閉舊視窗，再讀取目前文件。
            var myForm = System.Windows.Forms.Application.OpenForms["LayersForm"];
            if (myForm is LayersForm pendingForm && pendingForm.HasPendingEvent)
            {
                myForm.BringToFront();
                return Result.Succeeded;
            }
            if (myForm != null) myForm.Close();
            LayersForm layersForm = new LayersForm(commandData.Application.ActiveUIDocument, elementType);
            layersForm.Show();

            return Result.Succeeded;
        }

        public LayersForm(UIDocument uidoc, string elementType)
        {
            this.elementType = elementType;
            InitializeComponent();
            ConfigureLayout();

            IExternalEventHandler handler_CreateColumns = new CreateColumns(); // 自動翻柱
            ExternalEvent externalEvent_CreateColumns = ExternalEvent.Create(handler_CreateColumns);
            m_externalEvent_CreateColumns = externalEvent_CreateColumns;
            IExternalEventHandler handler_CreateWalls = new CreateWalls(); // 自動翻牆
            ExternalEvent externalEvent_CreateWalls = ExternalEvent.Create(handler_CreateWalls);
            m_externalEvent_CreateWalls = externalEvent_CreateWalls;

            m_externalEvent_CreateBeams = ExternalEvent.Create(new CreateBeams());

            lineInfos = new List<LineInfo>();
            layers = new List<string>();
            layers = GetCADLayerLines(uidoc.Document); // 取得CAD圖層線條
            CreateLayerNames(layers); // 建立圖層名稱

            if (elementType == "牆") ConfigureWallSelection();
            if (elementType == "柱" || elementType == "牆")
            {
                groupBox1.Visible = false;
                int unusedWidth = groupBox3.Left - groupBox1.Left;
                MinimumSize = new Size(MinimumSize.Width - unusedWidth, MinimumSize.Height);
                Width -= unusedWidth;
            }
            if (elementType == "柱")
            {
                groupBox3.Text = "樓層與 CAD 連結";
                label3.Text = "CAD 連結";
                sureBtn.Text = "預覽柱";
            }

            // 基準樓層與頂部樓層
            List<Level> levels = new FilteredElementCollector(uidoc.Document).OfClass(typeof(Level)).Cast<Level>().OrderBy(x => x.Elevation).ToList();
            foreach (Level level in levels)
            {
                string level_name = level.Name;
                b_level_comboBox.Items.Add(level_name);
                t_level_comboBox.Items.Add(level_name);
                levelElevations[level_name] = level.Elevation; // 把樓層名稱跟高度綁定記錄起來
            }
            if (b_level_comboBox.SelectedIndex < 0)
            {
                b_level_comboBox.Text = "請選擇基準樓層";
            }
            if (elementType != "樑" && t_level_comboBox.SelectedIndex < 0)
            {
                t_level_comboBox.Text = "請選擇頂部樓層";
            }

            if (elementType == "牆")
            {
                foreach (WallType type in new FilteredElementCollector(uidoc.Document).OfClass(typeof(WallType)).Cast<WallType>()
                    .Where(t => t.Kind == WallKind.Basic).OrderBy(t => t.Name))
                    wallTypes[type.Name] = type.Id;
                type_comboBox.Items.Add("自動依 CAD 牆厚選型");
                type_comboBox.Items.AddRange(wallTypes.Keys.Cast<object>().ToArray());
            }
            else if (elementType == "柱")
            {
                foreach (var id in wallLines.Select(line => line.ImportId).Distinct())
                {
                    var cad = uidoc.Document.GetElement(id);
                    string display = cad.Category.Name + " [" + id + "]";
                    familyTypes[display] = id;
                    type_comboBox.Items.Add(display);
                }
            }
            else
            {
                ElementFilter filter = new ElementCategoryFilter(BuiltInCategory.OST_StructuralFraming);
                foreach (FamilySymbol symbol in new FilteredElementCollector(uidoc.Document).OfClass(typeof(FamilySymbol))
                    .WherePasses(filter).Cast<FamilySymbol>().OrderBy(s => s.FamilyName).ThenBy(s => s.Name))
                {
                    string category = symbol.Category.Id == new ElementId(BuiltInCategory.OST_Columns) ? "一般柱" : "結構柱";
                    string display = (elementType == "柱" ? "[" + category + "] " : "") + symbol.FamilyName + "：" + symbol.Name;
                    familyTypes[display] = symbol.Id;
                }
                type_comboBox.Items.AddRange(familyTypes.Keys.Cast<object>().ToArray());
            }
            if (type_comboBox.Items.Count > 0) type_comboBox.SelectedIndex = 0;

            // 調整下拉選單寬度
            AdjustComboBoxDropDownListWidth(b_level_comboBox);
            AdjustComboBoxDropDownListWidth(t_level_comboBox);
            AdjustComboBoxDropDownListWidth(type_comboBox);

            CenterToParent(); // 視窗置中

            // 設定ListView UI介面
            listView1.View = System.Windows.Forms.View.Details;
            foreach (ColumnHeader column in listView1.Columns) { column.Width = listView1.ClientSize.Width / listView1.Columns.Count; }
            HideHorizontalScrollBar(listView1); // 自訂ListView滾輪只有上下滑動

            if (b_level_comboBox.Items.Count > 0) b_level_comboBox.SelectedIndex = 0;
            if (t_level_comboBox.Items.Count > 1) t_level_comboBox.SelectedIndex = 1;
        }

        private void ConfigureWallSelection()
        {
            var recommended = new HashSet<string>();
            foreach (var import in wallLines.GroupBy(line => line.ImportId))
            {
                var lines = import.Where(line => Math.Abs(line.Start.Z - line.End.Z) <= 1 / 304.8)
                    .Select(line => new KeyValuePair<string, WallRegionGeometry.Segment>(line.Layer,
                        new WallRegionGeometry.Segment(new WallRegionGeometry.Point(line.Start.X, line.Start.Y),
                            new WallRegionGeometry.Point(line.End.X, line.End.Y))));
                var found = WallLayerDetector.AnalyzeWalls(lines, 1000 / 304.8)
                    .Where(layer => layer.Recommended).Select(layer => layer.Layer).ToList();
                // Numbered companion layers may only supply short returns or end caps.
                if (found.Contains("WALL", StringComparer.OrdinalIgnoreCase))
                {
                    var companions = lines.Select(line => line.Key).Distinct()
                        .Where(name => System.Text.RegularExpressions.Regex.IsMatch(name, @"^WALL[0-9]+$",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                    recommended.UnionWith(companions);
                }
                recommended.UnionWith(found);
                if (found.Count > 0)
                    recommended.UnionWith(import.Select(line => line.Layer).Where(WallRegionGeometry.IsOpeningLayer));
            }
            allRbtn.Checked = false;
            listView1.Columns[0].Width = Math.Max(130, listView1.ClientSize.Width - 84);
            listView1.Columns.Add("建議", 62);
            foreach (ListViewItem item in listView1.Items)
            {
                item.Checked = recommended.Contains(item.Text);
                item.SubItems.Add(item.Checked ? "推薦" : "");
            }
            groupBox1.Text = recommended.Count == 0 ? "請選擇要計算的圖層" : "計算圖層（已勾選推薦圖層）";
            sureBtn.Text = "預覽牆體";
        }

        private void ConfigureLayout()
        {
            Text = "自動翻" + elementType;
            bool isBeam = elementType == "樑";
            label1.Text = isBeam ? "參考樓層" : "基準樓層";
            label2.Visible = t_level_comboBox.Visible = byLevelCB.Visible = !isBeam;
            if (isBeam)
            {
                label3.Location = label2.Location;
                type_comboBox.Location = t_level_comboBox.Location;
            }
        }
        /// <summary>
        /// 調整下拉選單寬度
        /// </summary>
        /// <param name="senderComboBox"></param>
        private void AdjustComboBoxDropDownListWidth(ComboBox senderComboBox)
        {
            Graphics g = null;
            Font font = null;
            try
            {
                int width = senderComboBox.Width;
                g = senderComboBox.CreateGraphics();
                font = senderComboBox.Font;

                // checks if a scrollbar will be displayed.
                // if yes, then get its width to adjust the size of the drop down list.
                int vertScrollBarWidth =
                    (senderComboBox.Items.Count > senderComboBox.MaxDropDownItems)
                    ? SystemInformation.VerticalScrollBarWidth : 0;

                int newWidth;
                foreach (object s in senderComboBox.Items)  //Loop through list items and check size of each items.
                {
                    if (s != null)
                    {
                        newWidth = (int)g.MeasureString(s.ToString().Trim(), font).Width
                            + vertScrollBarWidth;
                        if (width < newWidth)
                        {
                            width = newWidth;   //set the width of the drop down list to the width of the largest item.
                        }
                    }
                }
                senderComboBox.DropDownWidth = Math.Min(width + 16, Screen.FromControl(senderComboBox).WorkingArea.Width - 32);
            }
            catch
            {

            }
            finally
            {
                if (g != null)
                {
                    g.Dispose();
                }
            }
        }
        /// <summary>
        /// 取得CAD圖層線條
        /// </summary>
        /// <param name="doc"></param>
        /// <returns></returns>
        private List<string> GetCADLayerLines(Document doc)
        {
            wallLines.Clear();
            doorRadii.Clear();
            unsupportedWallCurves.Clear();
            cadDocument = doc;
            List<ImportInstance> importInstances = new FilteredElementCollector(doc, doc.ActiveView.Id).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Where(x => x.Category != null).ToList();
            foreach (ImportInstance importInstance in importInstances)
            {
                if (importInstance.IsLinked)
                {
                    if (elementType == "牆")
                        foreach (Category category in importInstance.Category.SubCategories)
                            layers.Add(category.Name);
                    ReadCadGeometry(doc, importInstance.get_Geometry(options), Transform.Identity, null, importInstance.Id);
                }
            }
            if (elementType == "牆") layers.AddRange(wallLines.Select(line => line.Layer));
            layers = layers.Distinct().OrderBy(x => x).ToList(); // 排序

            return layers;
        }
        // CAD solid fills can arrive as planar faces or triangulated meshes.
        // Only their perimeter is a potential column outline.
        private void ReadColumnFace(Document doc, Face face, Transform transform, string fallback, ElementId importId, Dictionary<string, CadWallLine> boundary)
        {
            if (!(face is PlanarFace plane) || Math.Abs(transform.OfVector(plane.FaceNormal).Normalize().Z) < 0.99999) return;
            string layer = (doc.GetElement(face.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? fallback;
            if (string.IsNullOrEmpty(layer)) return;
            foreach (var loop in face.GetEdgesAsCurveLoops())
            {
                var curves = loop.ToList();
                if (!curves.All(c => c is Autodesk.Revit.DB.Line)) continue;
                foreach (var c in curves)
                    ToggleBoundary(boundary, layer, importId, transform.OfPoint(c.GetEndPoint(0)), transform.OfPoint(c.GetEndPoint(1)));
            }
        }

        private static void ToggleBoundary(Dictionary<string, CadWallLine> boundary, string layer, ElementId importId, XYZ a, XYZ b)
        {
            string Key(XYZ p) => Math.Round(p.X * 3048) + "|" + Math.Round(p.Y * 3048) + "|" + Math.Round(p.Z * 3048);
            string ka = Key(a), kb = Key(b);
            string key = layer + ":" + (string.CompareOrdinal(ka, kb) < 0 ? ka + "/" + kb : kb + "/" + ka);
            if (!boundary.Remove(key))
                boundary.Add(key, new CadWallLine { Layer = layer, ImportId = importId, Start = a, End = b });
        }

        private void ReadColumnMesh(Mesh mesh, Transform transform, string layer, ElementId importId)
        {
            if (string.IsNullOrEmpty(layer)) return;
            var boundary = new Dictionary<string, CadWallLine>();
            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                var triangle = mesh.get_Triangle(i);
                var points = Enumerable.Range(0, 3).Select(j => transform.OfPoint(triangle.get_Vertex(j))).ToArray();
                if (points.Max(p => p.Z) - points.Min(p => p.Z) > 1 / 304.8) continue;
                for (int j = 0; j < 3; j++)
                {
                    XYZ a = points[j], b = points[(j + 1) % 3];
                    ToggleBoundary(boundary, layer, importId, a, b);
                }
            }
            wallLines.AddRange(boundary.Values);
        }
        private void ReadCadGeometry(Document doc, GeometryElement geometry, Transform transform, string inheritedLayer, ElementId importId)
        {
            if (geometry == null) return;
            foreach (GeometryObject obj in geometry)
            {
                GraphicsStyle style = doc.GetElement(obj.GraphicsStyleId) as GraphicsStyle;
                string layer = style?.GraphicsStyleCategory?.Name ?? inheritedLayer;
                if (elementType == "牆" && layer == "0" && inheritedLayer != null && WallRegionGeometry.IsOpeningLayer(inheritedLayer))
                    layer = inheritedLayer;
                if (obj is GeometryInstance instance)
                {
                    ReadCadGeometry(doc, instance.GetSymbolGeometry(), transform.Multiply(instance.Transform), layer, importId);
                    continue;
                }
                if (elementType == "柱" || elementType == "牆")
                {
                    if (obj is Solid solid)
                    {
                        var boundary = new Dictionary<string, CadWallLine>();
                        foreach (Face face in solid.Faces) ReadColumnFace(doc, face, transform, layer, importId, boundary);
                        wallLines.AddRange(boundary.Values);
                        continue;
                    }
                    if (obj is Mesh mesh)
                    {
                        ReadColumnMesh(mesh, transform, layer, importId);
                        continue;
                    }
                }
                if (string.IsNullOrEmpty(layer)) continue;
                if (obj is PolyLine polyline)
                {
                    var points = polyline.GetCoordinates().Select(transform.OfPoint).ToList();
                    lineInfos.Add(new LineInfo { layerName = layer, polyLine = PolyLine.Create(points) });
                    for (int i = 1; i < points.Count; i++)
                        wallLines.Add(new CadWallLine { Layer = layer, ImportId = importId, Start = points[i - 1], End = points[i] });
                    layers.Add(layer);
                }
                else if (obj is Autodesk.Revit.DB.Line line && line.IsBound)
                {
                    wallLines.Add(new CadWallLine { Layer = layer, ImportId = importId,
                        Start = transform.OfPoint(line.GetEndPoint(0)), End = transform.OfPoint(line.GetEndPoint(1)) });
                    layers.Add(layer);
                }
                else if (elementType == "牆" && obj is Arc swing && swing.IsBound &&
                    WallRegionGeometry.IsOpeningLayer(layer) &&
                    Math.Abs(transform.OfVector(swing.Normal).Normalize().Z) > 0.9999 &&
                    Math.Abs(swing.Length / swing.Radius - Math.PI / 2) <= Math.PI / 12)
                {
                    XYZ hinge = transform.OfPoint(swing.Center);
                    for (int i = 0; i < 2; i++)
                        doorRadii.Add(new CadWallLine { Layer = layer, ImportId = importId,
                            Start = hinge, End = transform.OfPoint(swing.GetEndPoint(i)) });
                    layers.Add(layer);
                }
                else if (obj is Curve)
                {
                    if (!unsupportedWallCurves.ContainsKey(layer)) unsupportedWallCurves[layer] = 0;
                    unsupportedWallCurves[layer]++;
                    layers.Add(layer);
                }
            }
        }
        /// <summary>
        /// 建立圖層名稱
        /// </summary>
        /// <param name="layers"></param>
        private void CreateLayerNames(List<string> layers)
        {
            listView1.Columns.Clear(); // 清空欄位
            listView1.Items.Clear(); // 清空節點

            try
            {
                listView1.Columns.Add("圖層名稱");
                foreach (string layer in layers) { listView1.Items.Add(layer); }
            }
            catch(Exception ex) { MessageBox.Show("建立圖層名稱時發生錯誤: " + ex.Message); }

            listView1.View = System.Windows.Forms.View.Details;
            listView1.Columns[0].Width = -2;
            //// 測試, 預設WALL的線條先打勾
            //foreach (ListViewItem item in listView1.Items)
            //{
            //    if (item.Text.Equals("WALL") || item.Text.Contains("OPEN")) { item.Checked = true; }
            //}
        }
        // 全選
        private void allRbtn_CheckedChanged(object sender, EventArgs e)
        {
            if (sender is RadioButton button && !button.Checked) return;
            for (int i = 0; i < listView1.Items.Count; i++) { listView1.Items[i].Checked = true; }
        }
        // 全部取消
        private void allCancelRbtn_CheckedChanged(object sender, EventArgs e)
        {
            if (sender is RadioButton button && !button.Checked) return;
            for (int i = 0; i < listView1.Items.Count; i++) { listView1.Items[i].Checked = false; }
        }
        // 確定
        private void sureBtn_Click(object sender, System.EventArgs e)
        {
            if (HasPendingEvent) return;
            selectedLayers.Clear(); // 清空

            if (b_level_comboBox.SelectedIndex < 0)
            {
                MessageBox.Show(elementType == "樑" ? "請選擇參考樓層" : "請選擇基準樓層");
                return;
            }
            if (elementType != "樑" && t_level_comboBox.SelectedIndex < 0)
            {
                MessageBox.Show("請選擇頂部樓層");
                return;
            }
            if (elementType != "樑" && b_level_comboBox.Text == t_level_comboBox.Text)
            {
                MessageBox.Show("基準樓層與頂部樓層相同，請重新選擇！");
                return;
            }
            if (elementType != "樑" && levelElevations.ContainsKey(b_level_comboBox.Text) && levelElevations.ContainsKey(t_level_comboBox.Text))
            {
                double b_elevation = levelElevations[b_level_comboBox.Text];
                double t_elevation = levelElevations[t_level_comboBox.Text];

                if (t_elevation < b_elevation)
                {
                    MessageBox.Show("頂部樓層高度不能低於基準樓層，請重新選擇！");
                    return;
                }
            }

            if (type_comboBox.SelectedIndex < 0)
            {
                MessageBox.Show(elementType == "柱" ? "請選擇含有線段的 CAD 連結" : "請選擇要建立的類型");
                return;
            }

            try
            {
                selectedLayers = listView1.CheckedItems.Cast<ListViewItem>().Select(item => item.Text).ToList();
                if (elementType == "樑" && selectedLayers.Count == 0)
                {
                    MessageBox.Show("請至少選擇一個圖層。");
                    return;
                }
                byLevel = byLevelCB.Checked;
                b_level_name = b_level_comboBox.Text;
                t_level_name = t_level_comboBox.Text;
                if (elementType == "柱")
                {
                    columnImportId = familyTypes[type_comboBox.Text];
                    m_externalEvent_CreateColumns.Raise();
                }
                else if (elementType == "樑")
                {
                    beamSymbolId = familyTypes[type_comboBox.Text];
                    m_externalEvent_CreateBeams.Raise();
                }
                else
                {
                    wallTypeId = type_comboBox.SelectedIndex == 0 ? ElementId.InvalidElementId : wallTypes[type_comboBox.Text];
                    m_externalEvent_CreateWalls.Raise();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("發生錯誤: " + ex.Message);
            }
        }
        // 取消
        private void cancelBtn_Click(object sender, System.EventArgs e)
        {
            Close();
        }
    }
}
