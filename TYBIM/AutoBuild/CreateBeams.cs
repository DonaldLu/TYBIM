using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

using TYBIM.Diagnostics;

namespace TYBIM.AutoBuild
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class CreateBeams : IExternalCommand, IExternalEventHandler
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CommandErrors.Execute("自動翻樑", "請確認目前視圖中的 DWG 連結、樑線圖層、樑類型與參考樓層。",
                commandData, ref message, elements, ExecuteCore);
        }

        private Result ExecuteCore(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return LayersForm.OpenForm(commandData, "樑");
        }

        public void Execute(UIApplication app)
        {
            CommandErrors.ExecuteEvent("自動翻樑", "請檢查 DWG 樑線圖層、樑類型與參考樓層。", () => PreviewAndCreate(app));
        }

        private static void PreviewAndCreate(UIApplication app)
        {
            if (app.ActiveUIDocument == null) return;
            Document doc = app.ActiveUIDocument.Document;
            Document source = LayersForm.cadDocument;
            if (source == null || !source.IsValidObject || !doc.Equals(source))
            {
                TaskDialog.Show("自動翻樑", "文件已切換，請在目前文件重新開啟自動翻樑。");
                return;
            }
            if (doc.ActiveView.Id != LayersForm.beamViewId)
            {
                TaskDialog.Show("自動翻樑", "視圖已切換，請在目前視圖重新開啟自動翻樑。");
                return;
            }
            var settings = System.Windows.Forms.Application.OpenForms["LayersForm"] as LayersForm;
            if (settings == null) return;
            settings.RefreshBeamCad(doc);
            FamilySymbol symbol = doc.GetElement(LayersForm.beamSymbolId) as FamilySymbol;
            Level level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => l.Name == LayersForm.b_level_name);
            if (symbol?.Category?.Id != new ElementId(BuiltInCategory.OST_StructuralFraming) || level == null)
            {
                TaskDialog.Show("自動翻樑", "請選擇有效的樑類型與參考樓層。");
                return;
            }
            double elevation = level.ProjectElevation;
            double minimumLength = Math.Max(BeamGeometry.Tolerance, app.Application.ShortCurveTolerance);
            var existing = new List<WallRegionGeometry.Segment>();
            foreach (FamilyInstance instance in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                if (instance.StructuralType != StructuralType.Beam ||
                    !((instance.Location as LocationCurve)?.Curve is Line axis) || !axis.IsBound) continue;
                XYZ a = axis.GetEndPoint(0);
                XYZ b = axis.GetEndPoint(1);
                if (Math.Abs(a.Z - elevation) <= BeamGeometry.Tolerance && Math.Abs(b.Z - elevation) <= BeamGeometry.Tolerance)
                    existing.Add(new WallRegionGeometry.Segment(BeamGeometry.ToPlan(a), BeamGeometry.ToPlan(b)));
            }
            var selected = new HashSet<string>(LayersForm.selectedLayers);
            var lines = LayersForm.wallLines.ToLookup(line => line.ImportId);
            var curves = LayersForm.unsupportedBeamCurves;
            var imports = lines.Select(group => group.Key).Concat(curves.Keys).Distinct()
                .OrderByDescending(id => lines[id].Any(line => selected.Contains(line.Layer)))
                .ThenBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
            if (imports.Length == 0)
            {
                TaskDialog.Show("自動翻樑", "目前視圖沒有可讀取線段的 DWG 連結。");
                return;
            }

            var names = imports.Select(id => $"{doc.GetElement(id)?.Category?.Name} [{id}]").ToArray();
            var backgrounds = imports.Select(id => lines[id].ToList()).ToArray();
            var layers = imports.Select(id => lines[id].Select(line => line.Layer)
                .Concat(curves.TryGetValue(id, out var counts) ? (IEnumerable<string>)counts.Keys : Array.Empty<string>())
                .Distinct().OrderByDescending(selected.Contains).ThenBy(name => name).ToList()).ToArray();
            BeamGeometry.Result Calculate(int index, HashSet<string> checkedLayers, bool pairEdges, double minWidth, double maxWidth)
            {
                int unsupported = curves.TryGetValue(imports[index], out var counts)
                    ? counts.Where(pair => checkedLayers.Contains(pair.Key)).Sum(pair => pair.Value) : 0;
                return BeamGeometry.Build(backgrounds[index].Where(line => checkedLayers.Contains(line.Layer)),
                    existing, pairEdges, minWidth, maxWidth, minimumLength, unsupported);
            }

            List<BeamGeometry.Candidate> approved;
            using (var iconStream = typeof(CreateBeams).Assembly.GetManifestResourceStream("TYBIM.WindowIcon"))
            using (var icon = new System.Drawing.Icon(iconStream))
            using (var preview = new BeamPreview($"參考樓層 {level.Name} ｜ 建立類型 {symbol.FamilyName}：{symbol.Name} ｜請確認 CAD 樑寬與所選類型",
                names, selected, index => layers[index], index => backgrounds[index], Calculate))
            {
                preview.Icon = icon;
                if (preview.ShowDialog(settings) != System.Windows.Forms.DialogResult.OK) return;
                approved = preview.Approved.ToList();
            }
            if (approved.Count == 0) return;
            Create(doc, level, symbol, approved, elevation);
        }

        private static void Create(Document doc, Level level, FamilySymbol symbol,
            List<BeamGeometry.Candidate> candidates, double elevation)
        {
            int count = 0, failed = 0;
            var errors = new List<string>();
            try
            {
                using (var transaction = new Transaction(doc, "自動翻樑"))
                {
                    transaction.Start();
                    if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                    foreach (var candidate in candidates)
                    {
                        using (var item = new SubTransaction(doc))
                        {
                            item.Start();
                            try
                            {
                                var a = candidate.Axis.A;
                                var b = candidate.Axis.B;
                                Line axis = Line.CreateBound(new XYZ(a.X, a.Y, elevation), new XYZ(b.X, b.Y, elevation));
                                FamilyInstance beam = doc.Create.NewFamilyInstance(axis, symbol, level, StructuralType.Beam);
                                if (beam == null) throw new InvalidOperationException("未建立樑實例。");
                                Parameter startOffset = beam.get_Parameter(BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION);
                                Parameter endOffset = beam.get_Parameter(BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION);
                                if (startOffset != null && !startOffset.IsReadOnly) startOffset.Set(0.0);
                                if (endOffset != null && !endOffset.IsReadOnly) endOffset.Set(0.0);
                                var status = item.Commit();
                                if (status != TransactionStatus.Committed)
                                    throw new InvalidOperationException($"Revit 未提交該樑（狀態：{status}）。");
                                count++;
                            }
                            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                            catch (Exception ex)
                            {
                                if (item.GetStatus() == TransactionStatus.Started) item.RollBack();
                                failed++;
                                if (errors.Count < 3) errors.Add(ex.Message);
                            }
                        }
                    }
                    CommandErrors.Commit(transaction);
                }
                TaskDialog.Show("自動翻樑", $"已建立 {count} 支樑，失敗 {failed} 支。" +
                    (errors.Count == 0 ? "" : "\n" + string.Join("\n", errors)));
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { throw; }
            catch (Exception ex) { throw new InvalidOperationException($"在樓層「{level.Name}」建立樑時發生錯誤。", ex); }
        }
        public string GetName() { return "自動翻樑"; }
    }
}
