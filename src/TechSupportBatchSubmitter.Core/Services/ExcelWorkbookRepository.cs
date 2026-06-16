using ClosedXML.Excel;
using TechSupportBatchSubmitter.Core.Interfaces;
using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Services;

public sealed class ExcelWorkbookRepository : IWorkbookRepository
{
    public static readonly string[] RequiredHeaders =
    [
        "序号",
        "标题",
        "发现人",
        "申请人",
        "事件类型",
        "所属系统",
        "指定受理人",
        "描述",
        "日期"
    ];

    public static readonly string[] ResultHeaders =
    [
        "技术支持编号",
        "提交状态",
        "实际提交时间",
        "失败原因",
        "关闭状态",
        "实际关闭时间",
        "关闭失败原因"
    ];

    private readonly Dictionary<string, DateTime> _expectedWriteTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _backupPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _fileGate = new(1, 1);

    public async Task<WorkbookLoadResult> PrepareAndLoadAsync(
        string workbookPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidatePath(workbookPath);
        await _fileGate.WaitAsync(cancellationToken);
        try
        {
            EnsureExclusiveAccess(fullPath);

            if (!_backupPaths.TryGetValue(fullPath, out var backupPath))
            {
                backupPath = CreateBackup(fullPath);
                _backupPaths[fullPath] = backupPath;
            }

            using var workbook = new XLWorkbook(fullPath);
            var worksheet = FindWorksheet(workbook);
            EnsureResultColumns(worksheet);
            ApplyBorders(worksheet);
            SaveAtomically(workbook, fullPath);
            _expectedWriteTimes[fullPath] = File.GetLastWriteTimeUtc(fullPath);

            return LoadPreparedWorkbook(fullPath, worksheet.Name, backupPath);
        }
        catch (IOException ex)
        {
            throw new IOException($"无法独占访问 Excel 文件，请关闭正在打开该文件的 Excel 窗口：{fullPath}", ex);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task UpdateRowsAsync(
        string workbookPath,
        string worksheetName,
        IReadOnlyCollection<TicketRow> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var fullPath = ValidatePath(workbookPath);
        await _fileGate.WaitAsync(cancellationToken);
        try
        {
            EnsureFileWasNotExternallyModified(fullPath);
            EnsureExclusiveAccess(fullPath);

            using var workbook = new XLWorkbook(fullPath);
            var worksheet = workbook.Worksheets.FirstOrDefault(
                sheet => string.Equals(sheet.Name, worksheetName, StringComparison.Ordinal));
            if (worksheet is null)
            {
                throw new InvalidDataException($"工作表“{worksheetName}”不存在。");
            }

            var headers = ReadHeaderMap(worksheet);
            foreach (var header in ResultHeaders)
            {
                if (!headers.ContainsKey(header))
                {
                    throw new InvalidDataException($"Excel 缺少结果列“{header}”，请重新选择并校验文件。");
                }
            }

            foreach (var row in rows)
            {
                worksheet.Cell(row.ExcelRowNumber, headers["技术支持编号"]).Value = row.TicketNumber ?? string.Empty;
                worksheet.Cell(row.ExcelRowNumber, headers["提交状态"]).Value = row.State.ToDisplayText();
                var submittedAtCell = worksheet.Cell(row.ExcelRowNumber, headers["实际提交时间"]);
                if (row.SubmittedAt is { } submittedAt)
                {
                    submittedAtCell.Value = submittedAt.LocalDateTime;
                    submittedAtCell.Style.DateFormat.Format = "yyyy/m/d h:mm:ss";
                }
                else
                {
                    submittedAtCell.Clear(XLClearOptions.Contents);
                }

                worksheet.Cell(row.ExcelRowNumber, headers["失败原因"]).Value = row.FailureReason ?? string.Empty;
                worksheet.Cell(row.ExcelRowNumber, headers["关闭状态"]).Value = row.CloseState.ToDisplayText();
                var closedAtCell = worksheet.Cell(row.ExcelRowNumber, headers["实际关闭时间"]);
                if (row.ClosedAt is { } closedAt)
                {
                    closedAtCell.Value = closedAt.LocalDateTime;
                    closedAtCell.Style.DateFormat.Format = "yyyy/m/d h:mm:ss";
                }
                else
                {
                    closedAtCell.Clear(XLClearOptions.Contents);
                }

                worksheet.Cell(row.ExcelRowNumber, headers["关闭失败原因"]).Value =
                    row.CloseFailureReason ?? string.Empty;
            }

            ApplyBorders(worksheet);
            SaveAtomically(workbook, fullPath);
            _expectedWriteTimes[fullPath] = File.GetLastWriteTimeUtc(fullPath);
        }
        catch (IOException ex) when (!ex.Message.Contains("其他程序修改", StringComparison.Ordinal))
        {
            throw new IOException($"Excel 文件无法写入，请关闭 Excel 后再继续：{fullPath}", ex);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private WorkbookLoadResult LoadPreparedWorkbook(string fullPath, string worksheetName, string? backupPath)
    {
        using var workbook = new XLWorkbook(fullPath);
        var worksheet = workbook.Worksheet(worksheetName);
        var headers = ReadHeaderMap(worksheet);
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        var rows = new List<TicketRow>();

        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            var sequence = GetCellText(worksheet, rowNumber, headers["序号"]);
            var title = GetCellText(worksheet, rowNumber, headers["标题"]);
            var description = GetCellText(worksheet, rowNumber, headers["描述"]);
            if (string.IsNullOrWhiteSpace(sequence) &&
                string.IsNullOrWhiteSpace(title) &&
                string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            var discoverer = GetCellText(worksheet, rowNumber, headers["发现人"]);
            var applicant = GetCellText(worksheet, rowNumber, headers["申请人"]);
            var eventType = GetCellText(worksheet, rowNumber, headers["事件类型"]);
            var systemName = GetCellText(worksheet, rowNumber, headers["所属系统"]);
            var assignee = GetCellText(worksheet, rowNumber, headers["指定受理人"]);
            var originalDate = GetCellText(worksheet, rowNumber, headers["日期"]);
            var ticketNumber = GetOptionalCellText(worksheet, rowNumber, headers, "技术支持编号");
            var stateText = GetOptionalCellText(worksheet, rowNumber, headers, "提交状态");
            var submittedAt = ReadOptionalDateTime(worksheet, rowNumber, headers, "实际提交时间");
            var failureReason = GetOptionalCellText(worksheet, rowNumber, headers, "失败原因");
            var closeStateText = GetOptionalCellText(worksheet, rowNumber, headers, "关闭状态");
            var closedAt = ReadOptionalDateTime(worksheet, rowNumber, headers, "实际关闭时间");
            var closeFailureReason = GetOptionalCellText(worksheet, rowNumber, headers, "关闭失败原因");

            var state = !string.IsNullOrWhiteSpace(ticketNumber)
                ? SubmissionState.Succeeded
                : SubmissionStateExtensions.FromDisplayText(stateText);

            rows.Add(new TicketRow
            {
                ExcelRowNumber = rowNumber,
                Sequence = sequence,
                Title = title,
                Discoverer = discoverer,
                Applicant = applicant,
                EventType = eventType,
                SystemName = systemName,
                Assignee = assignee,
                Description = description,
                OriginalDate = originalDate,
                Fingerprint = TicketFingerprint.Compute(
                    rowNumber,
                    sequence,
                    title,
                    discoverer,
                    applicant,
                    eventType,
                    systemName,
                    assignee,
                    description,
                    originalDate),
                TicketNumber = ticketNumber,
                State = state,
                SubmittedAt = submittedAt,
                FailureReason = failureReason,
                CloseState = TicketCloseStateExtensions.FromDisplayText(closeStateText) ??
                    (closedAt is not null ? TicketCloseState.Succeeded : TicketCloseState.Ready),
                ClosedAt = closedAt,
                CloseFailureReason = closeFailureReason
            });
        }

        return new WorkbookLoadResult(fullPath, worksheetName, rows, backupPath);
    }

    private static string ValidatePath(string workbookPath)
    {
        if (string.IsNullOrWhiteSpace(workbookPath))
        {
            throw new ArgumentException("请选择 Excel 文件。", nameof(workbookPath));
        }

        var fullPath = Path.GetFullPath(workbookPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Excel 文件不存在。", fullPath);
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("首版仅支持 .xlsx 文件。");
        }

        return fullPath;
    }

    private static IXLWorksheet FindWorksheet(XLWorkbook workbook)
    {
        var matches = workbook.Worksheets
            .Where(sheet =>
            {
                var headers = ReadHeaderMap(sheet);
                return RequiredHeaders.All(headers.ContainsKey);
            })
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException(
                $"没有找到包含完整模板表头的工作表。必需表头：{string.Join("、", RequiredHeaders)}"),
            _ => throw new InvalidDataException("检测到多个符合模板的工作表，请只保留一个提交清单工作表。")
        };
    }

    private static Dictionary<string, int> ReadHeaderMap(IXLWorksheet worksheet)
    {
        var lastColumn = worksheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        var headers = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var column = 1; column <= lastColumn; column++)
        {
            var header = worksheet.Cell(1, column).GetString().Trim();
            if (!string.IsNullOrWhiteSpace(header) && !headers.ContainsKey(header))
            {
                headers[header] = column;
            }
        }

        return headers;
    }

    private static void EnsureResultColumns(IXLWorksheet worksheet)
    {
        var headers = ReadHeaderMap(worksheet);
        var nextColumn = Math.Max(worksheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0, RequiredHeaders.Length);
        var sourceHeader = worksheet.Cell(1, headers["日期"]);

        foreach (var resultHeader in ResultHeaders)
        {
            if (headers.ContainsKey(resultHeader))
            {
                continue;
            }

            nextColumn++;
            var cell = worksheet.Cell(1, nextColumn);
            cell.Value = resultHeader;
            cell.Style = sourceHeader.Style;
            cell.Style.Alignment.WrapText = true;
            headers[resultHeader] = nextColumn;
        }
    }

    private static void ApplyBorders(IXLWorksheet worksheet)
    {
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        var lastColumn = worksheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? RequiredHeaders.Length;
        var usedRange = worksheet.Range(1, 1, lastRow, lastColumn);
        usedRange.Style.Border.SetTopBorder(XLBorderStyleValues.Thin);
        usedRange.Style.Border.SetBottomBorder(XLBorderStyleValues.Thin);
        usedRange.Style.Border.SetLeftBorder(XLBorderStyleValues.Thin);
        usedRange.Style.Border.SetRightBorder(XLBorderStyleValues.Thin);
        usedRange.Style.Border.SetInsideBorder(XLBorderStyleValues.Thin);
        usedRange.Style.Border.SetTopBorderColor(XLColor.FromHtml("#B8C2CC"));
        usedRange.Style.Border.SetBottomBorderColor(XLColor.FromHtml("#B8C2CC"));
        usedRange.Style.Border.SetLeftBorderColor(XLColor.FromHtml("#B8C2CC"));
        usedRange.Style.Border.SetRightBorderColor(XLColor.FromHtml("#B8C2CC"));
        usedRange.Style.Border.SetInsideBorderColor(XLColor.FromHtml("#D5DCE3"));

        var headerRange = worksheet.Range(1, 1, 1, lastColumn);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        headerRange.Style.Border.BottomBorderColor = XLColor.FromHtml("#667788");
    }

    private static string CreateBackup(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath)!;
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupPath = Path.Combine(directory, $"{fileName}.backup_{timestamp}{extension}");
        var suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = Path.Combine(directory, $"{fileName}.backup_{timestamp}_{suffix++}{extension}");
        }

        File.Copy(fullPath, backupPath, overwrite: false);
        return backupPath;
    }

