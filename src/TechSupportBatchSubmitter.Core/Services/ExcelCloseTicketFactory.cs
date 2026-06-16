using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Services;

public static class ExcelCloseTicketFactory
{
    public const string SourceKind = "ExcelSubmissionList";

    public static List<PendingTicketRow> CreateCloseRows(WorkbookLoadResult workbook)
    {
        return workbook.Rows
            .Where(row => !string.IsNullOrWhiteSpace(row.TicketNumber))
            .Select(row => new PendingTicketRow
            {
                IsSelected = false,
                CaseId = row.TicketNumber!.Trim(),
                CaseTitle = row.Title,
                TypeName = row.EventType,
                SystemName = row.SystemName,
                Assignee = row.Assignee,
                CurrentHandler = row.Assignee,
                Applicant = row.Applicant,
                CreateTime = row.SubmittedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
                StatusName = row.State.ToDisplayText(),
                CloseState = row.CloseState,
                CloseMessage = row.CloseFailureReason ?? string.Empty,
                ClosedAt = row.ClosedAt,
                SourceKind = SourceKind,
                WorkbookPath = workbook.WorkbookPath,
                WorksheetName = workbook.WorksheetName,
                ExcelRowNumber = row.ExcelRowNumber,
                Fingerprint = row.Fingerprint
            })
            .ToList();
    }
}
