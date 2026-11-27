using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

using TYBIM.Diagnostics;

namespace TYBIM.AutoBuild
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class CreateWalls : IExternalCommand, IExternalEventHandler
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CommandErrors.Execute("自動翻牆", "請確認 DWG 圖層有有效的封閉雙線、牆類型為基本牆，且頂部樓層高於基準樓層。",
                commandData, ref message, elements, ExecuteCore);
        }

        private Result ExecuteCore(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return LayersForm.OpenForm(commandData, "牆");
        }

        public void Execute(UIApplication app)
        {
            CommandErrors.ExecuteEvent("自動翻牆", "請檢查 DWG 封閉雙線、基本牆類型及基準／頂部樓層。", () => PreviewAndCreate(app));
        }

        private static void PreviewAndCreate(UIApplication app)
        {
            if (app.ActiveUIDocument == null) return;
            Document doc = app.ActiveUIDocument.Document;
            // Revit can return different managed wrappers for the same open document.
            // Document.Equals compares the native document identity; ReferenceEquals does not.
            Document sourceDocument = LayersForm.cadDocument;
            if (sourceDocument == null || !sourceDocument.IsValidObject || !doc.Equals(sourceDocument))
            {
                TaskDialog.Show("自動翻牆", "目前文件與讀取 DWG 的文件不同，請關閉自動翻模視窗後，在目前文件重新開啟。");
                return;
            }
            bool automatic = LayersForm.wallTypeId == ElementId.InvalidElementId;
            WallType type = doc.GetElement(LayersForm.wallTypeId) as WallType;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            Level bottom = levels.FirstOrDefault(l => l.Name == LayersForm.b_level_name);
            Level top = levels.FirstOrDefault(l => l.Name == LayersForm.t_level_name);
            if ((!automatic && (type == null || type.Kind != WallKind.Basic)) || bottom == null || top == null || top.Elevation <= bottom.Elevation)
            {
                TaskDialog.Show("自動翻牆", "請選擇基本牆類型與有效的基準／頂部樓層。");
                return;
            }
            double tolerance = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
            double minimumLength = Math.Max(app.Application.ShortCurveTolerance, tolerance);
            double maxThickness = UnitUtils.ConvertToInternalUnits(1000, UnitTypeId.Millimeters);
            var selected = new HashSet<string>(LayersForm.selectedLayers);
            int unsupported = 0;
            var centerLines = new List<WallRegionGeometry.Segment>();
            var stories = LayersForm.byLevel ? levels.Where(l => l.Elevation >= bottom.Elevation && l.Elevation <= top.Elevation)
                .GroupBy(l => l.Elevation).Select(g => g.First()).ToList() : new List<Level> { bottom, top };
            stories[0] = bottom; stories[stories.Count - 1] = top;
            var heights = stories.Take(stories.Count - 1).Select(level => level.ProjectElevation + 10 / 304.8).ToList();
            var columnFaces = ColumnWallCaps.ReadFaces(doc, heights);
            var additions = new List<WallRegionGeometry.Segment>();
            int regions = 0, openEdges = 0, invalidLoops = 0, nonPlanar = 0, joinedOpenings = 0;
            void Recalculate(HashSet<string> selectedLayers)
            {
                centerLines.Clear();
                additions.Clear();
                regions = openEdges = invalidLoops = nonPlanar = joinedOpenings = 0;
                unsupported = LayersForm.unsupportedWallCurves.Where(p => selectedLayers.Contains(p.Key)).Sum(p => p.Value);
                var inferred = new List<WallRegionGeometry.Segment>();
                // Keep separate DWG placements separate while reconstructing their regions.
                foreach (var import in LayersForm.wallLines.Where(l => selectedLayers.Contains(l.Layer)).GroupBy(l => l.ImportId))
                {
                    var input = new List<WallRegionGeometry.Segment>();
                    var rails = new List<KeyValuePair<string, WallRegionGeometry.Segment>>();
                    var firstAxis = centerLines.Count;
                    var openingRails = new List<WallRegionGeometry.Segment>();
                    var openings = new List<WallRegionGeometry.Segment>();
                    foreach (var line in import)
                    {
                        if (Math.Abs(line.Start.Z - line.End.Z) > tolerance) { nonPlanar++; continue; }
                        var segment = new WallRegionGeometry.Segment(new WallRegionGeometry.Point(line.Start.X, line.Start.Y),
                            new WallRegionGeometry.Point(line.End.X, line.End.Y));
                        if (segment.Length <= tolerance) continue;
                        if (WallRegionGeometry.IsOpeningLayer(line.Layer))
                        {
                            openings.Add(segment);
                            if (line.Layer.IndexOf("OPEN", StringComparison.OrdinalIgnoreCase) >= 0 || line.Layer.Contains("開口"))
                                openingRails.Add(segment);
                        }
                        else
                        {
                            input.Add(segment);
                            rails.Add(new KeyValuePair<string, WallRegionGeometry.Segment>(line.Layer, segment));
                        }
                    }
                    if (input.Count == 0) continue;
                    var pairs = WallPairBuilder.Build(rails, maxThickness, tolerance, openings);
                    var pairedAxes = new List<WallRegionGeometry.Segment>();
                    foreach (var wall in pairs.Walls)
                    {
                        wall.Axis.WidthMm = (int)Math.Round(wall.Thickness * 304.8, MidpointRounding.AwayFromZero);
                        pairedAxes.Add(wall.Axis);
                    }
                    centerLines.AddRange(pairedAxes);
                    var geometry = WallRegionGeometry.Build(input, tolerance, maxThickness);
                    joinedOpenings += pairs.Gaps.Count;
                    openEdges += geometry.OpenEdges;
                    bool valid = true;
                    foreach (var boundary in geometry.Boundaries)
                    {
                        try
                        {
                            var curves = new List<Curve>();
                            for (int i = 0; i < boundary.Count; i++)
                            {
                                var a = boundary[i]; var b = boundary[(i + 1) % boundary.Count];
                                curves.Add(Line.CreateBound(new XYZ(a.X, a.Y, bottom.Elevation), new XYZ(b.X, b.Y, bottom.Elevation)));
                            }
                            CurveLoop loop = CurveLoop.Create(curves);
                            if (loop.IsOpen() || !loop.HasPlane()) throw new InvalidOperationException("邊界不封閉或不共平面");
                            regions++;
                        }
                        catch (Exception) { valid = false; invalidLoops++; }
                    }
                    if (!valid) continue;
                    centerLines.AddRange(geometry.CenterLines.Where(axis => !pairedAxes.Any(pair => AlignedOverlap(axis, pair, tolerance))));
                    var caps = ColumnWallCaps.Find(input, columnFaces, heights, maxThickness);
                    if (caps.Count > 0)
                    {
                        var supplemented = WallRegionGeometry.Build(input.Concat(caps), tolerance, maxThickness);
                        inferred.AddRange(supplemented.CenterLines);
                    }
                    var localAxes = centerLines.GetRange(firstAxis, centerLines.Count - firstAxis);
                    centerLines.AddRange(WallRegionGeometry.OpeningHosts(openingRails, localAxes, tolerance));
                    centerLines.AddRange(WallRegionGeometry.FramedOpeningHosts(openingRails, input,
                        centerLines.GetRange(firstAxis, centerLines.Count - firstAxis), maxThickness, tolerance));
                    var swingRadii = new List<WallRegionGeometry.Segment>();
                    foreach (var radius in LayersForm.doorRadii.Where(r => r.ImportId == import.Key && selectedLayers.Contains(r.Layer)))
                    {
                        var gap = new WallRegionGeometry.Segment(new WallRegionGeometry.Point(radius.Start.X, radius.Start.Y),
                            new WallRegionGeometry.Point(radius.End.X, radius.End.Y));
                        swingRadii.Add(gap);
                        var extension = WallRegionGeometry.SwingHost(gap, localAxes);
                        if (extension != null) centerLines.Add(extension);
                    }
                    centerLines.AddRange(WallRegionGeometry.DoubleDoorHosts(swingRadii, localAxes));
                }
                var merged = WallRegionGeometry.Merge(centerLines, tolerance).Where(s => s.Length > minimumLength).ToList();
                centerLines.Clear();
                centerLines.AddRange(merged);

                foreach (var candidate in WallRegionGeometry.Merge(inferred, tolerance).Where(s => s.Length > minimumLength))
                {
                    var direction = (candidate.B - candidate.A) * (1 / candidate.Length);
                    bool Overlaps(WallRegionGeometry.Segment other)
                    {
                        var a = other.A - candidate.A;
                        var b = other.B - candidate.A;
                        if (Math.Abs(a.X * direction.Y - a.Y * direction.X) > tolerance ||
                            Math.Abs(b.X * direction.Y - b.Y * direction.X) > tolerance) return false;
                        double lo = a.X * direction.X + a.Y * direction.Y, hi = b.X * direction.X + b.Y * direction.Y;
                        return Math.Min(candidate.Length, Math.Max(lo, hi)) - Math.Max(0, Math.Min(lo, hi)) > tolerance;
                }
                    if (!centerLines.Any(Overlaps)) { additions.Add(candidate); centerLines.Add(candidate); }
                }
            }
            Recalculate(selected);
            using (var iconStream = typeof(CreateWalls).Assembly.GetManifestResourceStream("TYBIM.WindowIcon"))
            using (var icon = new System.Drawing.Icon(iconStream))
            using (var preview = new WallPreview(LayersForm.wallLines, centerLines, stories.Count - 1, additions, automatic ? null : type.Width, selected, Recalculate))
            {
                preview.Icon = icon;
                if (preview.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                centerLines.RemoveAll(preview.Excluded.Contains);
            }
            int count = 0, failed = 0;
            var errors = new List<string>();
            try
            {
                using var group = new TransactionGroup(doc, "自動翻牆");
                group.Start();
                var newWalls = new List<Wall>();
                using (var transaction = new Transaction(doc, "自動翻牆"))
                {
                    transaction.Start();
                    var byWidth = new Dictionary<int, WallType>();
                    if (automatic)
                    {
                        var types = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                            .Where(t => t.Kind == WallKind.Basic).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
                        foreach (int mm in centerLines.Select(s => s.WidthMm).Distinct().OrderBy(w => w))
                            byWidth[mm] = ResolveType(types, mm);
                    }
                    for (int i = 0; i < stories.Count - 1; i++)
                        foreach (var s in centerLines)
                        {
                            using (var step = new SubTransaction(doc))
                            {
                                step.Start();
                                try
                                {
                                    Line axis = Line.CreateBound(new XYZ(s.A.X, s.A.Y, stories[i].Elevation), new XYZ(s.B.X, s.B.Y, stories[i].Elevation));
                                    Wall wall = Wall.Create(doc, axis, (automatic ? byWidth[s.WidthMm] : type).Id, stories[i].Id,
                                        stories[i + 1].Elevation - stories[i].Elevation, 0, false, false);
                                    WallUtils.DisallowWallJoinAtEnd(wall, 0);
                                    WallUtils.DisallowWallJoinAtEnd(wall, 1);
                                    wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).Set(0);
                                    wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(stories[i + 1].Id);
                                    var status = step.Commit();
                                    if (status != TransactionStatus.Committed)
                                        throw new InvalidOperationException($"Revit 未提交該牆（狀態：{status}）。");
                                    newWalls.Add(wall);
                                    count++;
                                }
                                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                                catch (Exception ex)
                                {
                                    if (step.GetStatus() == TransactionStatus.Started) step.RollBack();
                                    failed++;
                                    if (errors.Count < 3) errors.Add(ex.Message);
                                }
                            }
                        }
                    CommandErrors.Commit(transaction);
                }
                int deferred = 0;
                if (newWalls.Count > 0 && !RestoreEnds(doc, newWalls))
                    foreach (var wall in newWalls)
                        if (!RestoreEnds(doc, new[] { wall })) deferred++;
                if (group.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("自動翻牆交易群組未成功提交。");
                TaskDialog.Show("自動翻牆", "已生成 " + count + " 道牆；封閉邊界 " + regions + " 個；已連接開口 " + joinedOpenings + " 處。\n" +
                    "略過未封閉線段：" + openEdges + "；無效邊界：" + invalidLoops + "；非水平線段：" + nonPlanar +
                    "；已略過的 CAD 曲線：" + unsupported + "；保留未開放接合的牆：" + deferred + "；建牆失敗：" + failed + (errors.Count == 0 ? "" : "\n" + string.Join("\n", errors)));
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { throw; }
            catch (Exception ex) { throw new InvalidOperationException("建立牆或還原牆端接合時發生錯誤。", ex); }
        }
        private sealed class RollBackJoinErrors : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                return accessor.GetFailureMessages().Any(f => f.GetSeverity() >= FailureSeverity.Error)
                    ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }

        private static bool RestoreEnds(Document doc, IEnumerable<Wall> walls)
        {
            using var tx = new Transaction(doc, "自動翻牆");
            tx.Start();
            var options = tx.GetFailureHandlingOptions().SetClearAfterRollback(true)
                .SetFailuresPreprocessor(new RollBackJoinErrors());
            tx.SetFailureHandlingOptions(options);
            try
            {
                foreach (var wall in walls)
                    for (int end = 0; end < 2; end++)
                        WallUtils.AllowWallJoinAtEnd(wall, end);
                return tx.Commit() == TransactionStatus.Committed;
            }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
            catch (Exception) when (tx.GetStatus() == TransactionStatus.Started)
            {
                tx.RollBack();
                return false;
            }
        }

        private static bool AlignedOverlap(WallRegionGeometry.Segment axis, WallRegionGeometry.Segment pair, double tolerance)
        {
            var d = (axis.B - axis.A) * (1 / axis.Length);
            var a = pair.A - axis.A;
            var b = pair.B - axis.A;
            if (Math.Abs(a.X * d.Y - a.Y * d.X) > tolerance || Math.Abs(b.X * d.Y - b.Y * d.X) > tolerance) return false;
            double p = a.X * d.X + a.Y * d.Y, q = b.X * d.X + b.Y * d.Y;
            return Math.Min(axis.Length, Math.Max(p, q)) - Math.Max(0, Math.Min(p, q)) > tolerance;
        }

        private static WallType ResolveType(List<WallType> types, int mm)
        {
            if (mm <= 0) throw new InvalidOperationException("無法判定 CAD 牆厚。");
            double width = mm / 304.8;
            var ordered = types.OrderBy(t => Math.Abs(t.Width - width)).ToList();
            var match = ordered.FirstOrDefault(t => Math.Abs(t.Width - width) < 0.5 / 304.8);
            if (match != null) return match;
            var basis = ordered.FirstOrDefault(t => t.GetCompoundStructure() is CompoundStructure cs &&
                !cs.IsVerticallyCompound && cs.LayerCount == 1 && cs.GetLayerWidth(0) > 0)
                ?? ordered.FirstOrDefault();
            if (basis == null) throw new InvalidOperationException("專案缺少可複製的基本牆型。");
            string stem = $"TYBIM CAD {mm}mm", name = stem;
            for (int i = 2; types.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
                name = $"{stem} ({i})";
            var copy = (WallType)basis.Duplicate(name);
            var structure = copy.GetCompoundStructure();
            if (structure != null && !structure.IsVerticallyCompound && structure.LayerCount == 1 && structure.GetLayerWidth(0) > 0)
                structure.SetLayerWidth(0, width);
            else
                structure = CompoundStructure.CreateSingleLayerCompoundStructure(MaterialFunctionAssignment.Structure,
                    width, ElementId.InvalidElementId);
            copy.SetCompoundStructure(structure);
            if (Math.Abs(copy.Width - width) > 1e-6)
                throw new InvalidOperationException($"無法建立 {mm} mm 基本牆型。");
            types.Add(copy);
            return copy;
        }

        public string GetName() { return "自動翻牆"; }
    }
}
