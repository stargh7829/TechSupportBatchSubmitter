namespace TechSupportBatchSubmitter.Core.Models;

public sealed record WorkbookLoadResult(
    string WorkbookPath,
    string WorksheetName,
    IReadOnlyList<TicketRow> Rows,
    string? BackupPath);
