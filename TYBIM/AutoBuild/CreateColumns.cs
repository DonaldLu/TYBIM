using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using D = System.Drawing;
using F = System.Windows.Forms;

namespace TYBIM.AutoBuild
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class CreateColumns : IExternalCommand, IExternalEventHandler
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return LayersForm.OpenForm(commandData, "柱");
        }

        private const double Mm = 1.0 / 304.8;
        private const string Skip = "略過";
        private static readonly Guid MappingId = new Guid("de5f500c-1992-40d8-b4db-87b215ea23bc");
        private class Candidate
        {
            public string Layer, Frame = "";
            public XYZ Point;
            public int Width, Depth;
            public double Angle;
        }

        private static string ChoiceKey(Candidate c) =>
            FormattableString.Invariant($"{c.Width}|{c.Depth}|{c.Frame}|{c.Layer}");

        private static string Vertex(XYZ p) =>
            Math.Round(p.X / Mm, 1) + "|" + Math.Round(p.Y / Mm, 1) + "|" + Math.Round(p.Z / Mm, 1);
        private static string Position(XYZ p) => Math.Round(p.X / Mm) + "|" + Math.Round(p.Y / Mm);
        private static IEnumerable<Candidate> FindRectangles()
        {
            foreach (var layer in LayersForm.wallLines.Where(l => l.ImportId == LayersForm.columnImportId).GroupBy(l => l.Layer))
            {
                var edges = new Dictionary<string, HashSet<string>>();
                var points = new Dictionary<string, XYZ>();
                foreach (var line in layer)
                {
                    string a = Vertex(line.Start), b = Vertex(line.End);
                    if (a == b) continue;
                    points[a] = line.Start;
                    points[b] = line.End;
                    if (!edges.ContainsKey(a)) edges[a] = new HashSet<string>();
                    if (!edges.ContainsKey(b)) edges[b] = new HashSet<string>();
                    edges[a].Add(b);
                    edges[b].Add(a);
                }
                var visited = new HashSet<string>();
                foreach (string start in edges.Keys)
                {
                    if (visited.Contains(start) || edges[start].Count != 2) continue;
                    var path = new List<string>();
                    string prev = null, current = start;
                    while (path.Count < 5 && edges[current].Count == 2 && visited.Add(current))
                    {
                        path.Add(current);
                        string next = edges[current].First(n => n != prev);
                        prev = current;
                        current = next;
                    }
                    if (current != start || path.Count != 4) continue;
                    var p = path.Select(k => points[k]).ToArray();
                    if (p.Max(v => v.Z) - p.Min(v => v.Z) > Mm) continue;
                    var sides = Enumerable.Range(0, 4).Select(i => p[(i + 1) % 4] - p[i]).ToArray();
                    if (sides.Any(v => v.GetLength() < Mm) ||
                        Enumerable.Range(0, 4).Any(i => Math.Abs(sides[i].Normalize().DotProduct(sides[(i+1)%4].Normalize())) > 0.01) ||
                        (sides[0] + sides[2]).GetLength() > Mm || (sides[1] + sides[3]).GetLength() > Mm) continue;
                    var sorted = sides.Take(2).OrderBy(v => v.GetLength()).ToArray();
                    yield return new Candidate { Layer = layer.Key, Point = (p[0]+p[2])*0.5,
                        Width = (int)Math.Round(sorted[0].GetLength()/Mm), Depth = (int)Math.Round(sorted[1].GetLength()/Mm),
                        Angle = Math.Atan2(sorted[0].Y, sorted[0].X) };
                }
            }
        }

        private static Parameter[] Dimensions(FamilySymbol symbol)
        {
            foreach (var names in new[] { new[] {"柱寬","柱深"}, new[] {"b","h"} })
            {
                var pair = names.Select(symbol.LookupParameter).ToArray();
                if (pair.All(p => p != null && p.StorageType == StorageType.Double &&
                    p.Definition.GetDataType() == SpecTypeId.Length)) return pair;
            }
            return null;
        }

        private static bool Matches(FamilySymbol s, Candidate c)
        {
            var d = Dimensions(s);
            return d != null && Math.Abs(d[0].AsDouble()/Mm-c.Width) <= 0.5 &&
                Math.Abs(d[1].AsDouble()/Mm-c.Depth) <= 0.5;
        }

        private static FamilySymbol Resolve(Document doc, List<FamilySymbol> types, Candidate c)
        {
            var existing = types.FirstOrDefault(s => Matches(s,c));
            if (existing != null) return existing;
            string name = c.Width + " x " + c.Depth + " mm";
            string unique = name;
            int suffix = 1;
            var seed = types[0];
            var names = new HashSet<string>(seed.Family.GetFamilySymbolIds().Select(id => doc.GetElement(id).Name), StringComparer.OrdinalIgnoreCase);
            while (names.Contains(unique)) unique = name + " (" + ++suffix + ")";
            var symbol = (FamilySymbol)seed.Duplicate(unique);
            var d = Dimensions(symbol);
            if (d.Any(p => p.IsReadOnly) || !d[0].Set(c.Width*Mm) || !d[1].Set(c.Depth*Mm))
                throw new InvalidOperationException("無法寫入柱寬深參數");
            doc.Regenerate();
            if (!Matches(symbol,c)) throw new InvalidOperationException("族群實際尺寸與 CAD 不符");
            return symbol;
        }

        private static bool Architectural(FamilySymbol s) => s.Category.Id == new ElementId(BuiltInCategory.OST_Columns);
        private static bool TypicalSection(Candidate c) =>
            c.Width >= 200 && c.Depth <= 2000 && c.Depth <= 4 * c.Width;

        private static string Suggest(Candidate c, bool repeatedSection, Dictionary<string,List<FamilySymbol>> families)
        {
            string layer = c.Layer;
            if (Regex.IsMatch(layer, @"DOOR|WINDOW|OPEN|ROOM|FURN|門|窗|空間|家具", RegexOptions.IgnoreCase))
                return Skip;
            bool named = Regex.IsMatch(layer, @"柱|(?:^|[-_ ])(?:COR|COL|COLS|COLUMN|COLUMNS)(?:$|[-_ ])", RegexOptions.IgnoreCase);
            if (!named && (Regex.IsMatch(layer, "HATCH|填充", RegexOptions.IgnoreCase) ||
                !(TypicalSection(c) && repeatedSection))) return Skip;
            bool architectural = Regex.IsMatch(layer, @"建築柱|(?:^|[-_ ])A[-_ ](?:COL|COR)", RegexOptions.IgnoreCase);
            var choices = families.Where(f => Architectural(f.Value[0]) == architectural)
                .Where(f => f.Value.Any(s => Matches(s,c)) || Dimensions(f.Value[0]).All(p => !p.IsReadOnly))
                .OrderByDescending(f => Regex.IsMatch(f.Value[0].FamilyName, @"矩形|長方形|rectang", RegexOptions.IgnoreCase))
                .ThenByDescending(f => f.Value.Any(s => Matches(s,c)))
                .ThenByDescending(f => Regex.IsMatch(f.Value[0].FamilyName, @"混凝土|concrete", RegexOptions.IgnoreCase))
                .ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase);
            return choices.Select(f => f.Key).FirstOrDefault() ?? Skip;
        }
        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null || !doc.Equals(LayersForm.cadDocument)) { TaskDialog.Show("自動翻柱","請在目前文件重新開啟翻柱。"); return; }
            try { Preview(doc); }
            catch (Exception e) { TaskDialog.Show("自動翻柱", e.Message); }
        }

        private static void Preview(Document doc)
        {
            var candidates = FindRectangles().ToList();
            if (candidates.Count == 0) { TaskDialog.Show("自動翻柱","此 CAD 未找到獨立的閉合矩形。"); return; }
            foreach (var same in candidates.GroupBy(c => Position(c.Point)).Where(g => g.Count()>1))
            {
                var sizes = same.Select(c => (long)c.Width*c.Depth).Distinct().OrderBy(a=>a).ToList();
                foreach (var c in same)
                    c.Frame = sizes.Count == 1 ? "重合框" : (long)c.Width*c.Depth == sizes[0] ? "同心小框" : "同心大框";
            }
            var filter = new LogicalOrFilter(new ElementCategoryFilter(BuiltInCategory.OST_Columns),new ElementCategoryFilter(BuiltInCategory.OST_StructuralColumns));
            var families = new FilteredElementCollector(doc).WherePasses(filter).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => Dimensions(s)!=null).OrderBy(s=>s.Name)
                .GroupBy(s=>(Architectural(s)?"[建築柱] ":"[結構柱] ")+s.FamilyName).ToDictionary(g=>g.Key,g=>g.ToList());
            if (families.Count == 0) { TaskDialog.Show("自動翻柱","請載入具有柱寬／柱深或 b／h 長度參數的矩形柱族群。"); return; }
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>l.Elevation).ToList();
            var first = levels.Single(l=>l.Name==LayersForm.b_level_name);
            var last = levels.Single(l=>l.Name==LayersForm.t_level_name);
            levels = levels.Where(l=>l.Elevation>first.Elevation && l.Elevation<last.Elevation && LayersForm.byLevel)
                .GroupBy(l=>l.Elevation).Select(g=>g.First()).ToList();
            levels.Insert(0,first);
            levels.Add(last);
            var occupied = new HashSet<string>();
            foreach (var col in new FilteredElementCollector(doc).WherePasses(filter).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                if (col.Location is LocationPoint loc)
                    occupied.Add(col.LevelId+"|"+Position(loc.Point));

            var schema = Schema.Lookup(MappingId);
            var entity = schema == null ? null : doc.ProjectInformation.GetEntity(schema);
            var mappings = entity != null && entity.IsValid() ? entity.Get<IDictionary<string,string>>("Layers") : new Dictionary<string,string>();
            var cad = doc.GetElement(LayersForm.columnImportId);
            var cadEntity = schema == null ? null : cad.GetEntity(schema);
            var choices = cadEntity != null && cadEntity.IsValid()
                ? cadEntity.Get<IDictionary<string,string>>("Layers") : new Dictionary<string,string>();
            var groups = candidates.GroupBy(c=>new { c.Layer,c.Width,c.Depth,c.Frame }).OrderBy(g=>g.Key.Layer).Select(g=>g.ToList()).ToList();
            using (var iconStream = typeof(CreateColumns).Assembly.GetManifestResourceStream("TYBIM.WindowIcon"))
            using (var icon = new D.Icon(iconStream))
            using (var form = new F.Form { Text="翻柱預覽（同心框請擇一）", Icon=icon, Width=1020, Height=750, MinimumSize=new D.Size(760,540), StartPosition=F.FormStartPosition.CenterScreen })
            using (var grid = new F.DataGridView { Dock=F.DockStyle.Fill, AllowUserToAddRows=false, AllowUserToDeleteRows=false, RowHeadersVisible=false, AutoSizeColumnsMode=F.DataGridViewAutoSizeColumnsMode.Fill })
            {
                foreach (string title in new[]{"CAD 圖層","寬 × 深 mm","輪廓數","已放置樓層筆數","輪廓提示"})
                    grid.Columns[grid.Columns.Add(title,title)].ReadOnly=true;
                var choice = new F.DataGridViewComboBoxColumn { HeaderText="放置族群", FillWeight=220 };
                choice.Items.Add(Skip);
                choice.Items.AddRange(families.Keys.OrderBy(k=>k).Cast<object>().ToArray());
                grid.Columns.Add(choice);
                grid.Columns[grid.Columns.Add("size","型號")].ReadOnly=true;
                var reviewRows = new List<F.DataGridViewRow>();
                foreach (var group in groups)
                {
                    var c=group[0];
                    string pick = mappings.TryGetValue(c.Layer,out string saved) && families.ContainsKey(saved) ? saved : Suggest(c, group.Count >= 3, families);
                    if (c.Frame!="") pick=Skip;
                    bool remembered = choices.TryGetValue(ChoiceKey(c), out string previous);
                    if (remembered)
                        pick = previous == Skip || families.ContainsKey(previous) ? previous : Skip;
                    int used=group.Sum(v=>levels.Take(levels.Count-1).Count(l=>occupied.Contains(l.Id+"|"+Position(v.Point))));
                    var row=grid.Rows[grid.Rows.Add(c.Layer,c.Width+" × "+c.Depth,group.Count,used,c.Frame,pick)];
                    row.Tag=group;
                    if (pick==Skip && (remembered || c.Frame=="")) reviewRows.Add(row);
                }
                void RefreshRow(F.DataGridViewRow row)
                {
                    var pick=(string)row.Cells[5].Value;
                    row.Cells[6].Value=pick==Skip ? "—" : families[pick].Any(s=>Matches(s,((List<Candidate>)row.Tag)[0])) ? "沿用" : "新增";
                }
                foreach(F.DataGridViewRow row in grid.Rows) RefreshRow(row);
                grid.CurrentCellDirtyStateChanged+=(s,e)=>{ if(grid.IsCurrentCellDirty) grid.CommitEdit(F.DataGridViewDataErrorContexts.Commit); };
                grid.CellValueChanged+=(s,e)=>{ if(e.RowIndex>=0 && e.ColumnIndex==5) RefreshRow(grid.Rows[e.RowIndex]); };
                grid.CellMouseClick += (sender,e) =>
                {
                    if (e.Button != F.MouseButtons.Left || e.RowIndex < 0 || e.ColumnIndex != choice.Index) return;
                    grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                    if (grid.BeginEdit(true) && grid.EditingControl is F.ComboBox combo)
                        combo.DroppedDown = true;
                };
                var showAll = new F.CheckBox { Text="顯示其他矩形（" + reviewRows.Count + " 組）",
                    Dock=F.DockStyle.Top, Checked=reviewRows.Count==grid.Rows.Count && choices.Count==0 };
                void FilterRows()
                {
                    grid.CurrentCell=null;
                    foreach (var row in reviewRows)
                        row.Visible=showAll.Checked || (string)row.Cells[5].Value!=Skip;
                }
                showAll.CheckedChanged+=(sender,e)=>FilterRows();
                FilterRows();
                var ok=new F.Button { Text="建立選定柱", AutoSize=true };
                ok.Click+=(s,e)=>{
                    var seen=new HashSet<string>();
                    foreach(F.DataGridViewRow row in grid.Rows)
                        if ((string)row.Cells[5].Value!=Skip && ((List<Candidate>)row.Tag).Any(c=>!seen.Add(Position(c.Point))))
                        { F.MessageBox.Show("同一位置選了多個輪廓，請保留一組。"); return; }
                    form.DialogResult=F.DialogResult.OK;
                };
                var footer = new F.FlowLayoutPanel { Dock=F.DockStyle.Bottom, AutoSize=true, Padding=new F.Padding(4) };
                var cancel = new F.Button { Text="取消", AutoSize=true, DialogResult=F.DialogResult.Cancel };
                footer.Controls.AddRange(new F.Control[] { ok, cancel });
                form.CancelButton=cancel;
                var instructions = new F.Label { Dock=F.DockStyle.Top, AutoSize=true, Padding=new F.Padding(5),
                    Text="請確認各組的放置族群。點選欄位可更換族群或選「略過」，同心框請擇一。核對下方平面後，按「建立選定柱」會記住此 CAD 各組的選擇（含略過），並套用至所選樓層。" };
                var split = new F.SplitContainer { Dock=F.DockStyle.Fill, Orientation=F.Orientation.Horizontal,
                    Size=new D.Size(960,580), SplitterDistance=235 };
                split.Panel1.Controls.Add(grid);
                var canvas = new F.PictureBox { Dock=F.DockStyle.Fill, BackColor=D.Color.White };
                var legend = new F.FlowLayoutPanel { Dock=F.DockStyle.Top, AutoSize=true };
                var story = new F.ComboBox { DropDownStyle=F.ComboBoxStyle.DropDownList };
                for(int i=0; i<levels.Count-1; i++) story.Items.Add(levels[i].Name+" → "+levels[i+1].Name);
                story.SelectedIndex=0;
                legend.Controls.AddRange(new F.Control[] { story, new F.Label { AutoSize=true, Padding=new F.Padding(3),
                    Text="灰線：CAD　紅色：預計建立的結構柱　藍色：預計建立的建築柱（已放置的位置略過）" } });
                split.Panel2.Controls.Add(canvas);
                split.Panel2.Controls.Add(legend);
                var lines=LayersForm.wallLines.Where(l=>l.ImportId==LayersForm.columnImportId).ToList();
                var endpoints=lines.SelectMany(l=>new[]{l.Start,l.End}).ToList();
                double left=endpoints.Min(p=>p.X), right=endpoints.Max(p=>p.X);
                double bottom=endpoints.Min(p=>p.Y), top=endpoints.Max(p=>p.Y);
                canvas.Resize+=(sender,e)=>canvas.Invalidate();
                story.SelectedIndexChanged+=(sender,e)=>canvas.Invalidate();
                grid.CellValueChanged+=(sender,e)=>canvas.Invalidate();
                canvas.Paint+=(sender,e)=>
                {
                    if(canvas.Width<=24 || canvas.Height<=24) return;
                    double scale=Math.Min((canvas.Width-24)/Math.Max(right-left,Mm),(canvas.Height-24)/Math.Max(top-bottom,Mm));
                    D.PointF Map(XYZ p)=>new D.PointF((float)(canvas.Width/2.0+(p.X-(left+right)/2)*scale),
                        (float)(canvas.Height/2.0-(p.Y-(bottom+top)/2)*scale));
                    foreach(var line in lines) e.Graphics.DrawLine(D.Pens.LightGray,Map(line.Start),Map(line.End));
                    foreach(F.DataGridViewRow row in grid.Rows)
                    {
                        var pick=(string)row.Cells[5].Value;
                        if(pick==Skip) continue;
                        bool architectural=Architectural(families[pick][0]);
                        foreach(var c in (List<Candidate>)row.Tag)
                        {
                            if(occupied.Contains(levels[story.SelectedIndex].Id+"|"+Position(c.Point))) continue;
                            var u=new XYZ(Math.Cos(c.Angle),Math.Sin(c.Angle),0)*(c.Width*Mm/2);
                            var v=new XYZ(-Math.Sin(c.Angle),Math.Cos(c.Angle),0)*(c.Depth*Mm/2);
                            var corners=new[]{Map(c.Point-u-v),Map(c.Point+u-v),Map(c.Point+u+v),Map(c.Point-u+v)};
                            e.Graphics.FillPolygon(architectural ? D.Brushes.SteelBlue : D.Brushes.Firebrick,corners);
                            e.Graphics.DrawPolygon(architectural ? D.Pens.SteelBlue : D.Pens.Firebrick,corners);
                        }
                    }
                };
                form.Controls.Add(split);
                form.Controls.Add(showAll);
                form.Controls.Add(instructions);
                form.Controls.Add(footer);
                if(form.ShowDialog()!=F.DialogResult.OK) return;
                int placed=0, skipped=0;
                bool choicesChanged = false;
                var errors=new List<string>();
                using(var tx=new Transaction(doc,"自動翻柱"))
                {
                    tx.Start();
                    foreach(F.DataGridViewRow row in grid.Rows)
                    {
                        var pick=(string)row.Cells[5].Value;
                        if(pick==Skip) continue;
                        var types=families[pick];
                        foreach(var c in (List<Candidate>)row.Tag)
                        for(int i=0;i<levels.Count-1;i++)
                        {
                            string key=levels[i].Id+"|"+Position(c.Point);
                            if(occupied.Contains(key)) { skipped++; continue; }
                            using(var sub=new SubTransaction(doc))
                            {
                                sub.Start();
                                try
                                {
                                    var symbol=Resolve(doc,types,c);
                                    if(!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                                    var p=new XYZ(c.Point.X,c.Point.Y,levels[i].ProjectElevation);
                                    var inst=doc.Create.NewFamilyInstance(p,symbol,levels[i],Architectural(symbol)?StructuralType.NonStructural:StructuralType.Column);
                                    inst.get_Parameter(BuiltInParameter.SCHEDULE_TOP_LEVEL_PARAM).Set(levels[i+1].Id);
                                    inst.get_Parameter(BuiltInParameter.SCHEDULE_BASE_LEVEL_OFFSET_PARAM).Set(0.0);
                                    inst.get_Parameter(BuiltInParameter.SCHEDULE_TOP_LEVEL_OFFSET_PARAM).Set(0.0);
                                    ElementTransformUtils.RotateElement(doc,inst.Id,Line.CreateBound(p,p+XYZ.BasisZ),c.Angle);
                                    sub.Commit();
                                    if(!types.Contains(symbol)) types.Add(symbol);
                                    occupied.Add(key);
                                    placed++;

                                }
                                catch(Exception e)
                                {
                                    if(sub.GetStatus()==TransactionStatus.Started) sub.RollBack();
                                    errors.Add(c.Layer+" "+c.Width+"×"+c.Depth+" @ "+levels[i].Name+"："+e.Message);
                                }
                            }
                        }
                    }
                    foreach (F.DataGridViewRow row in grid.Rows)
                    {
                        string key = ChoiceKey(((List<Candidate>)row.Tag)[0]);
                        string pick = (string)row.Cells[5].Value;
                        if (choices.TryGetValue(key, out string previous) && previous == pick) continue;
                        choices[key] = pick;
                        choicesChanged = true;
                    }
                    if (choicesChanged)
                    {
                        if(schema==null)
                        {
                            var builder=new SchemaBuilder(MappingId);
                            builder.SetSchemaName("TybimColumnLayerFamilies");
                            builder.AddMapField("Layers",typeof(string),typeof(string));
                            schema=builder.Finish();
                        }
                        var saved=new Entity(schema);
                        saved.Set<IDictionary<string,string>>("Layers",choices);
                        cad.SetEntity(saved);
                    }
                    if(tx.Commit()!=TransactionStatus.Committed) throw new InvalidOperationException("翻柱交易未完成。");
                }
                TaskDialog.Show("自動翻柱","建立 "+placed+" 支，略過已放置 "+skipped+" 支，失敗 "+errors.Count+" 支。"+Environment.NewLine+string.Join(Environment.NewLine,errors.Distinct().Take(6)));
            }
        }
        public string GetName() => "自動翻柱";
    }
}