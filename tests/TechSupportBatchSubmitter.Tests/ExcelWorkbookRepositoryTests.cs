using ClosedXML.Excel;
using TechSupportBatchSubmitter.Core.Models;
using TechSupportBatchSubmitter.Core.Services;

namespace TechSupportBatchSubmitter.Tests;

public sealed class ExcelWorkbookRepositoryTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "TechSupportBatchSubmitterTests", Guid.NewGuid().ToString("N"));

    public ExcelWorkbookRepositoryTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task PrepareAndLoad_CurrentTemplate_Loads116RowsAndAddsResultColumns()
    {
        var path = CopyTemplate();
        var repository = new ExcelWorkbookRepository();

        var result = await repository.PrepareAndLoadAsync(path);

        Assert.Equal(116, result.Rows.Count);
        Assert.Equal("Sheet1", result.WorksheetName);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));

        using var workbook = new XLWorkbook(path);
        var worksheet = workbook.Worksheet("Sheet1");
        Assert.Equal("技术支持编号", worksheet.Cell(1, 10).GetString());
        Assert.Equal("提交状态", worksheet.Cell(1, 11).GetString());
        Assert.Equal("实际提交时间", worksheet.Cell(1, 12).GetString());
        Assert.Equal("失败原因", worksheet.Cell(1, 13).GetString());
        Assert.Equal("关闭状态", worksheet.Cell(1, 14).GetString());
        Assert.Equal("实际关闭时间", worksheet.Cell(1, 15).GetString());
        Assert.Equal("关闭失败原因", worksheet.Cell(1, 16).GetString());
        Assert.NotEqual(XLBorderStyleValues.None, worksheet.Cell(2, 16).Style.Border.BottomBorder);
    }

    [Fact]
    public async Task UpdateRows_WritesResultAndReloadSkipsAsSucceeded()
    {
        var path = CopyTemplate();
        var repository = new ExcelWorkbookRepository();
        var load = await repository.PrepareAndLoadAsync(path);
        var row = load.Rows[0];
        row.TicketNumber = "20609999";
        row.State = SubmissionState.Succeeded;
        row.SubmittedAt = new DateTimeOffset(2026, 6, 15, 15, 30, 0, TimeSpan.FromHours(8));
        row.FailureReason = null;
        row.CloseState = TicketCloseState.Succeeded;
        row.ClosedAt = new DateTimeOffset(2026, 6, 15, 16, 30, 0, TimeSpan.FromHours(8));
        row.CloseFailureReason = null;

        await repository.UpdateRowsAsync(path, load.WorksheetName, [row]);
        var reloaded = await repository.PrepareAndLoadAsync(path);

        Assert.Equal("20609999", reloaded.Rows[0].TicketNumber);
        Assert.Equal(SubmissionState.Succeeded, reloaded.Rows[0].State);
        Assert.NotNull(reloaded.Rows[0].SubmittedAt);
        Assert.Equal(TicketCloseState.Succeeded, reloaded.Rows[0].CloseState);
        Assert.NotNull(reloaded.Rows[0].ClosedAt);
    }

    [Fact]
    public async Task ExcelCloseTicketFactory_UsesRowsWithTicketNumbersOnly()
    {
        var path = CopyTemplate();
        var repository = new ExcelWorkbookRepository();
        var load = await repository.PrepareAndLoadAsync(path);
        load.Rows[0].TicketNumber = "20601111";
        load.Rows[0].State = SubmissionState.Succeeded;
        load.Rows[0].SubmittedAt = new DateTimeOffset(2026, 6, 15, 15, 30, 0, TimeSpan.FromHours(8));
        load.Rows[0].CloseState = TicketCloseState.Failed;
        load.Rows[0].CloseFailureReason = "模拟失败";
        load.Rows[1].TicketNumber = null;

        var closeRows = ExcelCloseTicketFactory.CreateCloseRows(load);

        Assert.Contains(closeRows, row =>
            row.CaseId == "20601111" &&
            row.SourceKind == ExcelCloseTicketFactory.SourceKind &&
            row.ExcelRowNumber == load.Rows[0].ExcelRowNumber &&
            row.CloseState == TicketCloseState.Failed &&
            row.CloseMessage == "模拟失败" &&
            !row.IsSelected);
        Assert.DoesNotContain(closeRows, row => row.ExcelRowNumber == load.Rows[1].ExcelRowNumber);
    }

    [Fact]
    public async Task UpdateRows_WhenFileChangedExternally_Throws()
    {
        var path = CopyTemplate();
        var repository = new ExcelWorkbookRepository();
        var load = await repository.PrepareAndLoadAsync(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var error = await Assert.ThrowsAsync<IOException>(
            () => repository.UpdateRowsAsync(path, load.WorksheetName, [load.Rows[0]]));

        Assert.Contains("其他程序修改", error.Message);
    }

    [Fact]
    public async Task PrepareAndLoad_WhenRequiredHeaderMissing_Throws()
    {
        var path = Path.Combine(_tempDirectory, "invalid.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.AddWorksheet("Sheet1");
            worksheet.Cell("A1").Value = "标题";
            workbook.SaveAs(path);
        }

        var repository = new ExcelWorkbookRepository();
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.PrepareAndLoadAsync(path));

        Assert.Contains("完整模板表头", error.Message);
    }

    private string CopyTemplate()
    {
        var source = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "技术支持260608-260612.xlsx"));
        var destination = Path.Combine(_tempDirectory, "template.xlsx");
        File.Copy(source, destination);
        return destination;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