    private static void SaveAtomically(XLWorkbook workbook, string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)!;
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp.xlsx");

        try
        {
            workbook.SaveAs(tempPath);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private void EnsureFileWasNotExternallyModified(string fullPath)
    {
        if (!_expectedWriteTimes.TryGetValue(fullPath, out var expected))
        {
            return;
        }

        var actual = File.GetLastWriteTimeUtc(fullPath);
        if (actual != expected)
        {
            throw new IOException("Excel 文件在程序运行期间被其他程序修改，请重新选择并校验后继续。");
        }
    }

    private static void EnsureExclusiveAccess(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static string GetCellText(IXLWorksheet worksheet, int row, int column)
    {
        var cell = worksheet.Cell(row, column);
        return cell.DataType == XLDataType.DateTime
            ? cell.GetDateTime().ToString("yyyy/M/d H:mm:ss")
            : cell.GetFormattedString().Trim();
    }

    private static string? GetOptionalCellText(
        IXLWorksheet worksheet,
        int row,
        IReadOnlyDictionary<string, int> headers,
        string header)
    {
        return headers.TryGetValue(header, out var column)
            ? NullIfWhiteSpace(GetCellText(worksheet, row, column))
            : null;
    }

    private static DateTimeOffset? ReadOptionalDateTime(
        IXLWorksheet worksheet,
        int row,
        IReadOnlyDictionary<string, int> headers,
        string header)
    {
        if (!headers.TryGetValue(header, out var column))
        {
            return null;
        }

        var cell = worksheet.Cell(row, column);
        if (cell.IsEmpty())
        {
            return null;
        }

        if (cell.TryGetValue<DateTime>(out var dateTime))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Local));
        }

        return DateTimeOffset.TryParse(cell.GetFormattedString(), out var parsed) ? parsed : null;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
