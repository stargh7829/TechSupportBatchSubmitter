namespace TechSupportBatchSubmitter.Core.Models;

public sealed record JournalEntry(
    string WorkbookPath,
    string WorksheetName,
    int ExcelRowNumber,
    string Fingerprint,
    string CandidateCaseId,
    SubmissionState State,
    DateTimeOffset UpdatedAt,
    string? LastError);
