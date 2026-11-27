using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

using TYBIM.Diagnostics;

namespace TYBIM.AutoBuild
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    [Journaling(JournalingMode.NoCommandData)]
    public class CreateFloors : IExternalCommand
    {

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CommandErrors.Execute("自動翻板", "請確認專案有樓板類型，並檢查相關樓層的樑、牆與柱是否形成封閉區域。",
                commandData, ref message, elements, ExecuteCore);
        }

        private Result ExecuteCore(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Application app = uiapp.Application;
            Document doc = uidoc.Document;
            View view = doc.ActiveView;

            ViewPlan plan = view as ViewPlan;
            bool isFloorPlan = plan != null && !plan.IsTemplate
                && (plan.ViewType == ViewType.FloorPlan || plan.ViewType == ViewType.EngineeringPlan)
                && plan.GenLevel != null;

            if (!(view is View3D) && !isFloorPlan)
            {
                message = "目前視圖無法取得建板樓層。請開啟樓層平面、結構平面或 3D 視圖。";
                return Result.Failed;
            }

            try
            {
                ElementId defaultFloorTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(FloorType))
                    .FirstElementId();

                if (defaultFloorTypeId == null || defaultFloorTypeId == ElementId.InvalidElementId)
                    throw new InvalidOperationException("專案中找不到樓板類型。請先建立或移轉一個樓板類型。");

                // Plan views build only their associated level; 3D retains all levels.
                List<Level> levels = isFloorPlan
                    ? new List<Level> { plan.GenLevel }
                    : new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .OrderBy(l => l.Elevation)
                        .ToList();

                IList<Element> boundaryElements = GetBoundaryElements(doc);
                if (!boundaryElements.Any())
                {
                    message = "找不到本機模型的樑、基本牆或柱。請先在本機模型建立會圍合樓板的 Revit 元件。";
                    return Result.Failed;
                }

                int createdFloorsCount = 0;
                int skippedFloorsCount = 0;
                int failedFloorsCount = 0;
                var diagnostics = new List<string>();
                var floorErrors = new List<string>();
                var overlapGuard = new FloorOverlapGuard(doc);

                using (Transaction trans = new Transaction(doc, "自動翻板"))
                {
                    //// 關閉警示視窗
                    //FailureHandlingOptions options = trans.GetFailureHandlingOptions();
                    //CloseWarnings closeWarnings = new CloseWarnings();
                    //options.SetClearAfterRollback(true);
                    //options.SetFailuresPreprocessor(closeWarnings);
                    //trans.SetFailureHandlingOptions(options);
                    trans.Start();

                    foreach (Level level in levels)
                    {
                        foreach (CurveLoop profile in FloorRegionBuilder.GetProfiles(boundaryElements, level, diagnostics))
                        {
                            FloorCreationResult result = overlapGuard.TryCreate(doc, profile, defaultFloorTypeId, level.Id, out string reason);
                            if (result == FloorCreationResult.Created) createdFloorsCount++;
                            else if (result == FloorCreationResult.Overlap) skippedFloorsCount++;
                            else
                            {
                                failedFloorsCount++;
                                floorErrors.Add($"樓層 {level.Name}：{reason}");
                            }
                        }
                    }

                    CommandErrors.Commit(trans);
                }

                var summary = new TaskDialog("自動翻板");
                summary.MainInstruction = $"成功生成 {createdFloorsCount} 塊樓板。\n失敗／跳過 {skippedFloorsCount + failedFloorsCount} 處（重疊 {skippedFloorsCount}；建立或檢查失敗 {failedFloorsCount}）。"
                    + (createdFloorsCount + skippedFloorsCount + failedFloorsCount == 0 ? "\n未找到可建立樓板的封閉區域。" : "");
                if (floorErrors.Count > 0)
                    summary.MainContent = floorErrors[0] + "\n請檢查該樓層的封閉邊界、樓板類型與既有樓板。";
                var details = floorErrors.Concat(diagnostics).Distinct().ToArray();
                if (details.Length > 0) summary.ExpandedContent = string.Join("\n", details.Take(20))
                    + (details.Length > 20 ? $"\n另有 {details.Length - 20} 則明細。" : "");
                summary.Show();
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException("計算樓板邊界或建立樓板時發生錯誤。", ex);
            }
        }

        // --- 輔助方法 ---

        private IList<Element> GetBoundaryElements(Document doc)
        {
            ElementCategoryFilter columnsFilter = new ElementCategoryFilter(BuiltInCategory.OST_StructuralColumns);
            ElementCategoryFilter beamsFilter = new ElementCategoryFilter(BuiltInCategory.OST_StructuralFraming);
            ElementCategoryFilter architecturalColumnsFilter = new ElementCategoryFilter(BuiltInCategory.OST_Columns);
            LogicalOrFilter filter = new LogicalOrFilter(new List<ElementFilter> { columnsFilter, architecturalColumnsFilter, beamsFilter, new ElementCategoryFilter(BuiltInCategory.OST_Walls) });

            // Plan visibility/view range may exclude beams needed to close the slab boundary.
            // Collect the model for plan mode, then filter by the target level's elevation.
            FilteredElementCollector collector = new FilteredElementCollector(doc);
            return collector
                .WherePasses(filter)
                .WhereElementIsNotElementType()
                .Where(element => !(element is Wall wall) || wall.WallType.Kind == WallKind.Basic)
                .ToList();
        }
    }
}
