using TechSupportBatchSubmitter.Core.Exceptions;
using TechSupportBatchSubmitter.Core.Interfaces;
using TechSupportBatchSubmitter.Core.Models;
using TechSupportBatchSubmitter.Core.Services;

namespace TechSupportBatchSubmitter.Tests;

public sealed class TicketCloseQueueTests
{
    [Fact]
    public void DefaultCloseSettings_UseVerifiedDataQuestionCause()
    {
        Assert.Equal("21", CloseTicketSettings.Default.CauseTypeValue);
        Assert.Equal("数据疑问", CloseTicketSettings.Default.CauseTypeName);
        Assert.Equal("数据 / 数据疑问", CloseTicketSettings.Default.CauseTypePath);
        Assert.Equal("2", CloseTicketSettings.Default.SolveTypeValue);
        Assert.Equal("修改数据", CloseTicketSettings.Default.SolveTypeName);
    }

    [Fact]
    public async Task RunAsync_WaitsFiveSecondsAfterFirstVerification()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var platform = new FakePlatformClient(clock);
        var rows = new[] { CreateRow("20600001"), CreateRow("20600002") };
        var queue = new TicketCloseQueue(platform, clock, TimeSpan.FromSeconds(5));

        await queue.RunAsync(rows);

        Assert.Equal(2, platform.Attempts.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), platform.Attempts[1] - platform.Attempts[0]);
        Assert.All(rows, row => Assert.Equal(TicketCloseState.Succeeded, row.CloseState));
    }

    [Fact]
    public async Task RunAsync_DefaultIntervalUsesFiveToTenSeconds()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var platform = new FakePlatformClient(clock);
        var rows = new[] { CreateRow("20600001"), CreateRow("20600002") };
        var queue = new TicketCloseQueue(platform, clock);

        await queue.RunAsync(rows);

        Assert.Equal(2, platform.Attempts.Count);
        Assert.InRange(
            platform.Attempts[1] - platform.Attempts[0],
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RunAsync_OrdinaryFailureContinuesWithNextTicket()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var platform = new FakePlatformClient(clock) { FailedCaseId = "20600001" };
        var rows = new[] { CreateRow("20600001"), CreateRow("20600002") };
        var queue = new TicketCloseQueue(platform, clock, TimeSpan.Zero);

        await queue.RunAsync(rows);

        Assert.Equal(TicketCloseState.Failed, rows[0].CloseState);
        Assert.Equal(TicketCloseState.Succeeded, rows[1].CloseState);
        Assert.Equal(2, platform.Attempts.Count);
    }

    [Fact]
    public async Task RunAsync_UnknownOutcomeStopsAndMarksPendingVerification()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var platform = new FakePlatformClient(clock) { UnknownCaseId = "20600001" };
        var rows = new[] { CreateRow("20600001"), CreateRow("20600002") };
        var queue = new TicketCloseQueue(platform, clock, TimeSpan.Zero);

        await Assert.ThrowsAsync<SubmissionOutcomeUnknownException>(
            () => queue.RunAsync(rows));

        Assert.Equal(TicketCloseState.PendingVerification, rows[0].CloseState);
        Assert.Equal(TicketCloseState.Ready, rows[1].CloseState);
        Assert.Single(platform.Attempts);
    }

    [Fact]
    public async Task RunAsync_SessionExpiryStopsBeforeNextTicket()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var platform = new FakePlatformClient(clock) { ExpiredCaseId = "20600001" };
        var rows = new[] { CreateRow("20600001"), CreateRow("20600002") };
        var queue = new TicketCloseQueue(platform, clock, TimeSpan.Zero);

        await Assert.ThrowsAsync<PlatformSessionExpiredException>(
            () => queue.RunAsync(rows));

        Assert.Equal(TicketCloseState.Ready, rows[0].CloseState);
        Assert.Equal(TicketCloseState.Ready, rows[1].CloseState);
        Assert.Single(platform.Attempts);
    }

    [Fact]
    public async Task RunAsync_ForwardsCustomCloseSettings()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var platform = new FakePlatformClient(clock);
        var rows = new[] { CreateRow("20600001") };
        var settings = new CloseTicketSettings(
            "",
            "功能改进",
            "软件 / 功能改进",
            "已经调整为一级联审",
            "1",
            "修改软件",
            "已按本次口径处理完成");
        var queue = new TicketCloseQueue(platform, clock, TimeSpan.Zero);

        await queue.RunAsync(rows, settings);

        Assert.Same(settings, platform.LastSettings);
        Assert.Equal(TicketCloseState.Succeeded, rows[0].CloseState);
    }

    private static PendingTicketRow CreateRow(string caseId) => new()
    {
        CaseId = caseId,
        CaseTitle = $"标题{caseId}",
        IsSelected = true
    };

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

    private sealed class FakePlatformClient : ITicketPlatformClient
    {
        private readonly FakeClock _clock;

        public FakePlatformClient(FakeClock clock)
        {
            _clock = clock;
        }

        public string? FailedCaseId { get; init; }
        public string? UnknownCaseId { get; init; }
        public string? ExpiredCaseId { get; init; }
        public CloseTicketSettings? LastSettings { get; private set; }
        public List<DateTimeOffset> Attempts { get; } = [];

        public event EventHandler? SessionExpired
        {
            add { }
            remove { }
        }

        public Task<CloseTicketResult> CloseTicketAsync(
            PendingTicketRow ticket,
            CloseTicketSettings settings,
            CancellationToken cancellationToken = default)
        {
            Attempts.Add(_clock.UtcNow);
            LastSettings = settings;
            if (ticket.CaseId == UnknownCaseId)
            {
                throw new SubmissionOutcomeUnknownException("结果不明确");
            }

            if (ticket.CaseId == ExpiredCaseId)
            {
                throw new PlatformSessionExpiredException("登录失效");
            }

            return Task.FromResult(ticket.CaseId == FailedCaseId
                ? new CloseTicketResult(false, "模拟单条失败")
                : new CloseTicketResult(true, "已解决"));
        }

        public Task<PlatformSessionStatus> CheckSessionAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ResolvedTicket> ResolveTicketAsync(
            TicketRow row,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> AllocateCaseIdAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SaveTicketResult> SaveTicketAsync(
            ResolvedTicket ticket,
            string caseId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VerificationResult> VerifyCreatedAsync(
            string caseId,
            TicketRow expected,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PendingTicketQueryResult> QueryPendingAsync(
            string title,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PendingTicketQueryResult> QueryPendingAsync(
            PendingTicketQueryCriteria criteria,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
