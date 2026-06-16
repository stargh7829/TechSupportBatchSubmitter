using TechSupportBatchSubmitter.Core.Models;
using TechSupportBatchSubmitter.Core.Services;

namespace TechSupportBatchSubmitter.Tests;

public sealed class SqliteSubmissionJournalTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "TechSupportBatchSubmitterTests", Guid.NewGuid().ToString("N"));

    public SqliteSubmissionJournalTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task CandidateAndState_AreAvailableAfterJournalIsReopened()
    {
        var databasePath = Path.Combine(_tempDirectory, "submission.db");
        var workbookPath = Path.Combine(_tempDirectory, "source.xlsx");
        var row = CreateRow();
        var first = new SqliteSubmissionJournal(databasePath);
        await first.InitializeAsync();
        await first.UpsertCandidateAsync(
            workbookPath,
            "Sheet1",
            row,
            "20608888",
            SubmissionState.Submitting);

        var reopened = new SqliteSubmissionJournal(databasePath);
        await reopened.InitializeAsync();
        var entry = await reopened.GetAsync(workbookPath, "Sheet1", row);

        Assert.NotNull(entry);
        Assert.Equal("20608888", entry.CandidateCaseId);
        Assert.Equal(SubmissionState.Submitting, entry.State);
    }

    [Fact]
    public async Task MarkState_UpdatesExistingCandidateWithoutChangingNumber()
    {
        var databasePath = Path.Combine(_tempDirectory, "submission.db");
        var workbookPath = Path.Combine(_tempDirectory, "source.xlsx");
        var row = CreateRow();
        var journal = new SqliteSubmissionJournal(databasePath);
        await journal.InitializeAsync();
        await journal.UpsertCandidateAsync(
            workbookPath,
            "Sheet1",
            row,
            "20607777",
            SubmissionState.Pending);

        await journal.MarkStateAsync(
            workbookPath,
            "Sheet1",
            row,
            SubmissionState.PendingVerification,
            "网络状态不确定");
        var entry = await journal.GetAsync(workbookPath, "Sheet1", row);

        Assert.NotNull(entry);
        Assert.Equal("20607777", entry.CandidateCaseId);
        Assert.Equal(SubmissionState.PendingVerification, entry.State);
        Assert.Equal("网络状态不确定", entry.LastError);
    }

    [Fact]
    public async Task SaveAttemptTimestamp_IsPersisted()
    {
        var databasePath = Path.Combine(_tempDirectory, "submission.db");
        var journal = new SqliteSubmissionJournal(databasePath);
        await journal.InitializeAsync();
        var timestamp = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

        await journal.RecordSaveAttemptAsync(timestamp);
        var actual = await journal.GetLastSaveAttemptAsync();

        Assert.Equal(timestamp, actual);
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
        Fingerprint = TicketFingerprint.Compute(
            2,
            "1",
            "测试标题",
            "发现人",
            "申请人",
            "数据疑问",
            "网签合同",
            "受理人",
            "测试描述",
            "2026/6/15 10:00:00"),
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
