namespace TechSupportBatchSubmitter.Core.Models;

public sealed record WorkbookLoadResult(
    string WorkbookPath,
    string WorksheetName,
    IReadOnlyList<TicketRow> Rows,
    string? BackupPath)
{
    public WorkbookDiagnostics Diagnostics { get; init; } = WorkbookDiagnostics.Empty;
}

public sealed record WorkbookDiagnostics(
    int TotalRows,
    int PendingRows,
    int SucceededRows,
    int ValidationFailedRows,
    int CloseReadyRows,
    IReadOnlyList<string> Warnings)
{
    public static WorkbookDiagnostics Empty { get; } =
        new(0, 0, 0, 0, 0, Array.Empty<string>());

    public string ToSummary() =>
        $"共 {TotalRows} 条，可提交 {PendingRows} 条，已成功 {SucceededRows} 条，" +
        $"校验失败 {ValidationFailedRows} 条，可按清单关闭 {CloseReadyRows} 条";
}
