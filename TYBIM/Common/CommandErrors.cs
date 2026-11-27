using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TYBIM.Diagnostics
{
    internal static class CommandErrors
    {
        internal delegate Result Command(ExternalCommandData data, ref string message, ElementSet elements);

        internal static Result Execute(string feature, string guidance, ExternalCommandData data,
            ref string message, ElementSet elements, Command command)
        {
            string view = null;
            try
            {
                var document = data.Application.ActiveUIDocument;
                if (document == null)
                {
                    Show(feature, "目前沒有開啟專案文件。", "請先開啟 Revit 專案，再執行此功能。");
                    return Result.Cancelled;
                }

                view = document.ActiveView?.Name;
                var result = command(data, ref message, elements);
                if (result == Result.Failed || (result == Result.Cancelled && !string.IsNullOrWhiteSpace(message)))
                {
                    Show(feature, string.IsNullOrWhiteSpace(message) ? "指令未取得 Revit 的錯誤原因。" : message,
                        guidance, context: ViewContext(view));
                    message = string.Empty;
                    return Result.Cancelled;
                }
                return result;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                message = string.Empty;
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Show(feature, Describe(ex), guidance, ex.ToString(), ViewContext(view));
                message = string.Empty;
                return Result.Cancelled;
            }
        }

        internal static void ExecuteEvent(string feature, string guidance, Action action)
        {
            try { action(); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
            catch (Exception ex) { Show(feature, Describe(ex), guidance, ex.ToString()); }
        }

        internal static string Describe(Exception exception)
        {
            var messages = new List<string>();
            for (var current = exception; current != null; current = current.InnerException)
                messages.Add(string.IsNullOrWhiteSpace(current.Message) ? current.GetType().Name : current.Message);
            return string.Join("\n", messages.Distinct());
        }

        internal static void Show(string feature, string reason, string guidance, string details = null, string context = null)
        {
            var dialog = new TaskDialog(feature)
            {
                MainIcon = TaskDialogIcon.TaskDialogIconError,
                MainInstruction = $"{feature}無法完成",
                MainContent = (string.IsNullOrWhiteSpace(context) ? "" : context + "\n\n") +
                    $"原因：{reason}\n\n修正方式：{guidance}"
            };
            if (!string.IsNullOrWhiteSpace(details))
                dialog.ExpandedContent = details;
            dialog.Show();
        }

        internal static void Commit(Transaction transaction)
        {
            var options = transaction.GetFailureHandlingOptions();
            var errors = new CommitErrors(options.GetFailuresPreprocessor());
            transaction.SetFailureHandlingOptions(options
                .SetClearAfterRollback(true).SetFailuresPreprocessor(errors));
            var status = transaction.Commit();
            if (status != TransactionStatus.Committed)
            {
                string reason = errors.Messages.Count > 0 ? "\n" + string.Join("\n", errors.Messages)
                    : "\nRevit 未提供交易錯誤明細。";
                throw new InvalidOperationException($"Revit 未提交「{transaction.GetName()}」（狀態：{status}）。{reason}");
            }
        }

        private static string ViewContext(string view) => string.IsNullOrWhiteSpace(view) ? null : $"視圖：{view}";

        private sealed class CommitErrors : IFailuresPreprocessor
        {
            private readonly IFailuresPreprocessor existing;
            internal List<string> Messages { get; } = new List<string>();

            internal CommitErrors(IFailuresPreprocessor existing) => this.existing = existing;

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                foreach (var failure in accessor.GetFailureMessages())
                {
                    if (failure.GetSeverity() < FailureSeverity.Error)
                        continue;

                    var ids = failure.GetFailingElementIds().Select(id => id.Value).ToArray();
                    Messages.Add(failure.GetDescriptionText() +
                        (ids.Length == 0 ? "" : $"（元件 ID：{string.Join("、", ids)}）"));
                }
                return Messages.Count > 0 ? FailureProcessingResult.ProceedWithRollBack
                    : existing?.PreprocessFailures(accessor) ?? FailureProcessingResult.Continue;
            }
        }
    }
}
