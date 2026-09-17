using TechSupportBatchSubmitter.Core.Exceptions;
using TechSupportBatchSubmitter.Core.Interfaces;
using TechSupportBatchSubmitter.Core.Models;
using TechSupportBatchSubmitter.Core.Services;

namespace TechSupportBatchSubmitter.Tests;

public sealed class SubmissionQueueTests
{
    [Fact]
    public async Task RunAsync_SubmitsFirstImmediatelyAndWaitsIntervalBeforeSecond()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var workbookRepository = new FakeWorkbookRepository();
        var journal = new InMemoryJournal();
        var platform = new FakePlatformClient(clock);
        var rows = new[] { CreateRow(2, "1"), CreateRow(3, "2") };
        var workbook = new WorkbookLoadResult("C:\\test.xlsx", "Sheet1", rows, null);
        var resolved = rows.ToDictionary(
            row => row.Fingerprint,
            row => CreateResolved(row));
        var queue = new SubmissionQueue(
            workbookRepository,
            journal,
            platform,
            clock,
            TimeSpan.FromMinutes(1));

        await queue.RunAsync(workbook, resolved, SubmissionRunMode.Pending);

        Assert.Equal(2, platform.SaveAttempts.Count);
        Assert.Equal(2, platform.AcceptanceAttempts.Count);
        Assert.Equal(TimeSpan.FromMinutes(1), platform.SaveAttempts[1] - platform.SaveAttempts[0]);
        Assert.All(rows, row =>
        {
            Assert.Equal(SubmissionState.Succeeded, row.State);
            Assert.Equal(TicketAcceptanceState.Succeeded, row.AcceptanceState);
            Assert.False(string.IsNullOrWhiteSpace(row.TicketNumber));
        });
    }

    [Fact]
    public async Task RunAsync_DefaultIntervalUsesThirtyToNinetySeconds()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var workbookRepository = new FakeWorkbookRepository();
        var journal = new InMemoryJournal();
        var platform = new FakePlatformClient(clock);
        var rows = new[] { CreateRow(2, "1"), CreateRow(3, "2") };
        var workbook = new WorkbookLoadResult("C:\\test.xlsx", "Sheet1", rows, null);
        var resolved = rows.ToDictionary(
            row => row.Fingerprint,
            row => CreateResolved(row));
        var queue = new SubmissionQueue(
            workbookRepository,
            journal,
            platform,
            clock);

        await queue.RunAsync(workbook, resolved, SubmissionRunMode.Pending);

        Assert.Equal(2, platform.SaveAttempts.Count);
        Assert.InRange(
            platform.SaveAttempts[1] - platform.SaveAttempts[0],
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task RunAsync_OrdinaryFailureIsSkippedAndNextRowContinues()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var workbookRepository = new FakeWorkbookRepository();
        var journal = new InMemoryJournal();
        var platform = new FakePlatformClient(clock) { FailSequence = "1" };
        var rows = new[] { CreateRow(2, "1"), CreateRow(3, "2") };
        var workbook = new WorkbookLoadResult("C:\\test.xlsx", "Sheet1", rows, null);
        var resolved = rows.ToDictionary(row => row.Fingerprint, CreateResolved);
        var queue = new SubmissionQueue(
            workbookRepository,
            journal,
            platform,
            clock,
            TimeSpan.FromMinutes(1));

        await queue.RunAsync(workbook, resolved, SubmissionRunMode.Pending);

        Assert.Equal(SubmissionState.Failed, rows[0].State);
        Assert.Equal(SubmissionState.Succeeded, rows[1].State);
        Assert.Equal(2, platform.SaveAttempts.Count);
    }

    [Fact]
    public async Task RunAsync_UnknownOutcomeIsMarkedPendingVerificationAndStops()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var workbookRepository = new FakeWorkbookRepository();
        var journal = new InMemoryJournal();
        var platform = new FakePlatformClient(clock) { UnknownSequence = "1" };
        var rows = new[] { CreateRow(2, "1"), CreateRow(3, "2") };
        var workbook = new WorkbookLoadResult("C:\\test.xlsx", "Sheet1", rows, null);
        var resolved = rows.ToDictionary(row => row.Fingerprint, CreateResolved);
        var queue = new SubmissionQueue(workbookRepository, journal, platform, clock, TimeSpan.Zero);

        await Assert.ThrowsAsync<SubmissionOutcomeUnknownException>(
            () => queue.RunAsync(workbook, resolved, SubmissionRunMode.Pending));

        Assert.Equal(SubmissionState.PendingVerification, rows[0].State);
        Assert.Equal(SubmissionState.Pending, rows[1].State);
        Assert.Single(platform.SaveAttempts);
    }

    [Fact]
    public async Task RunAsync_WaitsTwoSecondsBeforeCreationVerification()
    {
        var startedAt = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(startedAt);
        var workbookRepository = new FakeWorkbookRepository();
        var journal = new InMemoryJournal();
        var platform = new FakePlatformClient(clock);
        var row = CreateRow(2, "1");
        var workbook = new WorkbookLoadResult("C:\\test.xlsx", "Sheet1", [row], null);
        var resolved = new Dictionary<string, ResolvedTicket>
        {
            [row.Fingerprint] = CreateResolved(row)
        };
        var queue = new SubmissionQueue(workbookRepository, journal, platform, clock, TimeSpan.Zero);

        await queue.RunAsync(workbook, resolved, SubmissionRunMode.Pending);

        Assert.Equal(startedAt, Assert.Single(platform.SaveAttempts));
        Assert.Equal(startedAt.AddSeconds(2), Assert.Single(platform.VerificationAttempts));
        Assert.Equal(startedAt.AddSeconds(2), Assert.Single(platform.AcceptanceAttempts));
    }

    [Fact]
    public async Task RunAsync_AcceptanceFailureKeepsTicketNumberForNextRun()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var workbookRepository = new FakeWorkbookRepository();
        var journal = new InMemoryJournal();
        var platform = new FakePlatformClient(clock) { AcceptanceFailSequence = "1" };
        var row = CreateRow(2, "1");
        var workbook = new WorkbookLoadResult("C:\\test.xlsx", "Sheet1", [row], null);
        var queue = new SubmissionQueue(workbookRepository, journal, platform, clock, TimeSpan.Zero);

        await queue.RunAsync(
            workbook,
            new Dictionary<string, ResolvedTicket> { [row.Fingerprint] = CreateResolved(row) },
            SubmissionRunMode.Pending);

        Assert.False(string.IsNullOrWhiteSpace(row.TicketNumber));
        Assert.Equal(SubmissionState.PendingAcceptance, row.State);
        Assert.Equal(TicketAcceptanceState.Failed, row.AcceptanceState);
        Assert.Equal("平台明确返回受理并处理失败", row.AcceptanceFailureReason);
        Assert.Single(platform.SaveAttempts);
        Assert.Single(platform.AcceptanceAttempts);
    }

    [Fact]
    public async Task RunAsync_AcceptanceUnknownOutcomeStopsWithoutRepeatingAcceptance()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var workbookRepository = new FakeWorkbookRepository();
        var journal = new InMemoryJournal();
        var platform = new FakePlatformClient(clock) { AcceptanceUnknownSequence = "1" };
        var row = CreateRow(2, "1");
        var workbook = new WorkbookLoadResult("C:\\test.xlsx", "Sheet1", [row], null);
        var queue = new SubmissionQueue(workbookRepository, journal, platform, clock, TimeSpan.Zero);

        await Assert.ThrowsAsync<SubmissionOutcomeUnknownException>(() => queue.RunAsync(
            workbook,
            new Dictionary<string, ResolvedTicket> { [row.Fingerprint] = CreateResolved(row) },
            SubmissionRunMode.Pending));

        Assert.False(string.IsNullOrWhiteSpace(row.TicketNumber));
        Assert.Equal(SubmissionState.PendingAcceptanceVerification, row.State);
        Assert.Equal(TicketAcceptanceState.PendingVerification, row.AcceptanceState);
        Assert.Single(platform.AcceptanceAttempts);
    }

    [Fact]
    public async Task RunAsync_ResumesFailedAcceptanceWithoutSubmittingAnotherEvent()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var workbookRepository = new FakeWorkbookRepository();
        var journal = new InMemoryJournal();
        var platform = new FakePlatformClient(clock);
        var row = CreateRow(2, "1");
        row.TicketNumber = "20601234";
        row.State = SubmissionState.PendingAcceptance;
        row.AcceptanceState = TicketAcceptanceState.Failed;
        row.AcceptanceFailureReason = "上次失败";
        var workbook = new WorkbookLoadResult("C:\\test.xlsx", "Sheet1", [row], null);
        var queue = new SubmissionQueue(workbookRepository, journal, platform, clock, TimeSpan.Zero);

        await queue.RunAsync(workbook, new Dictionary<string, ResolvedTicket>(), SubmissionRunMode.Pending);

        Assert.Empty(platform.SaveAttempts);
        Assert.Single(platform.AcceptanceAttempts);
        Assert.Equal(SubmissionState.Succeeded, row.State);
        Assert.Equal(TicketAcceptanceState.Succeeded, row.AcceptanceState);
    }

    [Fact]
    public async Task ValidateAsync_WhenAssigneeDiffersFromCurrentUser_MarksRowAsValidationFailed()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var repository = new FakeWorkbookRepository();
        var journal = new InMemoryJournal();
        var platform = new FakePlatformClient(clock) { DisplayName = "其他受理人" };
        var row = CreateRow(2, "1");
        var workbook = new WorkbookLoadResult("C:\\test.xlsx", "Sheet1", [row], null);
        var queue = new SubmissionQueue(repository, journal, platform, clock, TimeSpan.Zero);

        var resolved = await queue.ValidateAsync(workbook);

        Assert.Empty(resolved);
        Assert.Equal(SubmissionState.ValidationFailed, row.State);
        Assert.Contains("指定受理人", row.FailureReason);
        Assert.Contains("当前登录人", row.FailureReason);
    }

    [Fact]
    public async Task RunAsync_CrashRecoveryVerifiesOldNumberWithoutSavingAgain()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var workbookRepository = new FakeWorkbookRepository();
        var journal = new InMemoryJournal();
        var platform = new FakePlatformClient(clock);
        var row = CreateRow(2, "1");
        var workbook = new WorkbookLoadResult("C:\\test.xlsx", "Sheet1", [row], null);
        await journal.UpsertCandidateAsync(
            workbook.WorkbookPath,
            workbook.WorksheetName,
            row,
            "20605555",
            SubmissionState.PendingVerification);
        platform.ExistingIds.Add("20605555");
        var queue = new SubmissionQueue(workbookRepository, journal, platform, clock, TimeSpan.Zero);

        await queue.RunAsync(
            workbook,
            new Dictionary<string, ResolvedTicket>(),
            SubmissionRunMode.Pending);

        Assert.Equal("20605555", row.TicketNumber);
        Assert.Equal(SubmissionState.Succeeded, row.State);
        Assert.Empty(platform.SaveAttempts);
    }

    [Fact]
    public async Task RunAsync_RowWithExistingTicketNumberIsSkipped()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var workbookRepository = new FakeWorkbookRepository();
        var journal = new InMemoryJournal();
        var platform = new FakePlatformClient(clock);
        var row = CreateRow(2, "1");
        row.TicketNumber = "20601234";
        row.State = SubmissionState.Succeeded;
        var workbook = new WorkbookLoadResult("C:\\test.xlsx", "Sheet1", [row], null);
        var queue = new SubmissionQueue(workbookRepository, journal, platform, clock, TimeSpan.Zero);

        await queue.RunAsync(
            workbook,
            new Dictionary<string, ResolvedTicket>(),
            SubmissionRunMode.Pending);

        Assert.Empty(platform.SaveAttempts);
        Assert.Equal("20601234", row.TicketNumber);
    }

    [Fact]
    public async Task RunAsync_FailedOnlySubmitsFailedRowsAndLeavesPendingRowsUntouched()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var workbookRepository = new FakeWorkbookRepository();
        var journal = new InMemoryJournal();
        var platform = new FakePlatformClient(clock);
        var failed = CreateRow(2, "1");
        failed.State = SubmissionState.Failed;
        var pending = CreateRow(3, "2");
        var rows = new[] { failed, pending };
        var workbook = new WorkbookLoadResult("C:\\test.xlsx", "Sheet1", rows, null);
        var resolved = rows.ToDictionary(row => row.Fingerprint, CreateResolved);
        var queue = new SubmissionQueue(workbookRepository, journal, platform, clock, TimeSpan.Zero);

        await queue.RunAsync(workbook, resolved, SubmissionRunMode.FailedOnly);

        Assert.Single(platform.SaveAttempts);
        Assert.Equal(SubmissionState.Succeeded, failed.State);
        Assert.Equal(SubmissionState.Pending, pending.State);
    }

    [Fact]
    public async Task RunAsync_SessionExpiryStopsBeforeNextRow()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var workbookRepository = new FakeWorkbookRepository();
        var journal = new InMemoryJournal();
        var platform = new FakePlatformClient(clock) { SessionExpirySequence = "1" };
        var rows = new[] { CreateRow(2, "1"), CreateRow(3, "2") };
        var workbook = new WorkbookLoadResult("C:\\test.xlsx", "Sheet1", rows, null);
        var resolved = rows.ToDictionary(row => row.Fingerprint, CreateResolved);
        var queue = new SubmissionQueue(workbookRepository, journal, platform, clock, TimeSpan.Zero);

        await Assert.ThrowsAsync<PlatformSessionExpiredException>(
            () => queue.RunAsync(workbook, resolved, SubmissionRunMode.Pending));

        Assert.Single(platform.SaveAttempts);
        Assert.Equal(SubmissionState.Pending, rows[1].State);
    }

    private static TicketRow CreateRow(int excelRow, string sequence) => new()
    {
        ExcelRowNumber = excelRow,
        Sequence = sequence,
        Title = $"标题{sequence}",
        Discoverer = "发现人",
        Applicant = "申请人",
        EventType = "数据疑问",
        ProcessingRemark = "已处理",
        SystemName = "网签合同",
        Assignee = "受理人",
        Description = $"描述{sequence}",
        OriginalDate = "2026/6/15",
        Fingerprint = $"fingerprint-{sequence}",
        State = SubmissionState.Pending
    };

    private static ResolvedTicket CreateResolved(TicketRow row) => new(
        row,
        new PlatformOption("1", "发现人"),
        new PlatformOption("2", "申请人"),
        new PlatformOption("21", "数据疑问"),
        new PlatformOption("117", "网签合同"),
        new PlatformOption("3", "受理人"));

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset now)
        {
            UtcNow = now;
        }

        public DateTimeOffset UtcNow { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkbookRepository : IWorkbookRepository
    {
        public List<int> WrittenRows { get; } = [];

        public Task<WorkbookLoadResult> PrepareAndLoadAsync(
            string workbookPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateRowsAsync(
            string workbookPath,
            string worksheetName,
            IReadOnlyCollection<TicketRow> rows,
            CancellationToken cancellationToken = default)
        {
            WrittenRows.AddRange(rows.Select(row => row.ExcelRowNumber));
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryJournal : ISubmissionJournal
    {
        private readonly Dictionary<string, JournalEntry> _entries = [];
        private DateTimeOffset? _lastAttempt;

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpsertCandidateAsync(
            string workbookPath,
            string worksheetName,
            TicketRow row,
            string candidateCaseId,
            SubmissionState state,
            string? error = null,
            CancellationToken cancellationToken = default)
        {
            _entries[row.Fingerprint] = new JournalEntry(
                workbookPath,
                worksheetName,
                row.ExcelRowNumber,
                row.Fingerprint,
                candidateCaseId,
                state,
                DateTimeOffset.UtcNow,
                error);
            return Task.CompletedTask;
        }

        public Task<JournalEntry?> GetAsync(
            string workbookPath,
            string worksheetName,
            TicketRow row,
            CancellationToken cancellationToken = default)
        {
            _entries.TryGetValue(row.Fingerprint, out var entry);
            return Task.FromResult(entry);
        }

        public Task MarkStateAsync(
            string workbookPath,
            string worksheetName,
            TicketRow row,
            SubmissionState state,
            string? error = null,
            CancellationToken cancellationToken = default)
        {
            if (_entries.TryGetValue(row.Fingerprint, out var entry))
            {
                _entries[row.Fingerprint] = entry with
                {
                    State = state,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    LastError = error
                };
            }

            return Task.CompletedTask;
        }

        public Task<DateTimeOffset?> GetLastSaveAttemptAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_lastAttempt);

        public Task RecordSaveAttemptAsync(
            DateTimeOffset attemptedAt,
            CancellationToken cancellationToken = default)
        {
            _lastAttempt = attemptedAt;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlatformClient : ITicketPlatformClient
    {
        private readonly FakeClock _clock;
        private int _nextCaseId = 20600000;

        public FakePlatformClient(FakeClock clock)
        {
            _clock = clock;
        }

        public string? FailSequence { get; init; }
        public string? UnknownSequence { get; init; }
        public string? SessionExpirySequence { get; init; }
        public string? AcceptanceFailSequence { get; init; }
        public string? AcceptanceUnknownSequence { get; init; }
        public string DisplayName { get; init; } = "测试用户";
        public List<DateTimeOffset> SaveAttempts { get; } = [];
        public List<DateTimeOffset> VerificationAttempts { get; } = [];
        public List<DateTimeOffset> AcceptanceAttempts { get; } = [];
        public HashSet<string> ExistingIds { get; } = [];
        public HashSet<string> AcceptedIds { get; } = [];
        public Dictionary<string, string> CaseSequences { get; } = [];
        public event EventHandler? SessionExpired
        {
            add { }
            remove { }
        }

        public Task<PlatformSessionStatus> CheckSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlatformSessionStatus(true, true, DisplayName, "已登录"));

        public Task<ResolvedTicket> ResolveTicketAsync(
            TicketRow row,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateResolved(row));

        public Task<string> AllocateCaseIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((++_nextCaseId).ToString());

        public Task<SaveTicketResult> SaveTicketAsync(
            ResolvedTicket ticket,
            string caseId,
            CancellationToken cancellationToken = default)
        {
            SaveAttempts.Add(_clock.UtcNow);
            if (ticket.Source.Sequence == UnknownSequence)
            {
                throw new SubmissionOutcomeUnknownException("结果不确定");
            }

            if (ticket.Source.Sequence == SessionExpirySequence)
            {
                throw new PlatformSessionExpiredException("登录失效");
            }

            if (ticket.Source.Sequence == FailSequence)
            {
                throw new InvalidOperationException("模拟单行失败");
            }

            ExistingIds.Add(caseId);
            CaseSequences[caseId] = ticket.Source.Sequence;
            return Task.FromResult(new SaveTicketResult(true, "{}"));
        }

        public Task<VerificationResult> VerifyCreatedAsync(
            string caseId,
            CancellationToken cancellationToken = default)
        {
            VerificationAttempts.Add(_clock.UtcNow);
            return Task.FromResult(new VerificationResult(
                ExistingIds.Contains(caseId),
                ExistingIds.Contains(caseId) ? "已创建" : "未创建"));
        }

        public Task<AcceptanceResult> AcceptAndHandleAsync(
            string caseId,
            string processingRemark,
            CancellationToken cancellationToken = default)
        {
            AcceptanceAttempts.Add(_clock.UtcNow);
            CaseSequences.TryGetValue(caseId, out var sequence);
            if (!string.IsNullOrWhiteSpace(AcceptanceUnknownSequence) &&
                sequence == AcceptanceUnknownSequence)
            {
                throw new SubmissionOutcomeUnknownException("受理并处理结果不确定");
            }

            if (!string.IsNullOrWhiteSpace(AcceptanceFailSequence) &&
                sequence == AcceptanceFailSequence)
            {
                return Task.FromResult(new AcceptanceResult(false, "平台明确返回受理并处理失败"));
            }

            AcceptedIds.Add(caseId);
            return Task.FromResult(new AcceptanceResult(true, "已离开未受理列表"));
        }

        public Task<AcceptanceResult> VerifyAcceptedAndHandledAsync(
            string caseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AcceptedIds.Contains(caseId)
                ? new AcceptanceResult(true, "已离开未受理列表")
                : new AcceptanceResult(false, "办件仍处于已创建状态"));

        public Task<PendingTicketQueryResult> QueryPendingAsync(
            string title,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PendingTicketQueryResult());

        public Task<PendingTicketQueryResult> QueryPendingAsync(
            PendingTicketQueryCriteria criteria,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PendingTicketQueryResult());

        public Task<CloseTicketResult> CloseTicketAsync(
            PendingTicketRow ticket,
            CloseTicketSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
