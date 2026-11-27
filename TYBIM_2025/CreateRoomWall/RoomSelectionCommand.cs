using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using TYBIM.Diagnostics;

namespace TYBIM_2025.CreateRoomWall
{
    [Transaction(TransactionMode.Manual)]
    public class RoomSelectionCommand : IExternalCommand
    {
        private const string Feature = "自動裝修牆";
        private const string Guidance = "請依錯誤訊息檢查該房間的完成面邊界、樓層與所選基本牆類型。";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CommandErrors.Execute(Feature, Guidance, commandData, ref message, elements, ExecuteCore);
        }

        private Result ExecuteCore(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            var doc = uiapp.ActiveUIDocument.Document;
            if (doc.IsFamilyDocument || doc.IsReadOnly)
            {
                CommandErrors.Show(Feature, doc.IsFamilyDocument ? "目前開啟的是族群文件。" : "目前專案為唯讀。",
                    "請開啟可編輯的 Revit 專案，再建立裝修牆。");
                return Result.Cancelled;
            }

            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .Cast<Room>().Where(room => room.Area > 0).ToList();
            if (rooms.Count == 0)
            {
                CommandErrors.Show(Feature, "專案中沒有面積大於零的已放置房間。",
                    "請先在目標樓層放置房間，並確認房間邊界已封閉及房間面積大於零。");
                return Result.Cancelled;
            }

            var wallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                .Cast<WallType>().Where(type => type.Kind == WallKind.Basic).OrderBy(type => type.Name).ToList();
            if (wallTypes.Count == 0)
            {
                CommandErrors.Show(Feature, "專案中找不到基本牆類型。", "請先建立或移轉所需的基本牆類型。");
                return Result.Cancelled;
            }

            var viewModel = new MainViewModel();
            foreach (var wallType in wallTypes)
                viewModel.WallTypes.Add(wallType);
            foreach (var group in rooms.GroupBy(room => room.Level?.Name ?? "未指定樓層").OrderBy(group => group.Key))
            {
                var level = new LevelNode { Name = group.Key };
                foreach (var room in group.OrderBy(room => room.Number))
                {
                    level.Rooms.Add(new RoomNode
                    {
                        Name = $"{room.Number} - {room.Name}",
                        RoomElement = room,
                        ParentLevel = level
                    });
                }
                viewModel.Levels.Add(level);
            }

            try
            {
                var window = new RoomSelectionWindow { DataContext = viewModel };
                new WindowInteropHelper(window).Owner = uiapp.MainWindowHandle;
                if (window.ShowDialog() != true)
                    return Result.Cancelled;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException("開啟房間及牆類型選擇視窗時發生錯誤。", ex);
            }

            var selectedWall = viewModel.SelectedWallType;
            var selectedRooms = viewModel.Levels.SelectMany(level => level.Rooms)
                .Where(room => room.IsSelected).Select(room => room.RoomElement).ToList();
            if (selectedWall == null || selectedRooms.Count == 0)
            {
                CommandErrors.Show(Feature, "牆類型或房間選擇不完整。", "請選擇基本牆類型並至少勾選一個房間。");
                return Result.Cancelled;
            }

            using var transaction = new Transaction(doc, "依房間邊界建立牆壁");
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Revit 無法開始建立裝修牆的交易。");

            using var options = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
            };
            double height = UnitUtils.ConvertToInternalUnits(3000, UnitTypeId.Millimeters);
            double offset = -selectedWall.Width / 2;
            int created = 0;
            int skipped = 0;
            int fallback = 0;
            var issues = new List<string>();
            foreach (var room in selectedRooms)
            {
                var roomContext = $"房間 {room.Number} - {room.Name}（ID：{room.Id.Value}，樓層：{room.Level?.Name ?? "未指定"}）";
                string stage = "讀取完成面邊界";
                try
                {
                    var boundaries = room.GetBoundarySegments(options);
                    if (boundaries == null || boundaries.Count == 0)
                    {
                        skipped++;
                        issues.Add($"{roomContext}：找不到完成面邊界。請檢查房間封閉範圍及元件的房間邊界設定。");
                        continue;
                    }

                    for (int index = 0; index < boundaries.Count; index++)
                    {
                        stage = $"組合第 {index + 1} 圈完成面邊界";
                        using var original = new CurveLoop();
                        foreach (var segment in boundaries[index])
                            original.Append(segment.GetCurve());

                        stage = $"偏移第 {index + 1} 圈完成面邊界";
                        CurveLoop geometry = null;
                        try
                        {
                            geometry = CurveLoop.CreateViaOffset(original, offset, XYZ.BasisZ);
                        }
                        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.InvalidOperationException
                            or Autodesk.Revit.Exceptions.ArgumentException)
                        {
                            fallback++;
                            issues.Add($"{roomContext}，{stage}失敗：{CommandErrors.Describe(ex)}。已沿原邊界建牆，請檢查牆面位置。");
                        }

                        using (geometry)
                        {
                            int segmentIndex = 0;
                            foreach (var curve in geometry ?? original)
                            {
                                stage = $"沿第 {index + 1} 圈第 {++segmentIndex} 段邊界建立「{selectedWall.Name}」";
                                var wall = Wall.Create(doc, curve, selectedWall.Id, room.LevelId, height, 0, false, false);
                                var parameter = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                                if (parameter != null && !parameter.IsReadOnly)
                                    parameter.Set((int)WallLocationLine.FinishFaceExterior);
                                created++;
                            }
                        }
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"{roomContext}：{stage}時發生錯誤。", ex);
                }
            }

            CommandErrors.Commit(transaction);
            var dialog = new TaskDialog(Feature)
            {
                MainInstruction = created > 0 ? $"已建立 {created} 道裝修牆" : "沒有建立裝修牆",
                MainContent = $"已檢查 {selectedRooms.Count} 個房間。略過 {skipped} 個房間，{fallback} 圈改沿原邊界建牆。"
            };
            if (issues.Count > 0)
            {
                dialog.MainContent += "\n\n" + issues[0];
                if (issues.Count > 1)
                    dialog.ExpandedContent = string.Join("\n", issues.Take(20))
                        + (issues.Count > 20 ? $"\n另有 {issues.Count - 20} 則明細。" : "");
            }
            dialog.Show();
            return Result.Succeeded;
        }
    }
}
