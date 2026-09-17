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
        Assert.Equal("处理类型", worksheet.Cell(1, 10).GetString());
        Assert.Equal("处理说明", worksheet.Cell(1, 11).GetString());
        Assert.Equal("技术支持编号", worksheet.Cell(1, 12).GetString());
        Assert.Equal("提交状态", worksheet.Cell(1, 13).GetString());
        Assert.Equal("实际提交时间", worksheet.Cell(1, 14).GetString());
        Assert.Equal("失败原因", worksheet.Cell(1, 15).GetString());
        Assert.Equal("受理并处理状态", worksheet.Cell(1, 16).GetString());
        Assert.Equal("受理并处理完成时间", worksheet.Cell(1, 17).GetString());
        Assert.Equal("受理并处理失败原因", worksheet.Cell(1, 18).GetString());
        Assert.Equal("关闭状态", worksheet.Cell(1, 19).GetString());
        Assert.Equal("实际关闭时间", worksheet.Cell(1, 20).GetString());
        Assert.Equal("关闭失败原因", worksheet.Cell(1, 21).GetString());
        Assert.Equal("运营", worksheet.Cell(2, 10).GetString());
        Assert.NotEqual(XLBorderStyleValues.None, worksheet.Cell(2, 21).Style.Border.BottomBorder);
        Assert.Equal(116, result.Diagnostics.TotalRows);
        Assert.Equal(116, result.Diagnostics.ValidationFailedRows);
    }

    [Fact]
    public async Task PrepareAndLoad_ReadsProcessingTypeFromLatestTemplate()
    {
        var path = Path.Combine(_tempDirectory, "latest-template.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.AddWorksheet("Sheet1");
            var headers = ExcelWorkbookRepository.RequiredHeaders
                .Append(ExcelWorkbookRepository.ProcessingTypeHeader)
                .Append(ExcelWorkbookRepository.ProcessingRemarkHeader)
                .ToArray();
            for (var index = 0; index < headers.Length; index++)
            {
                worksheet.Cell(1, index + 1).Value = headers[index];
            }

            worksheet.Cell(2, 1).Value = "1";
            worksheet.Cell(2, 2).Value = "标题";
            worksheet.Cell(2, 3).Value = "发现人";
            worksheet.Cell(2, 4).Value = "申请人";
            worksheet.Cell(2, 5).Value = "数据疑问";
            worksheet.Cell(2, 6).Value = "网签合同";
            worksheet.Cell(2, 7).Value = "受理人";
            worksheet.Cell(2, 8).Value = "描述";
            worksheet.Cell(2, 9).Value = "2026/6/15";
            worksheet.Cell(2, 10).Value = "运维";
            worksheet.Cell(2, 11).Value = "已处理";
            workbook.SaveAs(path);
        }

        var result = await new ExcelWorkbookRepository().PrepareAndLoadAsync(path);

        Assert.Equal("运维", Assert.Single(result.Rows).ProcessingType);
        Assert.Equal("已处理", Assert.Single(result.Rows).ProcessingRemark);
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
        row.AcceptanceState = TicketAcceptanceState.Succeeded;
        row.AcceptedAt = new DateTimeOffset(2026, 6, 15, 15, 31, 0, TimeSpan.FromHours(8));
        row.AcceptanceFailureReason = null;
        row.CloseState = TicketCloseState.Succeeded;
        row.ClosedAt = new DateTimeOffset(2026, 6, 15, 16, 30, 0, TimeSpan.FromHours(8));
        row.CloseFailureReason = null;

        await repository.UpdateRowsAsync(path, load.WorksheetName, [row]);
        var reloaded = await repository.PrepareAndLoadAsync(path);

        Assert.Equal("20609999", reloaded.Rows[0].TicketNumber);
        Assert.Equal(SubmissionState.Succeeded, reloaded.Rows[0].State);
        Assert.NotNull(reloaded.Rows[0].SubmittedAt);
        Assert.Equal(TicketAcceptanceState.Succeeded, reloaded.Rows[0].AcceptanceState);
        Assert.NotNull(reloaded.Rows[0].AcceptedAt);
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
    public async Task ExcelCloseTicketFactory_ExcludesInvalidTicketNumbers()
    {
        var path = CopyTemplate();
        var repository = new ExcelWorkbookRepository();
        var load = await repository.PrepareAndLoadAsync(path);
        load.Rows[0].TicketNumber = "ABC-1";
        load.Rows[0].State = SubmissionState.ValidationFailed;

        var closeRows = ExcelCloseTicketFactory.CreateCloseRows(load);

        Assert.DoesNotContain(closeRows, row => row.ExcelRowNumber == load.Rows[0].ExcelRowNumber);
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

    [Fact]
    public async Task PrepareAndLoad_WhenTemplateHasDuplicateHeader_Throws()
    {
        var path = Path.Combine(_tempDirectory, "duplicate-header.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.AddWorksheet("Sheet1");
            for (var index = 0; index < ExcelWorkbookRepository.RequiredHeaders.Length; index++)
            {
                worksheet.Cell(1, index + 1).Value = ExcelWorkbookRepository.RequiredHeaders[index];
            }

            worksheet.Cell(1, 10).Value = "标题";
            worksheet.Cell(2, 1).Value = "1";
            workbook.SaveAs(path);
        }

        var repository = new ExcelWorkbookRepository();
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.PrepareAndLoadAsync(path));

        Assert.Contains("重复表头", error.Message);
        Assert.Contains("标题", error.Message);
    }

    [Fact]
    public async Task PrepareAndLoad_WhenTemplateHasNoRows_Throws()
    {
        var path = Path.Combine(_tempDirectory, "empty-template.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.AddWorksheet("Sheet1");
            for (var index = 0; index < ExcelWorkbookRepository.RequiredHeaders.Length; index++)
            {
                worksheet.Cell(1, index + 1).Value = ExcelWorkbookRepository.RequiredHeaders[index];
            }

            workbook.SaveAs(path);
        }

        var repository = new ExcelWorkbookRepository();
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.PrepareAndLoadAsync(path));

        Assert.Contains("没有可读取的数据行", error.Message);
    }

    [Fact]
    public async Task PrepareAndLoad_WhenRowHasValidationError_MarksRowAsValidationFailed()
    {
        var path = Path.Combine(_tempDirectory, "invalid-row.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.AddWorksheet("Sheet1");
            for (var index = 0; index < ExcelWorkbookRepository.RequiredHeaders.Length; index++)
            {
                worksheet.Cell(1, index + 1).Value = ExcelWorkbookRepository.RequiredHeaders[index];
            }

            worksheet.Cell(2, 1).Value = "1";
            worksheet.Cell(2, 2).Value = string.Empty;
            worksheet.Cell(2, 3).Value = "发现人";
            worksheet.Cell(2, 4).Value = "申请人";
            worksheet.Cell(2, 5).Value = "数据疑问";
            worksheet.Cell(2, 6).Value = "就业管理";
            worksheet.Cell(2, 7).Value = "受理人";
            worksheet.Cell(2, 8).Value = "描述";
            worksheet.Cell(2, 9).Value = "2026/6/16";
            workbook.SaveAs(path);
        }

        var repository = new ExcelWorkbookRepository();
        var result = await repository.PrepareAndLoadAsync(path);

        Assert.Single(result.Rows);
        Assert.Equal(SubmissionState.ValidationFailed, result.Rows[0].State);
        Assert.Contains("标题", result.Rows[0].FailureReason);
        Assert.Equal(1, result.Diagnostics.ValidationFailedRows);
        Assert.Contains("Excel 行 2", result.Diagnostics.Warnings[0]);
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
