using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using RevitArgumentException = Autodesk.Revit.Exceptions.ArgumentException;
using RevitInvalidOperationException = Autodesk.Revit.Exceptions.InvalidOperationException;

using TYBIM.Diagnostics;

namespace TYBIM.AutoBuild
{
    [Transaction(TransactionMode.Manual)]
    public sealed class AutoJoinCommand : IExternalCommand
    {
        private const string Guidance = "請用元件 ID 找到相關元件，檢查目前視圖、品類、交界幾何及編輯權限後重試。";

        private static readonly BuiltInCategory[][] Categories =
        {
            new[] { BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns },
            new[] { BuiltInCategory.OST_StructuralFraming },
            new[] { BuiltInCategory.OST_Walls },
            new[] { BuiltInCategory.OST_Floors }
        };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CommandErrors.Execute("自動接合", Guidance,
                commandData, ref message, elements, ExecuteCore);
        }

        private Result ExecuteCore(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var ui = commandData.Application;
            var doc = ui.ActiveUIDocument?.Document;
            var view = doc?.ActiveView;
            if (doc.IsFamilyDocument || doc.IsReadOnly)
            {
                CommandErrors.Show("自動接合", doc.IsFamilyDocument ? "目前開啟的是族群文件。" : "目前專案為唯讀。",
                    "請開啟可編輯的 Revit 專案，再執行此功能。");
                return Result.Cancelled;
            }
            if (view == null || !FilteredElementCollector.IsViewValidForElementIteration(doc, view.Id))
            {
                CommandErrors.Show("自動接合", "目前視圖無法讀取可接合的模型元件。",
                    "請開啟含有柱、樑、牆或樓板的模型視圖，例如樓層平面或 3D 視圖。");
                return Result.Cancelled;
            }

            AutoJoinWindow window;
            try
            {
                window = new AutoJoinWindow(view.Name);
                new WindowInteropHelper(window).Owner = ui.MainWindowHandle;
                if (window.ShowDialog() != true)
                    return Result.Cancelled;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException("開啟品類選擇視窗時發生錯誤。", ex);
            }

            string operation = window.Unjoin ? "取消接合" : "自動接合";
            try
            {
                Run(doc, view, window, operation);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"執行「{operation}」時發生錯誤。", ex);
            }
        }

        private static void Run(Document doc, View view, AutoJoinWindow window, string operation)
        {
            bool unjoin = window.Unjoin;
            using var hostFilter = new ElementMulticategoryFilter(Categories[window.HostCategory]);
            using var targetFilter = new ElementMulticategoryFilter(window.TargetCategories.SelectMany(index => Categories[index]).ToList());
            using var hostCollector = new FilteredElementCollector(doc, view.Id);
            using var targetCollector = new FilteredElementCollector(doc, view.Id);
            var hosts = hostCollector.WherePasses(hostFilter).WhereElementIsNotElementType().ToElements();
            var targetIds = targetCollector.WherePasses(targetFilter).WhereElementIsNotElementType().ToElementIds().ToHashSet();

            if (hosts.Count == 0 || targetIds.Count == 0)
            {
                CommandErrors.Show(operation, hosts.Count == 0
                    ? "目前視圖沒有符合所選主體品類的元件。"
                    : "目前視圖沒有符合所勾選接合品類的元件。",
                    "請切換至包含這些元件的模型視圖，或調整主體與接合品類。");
                return;
            }

            var pairs = new List<(ElementId Host, ElementId Target)>();
            var seen = new HashSet<(long, long)>();
            int existing = 0;
            foreach (var host in hosts)
            {
                ICollection<ElementId> candidates;
                if (unjoin)
                {
                    candidates = JoinGeometryUtils.GetJoinedElements(doc, host);
                }
                else
                {
                    var box = host.get_BoundingBox(view);
                    if (box == null)
                        continue;

                    using var outline = new Outline(box.Min, box.Max);
                    using var overlap = new BoundingBoxIntersectsFilter(outline);
                    using var nearby = new FilteredElementCollector(doc, view.Id);
                    candidates = nearby.WherePasses(targetFilter).WherePasses(overlap)
                        .WhereElementIsNotElementType().ToElementIds();
                }

                foreach (var id in candidates)
                {
                    if (id == host.Id || !targetIds.Contains(id))
                        continue;

                    long first = host.Id.Value;
                    long second = id.Value;
                    if (!seen.Add((Math.Min(first, second), Math.Max(first, second))))
                        continue;

                    if (!unjoin && JoinGeometryUtils.AreElementsJoined(doc, host, doc.GetElement(id)))
                        existing++;
                    else
                        pairs.Add((host.Id, id));
                }
            }

            int changed = 0;
            int skipped = 0;
            var details = new List<string>();
            if (pairs.Count > 0)
            {
                using var group = new TransactionGroup(doc, operation);
                var startStatus = group.Start();
                if (startStatus != TransactionStatus.Started)
                    throw new InvalidOperationException($"Revit 無法啟動「{operation}」交易群組（狀態：{startStatus}）。");

                RunBatch(0, pairs.Count);
                var groupStatus = group.Assimilate();
                if (groupStatus != TransactionStatus.Committed)
                    throw new InvalidOperationException($"Revit 未提交「{operation}」交易群組（狀態：{groupStatus}）。");
            }

            var summary = $"視圖使用範圍　{view.Name}\n主體元件　{hosts.Count} 個\n" +
                $"{(unjoin ? "已解除" : "已接合")}　{changed} 組";
            if (!unjoin)
                summary += $"\n原已接合　{existing} 組";
            if (skipped > 0)
                summary += $"\n無法處理　{skipped} 組\n\n{details[0]}\n\n{Guidance}";
            if (pairs.Count == 0 && existing == 0)
                summary += unjoin ? "\n所選品類之間沒有既有接合。" : "\n所選品類之間沒有找到交界候選。";

            var dialog = new TaskDialog(operation)
            {
                MainInstruction = skipped > 0 && changed == 0 ? $"{operation}未套用任何變更" : $"{operation}結果",
                MainContent = summary
            };
            if (details.Count > 1)
                dialog.ExpandedContent = string.Join("\n", details) +
                    (skipped > details.Count ? $"\n另有 {skipped - details.Count} 組略過。" : "");
            dialog.Show();

            // 快速提交整批。若 Revit 拒絕其中的配對，回復該批後再分批定位。
            void RunBatch(int start, int count)
            {
                string failure = TryBatch(start, count);
                if (failure == null)
                {
                    changed += count;
                    return;
                }

                if (count == 1)
                {
                    skipped++;
                    if (details.Count < 5)
                    {
                        var pair = pairs[start];
                        details.Add($"元件 {pair.Host.Value} / {pair.Target.Value}：{failure}");
                    }
                    return;
                }

                int half = count / 2;
                RunBatch(start, half);
                RunBatch(start + half, count - half);
            }

            string TryBatch(int start, int count)
            {
                using var transaction = new Transaction(doc, operation);
                if (transaction.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("無法啟動 Revit 交易。");

                var failures = new JoinFailures();
                transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
                    .SetClearAfterRollback(true).SetFailuresPreprocessor(failures));

                try
                {
                    for (int i = start; i < start + count; i++)
                    {
                        var host = doc.GetElement(pairs[i].Host);
                        var target = doc.GetElement(pairs[i].Target);
                        if (unjoin)
                        {
                            JoinGeometryUtils.UnjoinGeometry(doc, host, target);
                            continue;
                        }

                        JoinGeometryUtils.JoinGeometry(doc, host, target);
                        // 樓板與其他品類接合時，由其他元件切割樓板。
                        if ((host is Floor) != (target is Floor) &&
                            JoinGeometryUtils.IsCuttingElementInJoin(doc, host, target) == (host is Floor))
                        {
                            JoinGeometryUtils.SwitchJoinOrder(doc, host, target);
                        }
                    }
                }
                catch (Exception ex) when (ex is RevitArgumentException or RevitInvalidOperationException)
                {
                    if (transaction.RollBack() != TransactionStatus.RolledBack)
                        throw;
                    return CommandErrors.Describe(ex);
                }

                var status = transaction.Commit();
                return status switch
                {
                    TransactionStatus.Committed => null,
                    TransactionStatus.RolledBack when failures.Reason != null => failures.Reason,
                    _ => throw new InvalidOperationException($"Revit 未提交「{operation}」交易（狀態：{status}）。")
                };
            }
        }

        private sealed class JoinFailures : IFailuresPreprocessor
        {
            public string Reason { get; private set; }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                foreach (var failure in accessor.GetFailureMessages())
                {
                    var id = failure.GetFailureDefinitionId();
                    if (failure.GetSeverity() != FailureSeverity.Error &&
                        !id.Equals(BuiltInFailures.JoinElementsFailures.JoiningDisjointWarn) &&
                        !id.Equals(BuiltInFailures.JoinElementsFailures.CannotJoinElementsWarn) &&
                        !id.Equals(BuiltInFailures.JoinElementsFailures.CannotKeepJoined))
                        continue;

                    Reason = failure.GetDescriptionText();
                    return FailureProcessingResult.ProceedWithRollBack;
                }

                return FailureProcessingResult.Continue;
            }
        }
    }
}
