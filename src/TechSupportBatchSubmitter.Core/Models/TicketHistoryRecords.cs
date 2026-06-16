namespace TechSupportBatchSubmitter.Core.Models;

public sealed record SubmissionHistoryRecord(
    long Id,
    DateTimeOffset OccurredAt,
    string WorkbookPath,
    string WorksheetName,
    int ExcelRowNumber,
    string Fingerprint,
    string? CaseId,
    string Title,
    string EventType,
    string SystemName,
    string Applicant,
    string Assignee,
    SubmissionState State,
    string? Message)
{
    public string StateText => State.ToDisplayText();
}

public sealed record CloseHistoryRecord(
    long Id,
    DateTimeOffset OccurredAt,
    string SourceKind,
    string? WorkbookPath,
    string? WorksheetName,
    int? ExcelRowNumber,
    string? Fingerprint,
    string CaseId,
    string Title,
    string EventType,
    string SystemName,
    string Applicant,
    string Assignee,
    TicketCloseState State,
    string? Message)
{
    public string StateText => State.ToDisplayText();
}
