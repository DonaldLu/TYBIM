using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TYBIM_2025.AutoBuild
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    [Journaling(JournalingMode.NoCommandData)]
    public class CreateFloors : IExternalCommand
    {

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
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
                TaskDialog.Show("提示", "請在樓層平面、結構平面或 3D 視圖中執行此功能。");
                return Result.Failed;
            }

            try
            {
                ElementId defaultFloorTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(FloorType))
                    .FirstElementId();

                if (defaultFloorTypeId == null || defaultFloorTypeId == ElementId.InvalidElementId) return Result.Failed;

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
                    TaskDialog.Show("自動翻板", "沒有找到本機模型的樑、基本牆或柱。此功能需要 Revit 構件實體；CAD 線條或連結模型不會作為建板邊界。");
                    return Result.Failed;
                }

                int createdFloorsCount = 0;
                int skippedFloorsCount = 0;
                int failedFloorsCount = 0;
                var diagnostics = new List<string>();
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
                            FloorCreationResult result = overlapGuard.TryCreate(doc, profile, defaultFloorTypeId, level.Id);
                            if (result == FloorCreationResult.Created) createdFloorsCount++;
                            else if (result == FloorCreationResult.Overlap) skippedFloorsCount++;
                            else failedFloorsCount++;
                        }
                    }

                    if (trans.Commit() != TransactionStatus.Committed)
                    {
                        TaskDialog.Show("自動翻板", "樓板交易未成功提交，請檢查 Revit 的錯誤訊息。");
                        return Result.Failed;
                    }
                }

                var summary = new TaskDialog("自動翻板");
                summary.MainInstruction = $"成功生成 {createdFloorsCount} 塊樓板。\n失敗／跳過 {skippedFloorsCount + failedFloorsCount} 處（重疊 {skippedFloorsCount}；建立或檢查失敗 {failedFloorsCount}）。"
                    + (createdFloorsCount + skippedFloorsCount + failedFloorsCount == 0 ? "\n未找到可建立樓板的封閉區域。" : "");
                if (diagnostics.Count > 0) summary.ExpandedContent = string.Join("\n", diagnostics);
                summary.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Revit API 錯誤", ex.ToString());
                return Result.Failed;
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
