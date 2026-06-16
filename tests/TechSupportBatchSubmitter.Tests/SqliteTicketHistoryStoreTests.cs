using TechSupportBatchSubmitter.Core.Models;
using TechSupportBatchSubmitter.Core.Services;

namespace TechSupportBatchSubmitter.Tests;

public sealed class SqliteTicketHistoryStoreTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "TechSupportBatchSubmitterTests", Guid.NewGuid().ToString("N"));

    public SqliteTicketHistoryStoreTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task SubmissionAndCloseHistory_ArePersistedAfterReopen()
    {
        var databasePath = Path.Combine(_tempDirectory, "history.db");
        var row = CreateRow();
        row.TicketNumber = "20608888";
        row.State = SubmissionState.Succeeded;
        var first = new SqliteTicketHistoryStore(databasePath);
        await first.InitializeAsync();
        await first.RecordSubmissionAsync("C:\\temp\\source.xlsx", "Sheet1", row, "提交成功");
        await first.RecordCloseAsync(new PendingTicketRow
        {
            SourceKind = ExcelCloseTicketFactory.SourceKind,
            WorkbookPath = "C:\\temp\\source.xlsx",
            WorksheetName = "Sheet1",
            ExcelRowNumber = row.ExcelRowNumber,
            Fingerprint = row.Fingerprint,
            CaseId = "20608888",
            CaseTitle = row.Title,
            TypeName = row.EventType,
            SystemName = row.SystemName,
            Applicant = row.Applicant,
            Assignee = row.Assignee,
            CloseState = TicketCloseState.Succeeded,
            CloseMessage = "已解决"
        });

        var reopened = new SqliteTicketHistoryStore(databasePath);
        await reopened.InitializeAsync();
        var submissions = await reopened.GetSubmissionHistoryAsync();
        var closes = await reopened.GetCloseHistoryAsync();

        Assert.Single(submissions);
        Assert.Equal("20608888", submissions[0].CaseId);
        Assert.Equal(SubmissionState.Succeeded, submissions[0].State);
        Assert.Single(closes);
        Assert.Equal("20608888", closes[0].CaseId);
        Assert.Equal(TicketCloseState.Succeeded, closes[0].State);
        Assert.Equal(ExcelCloseTicketFactory.SourceKind, closes[0].SourceKind);
    }

    private static TicketRow CreateRow() => new()
    {
        ExcelRowNumber = 2,
        Sequence = "1",
        Title = "测试标题",
        Discoverer = "发现人",
        Applicant = "申请人",
        EventType = "数据疑问",
        SystemName = "网签合同",
        Assignee = "受理人",
        Description = "测试描述",
        OriginalDate = "2026/6/15 10:00:00",
        Fingerprint = "fingerprint-1",
        State = SubmissionState.Pending
    };

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
