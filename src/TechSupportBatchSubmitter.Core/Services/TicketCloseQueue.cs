using TechSupportBatchSubmitter.Core.Exceptions;
using TechSupportBatchSubmitter.Core.Interfaces;
using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Services;

public sealed class TicketCloseQueue
{
    public static readonly DelaySchedule DefaultInterval =
        new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

    private readonly ITicketPlatformClient _platformClient;
    private readonly IClock _clock;
    private readonly ITicketHistoryStore? _historyStore;
    private DelaySchedule _interval;

    public TicketCloseQueue(
        ITicketPlatformClient platformClient,
        IClock? clock = null,
        TimeSpan? interval = null,
        ITicketHistoryStore? historyStore = null)
        : this(
            platformClient,
            clock,
            interval.HasValue ? DelaySchedule.Fixed(interval.Value) : null,
            historyStore)
    {
    }

    public TicketCloseQueue(
        ITicketPlatformClient platformClient,
        IClock? clock,
        DelaySchedule? interval,
        ITicketHistoryStore? historyStore = null)
    {
        _platformClient = platformClient;
        _clock = clock ?? new SystemClock();
        _interval = interval ?? DefaultInterval;
        _historyStore = historyStore;
    }

    public event EventHandler<TicketCloseLogEventArgs>? LogEmitted;
    public event EventHandler<TicketCloseProgressEventArgs>? ProgressChanged;
    public event EventHandler<CountdownChangedEventArgs>? CountdownChanged;

    public DelaySchedule Interval
    {
        get => _interval;
        set => _interval = value ?? throw new ArgumentNullException(nameof(value));
    }

    public async Task RunAsync(
        IReadOnlyCollection<PendingTicketRow> tickets,
        CloseTicketSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= CloseTicketSettings.Default;
        if (_historyStore is not null)
        {
            await _historyStore.InitializeAsync(cancellationToken);
        }

        var candidates = tickets.Where(ticket => ticket.CanClose).ToList();
        foreach (var ticket in candidates.Where(ticket => ticket.CloseState == TicketCloseState.Failed))
        {
            ticket.CloseState = TicketCloseState.Ready;
            ticket.CloseMessage = string.Empty;
        }

        EmitProgress(candidates, null);

        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index > 0)
            {
                await WaitIntervalAsync(cancellationToken);
            }

            var ticket = candidates[index];
            ticket.CloseState = TicketCloseState.Processing;
            ticket.CloseMessage = string.Empty;
            EmitProgress(candidates, ticket.CaseId);
            EmitLog(ticket, "正在执行受理并总结");

            try
            {
                var result = await _platformClient.CloseTicketAsync(ticket, settings, cancellationToken);
                ticket.CloseMessage = result.Message;
                if (result.IsClosed)
                {
                    ticket.CloseState = TicketCloseState.Succeeded;
                    ticket.ClosedAt = _clock.UtcNow.ToLocalTime();
                    ticket.IsSelected = false;
                    await RecordCloseHistoryAsync(ticket, cancellationToken);
                    EmitLog(ticket, "关闭成功，已核验状态为已解决");
                }
                else
                {
                    ticket.CloseState = TicketCloseState.Failed;
                    ticket.ClosedAt = null;
                    await RecordCloseHistoryAsync(ticket, cancellationToken);
                    EmitLog(ticket, $"关闭失败，已跳过：{ToSafeMessage(result.Message)}", true);
                }
            }
            catch (OperationCanceledException)
            {
                ticket.CloseState = TicketCloseState.Ready;
                ticket.CloseMessage = "操作已暂停";
                await RecordCloseHistoryAsync(ticket, CancellationToken.None);
                throw;
            }
            catch (SubmissionOutcomeUnknownException ex)
            {
                ticket.CloseState = TicketCloseState.PendingVerification;
                ticket.CloseMessage = ToSafeMessage(ex.Message);
                ticket.ClosedAt = null;
                await RecordCloseHistoryAsync(ticket, cancellationToken);
                EmitLog(ticket, "关闭结果不明确，队列已暂停且不会自动重试", true);
                EmitProgress(candidates, ticket.CaseId);
                throw;
            }
            catch (PlatformSessionExpiredException ex)
            {
                ticket.CloseState = TicketCloseState.Ready;
                ticket.CloseMessage = ToSafeMessage(ex.Message);
                await RecordCloseHistoryAsync(ticket, cancellationToken);
                EmitLog(ticket, "登录失效，关闭队列已暂停", true);
                EmitProgress(candidates, ticket.CaseId);
                throw;
            }
            catch (PlatformProtocolException ex)
            {
                ticket.CloseState = TicketCloseState.Ready;
                ticket.CloseMessage = ToSafeMessage(ex.Message);
                await RecordCloseHistoryAsync(ticket, cancellationToken);
                EmitLog(ticket, $"平台协议异常，关闭队列已暂停：{ticket.CloseMessage}", true);
                EmitProgress(candidates, ticket.CaseId);
                throw;
            }
            catch (Exception ex)
            {
                ticket.CloseState = TicketCloseState.Failed;
                ticket.CloseMessage = ToSafeMessage(ex.Message);
                ticket.ClosedAt = null;
                await RecordCloseHistoryAsync(ticket, cancellationToken);
                EmitLog(ticket, $"关闭失败，已跳过：{ticket.CloseMessage}", true);
            }

            EmitProgress(candidates, ticket.CaseId);
        }

        CountdownChanged?.Invoke(this, new CountdownChangedEventArgs(TimeSpan.Zero));
        EmitProgress(candidates, null);
    }

    private async Task WaitIntervalAsync(CancellationToken cancellationToken)
    {
        var remaining = _interval.NextDelay();
        while (remaining > TimeSpan.Zero)
        {
            CountdownChanged?.Invoke(this, new CountdownChangedEventArgs(remaining));
            var delay = remaining > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : remaining;
            await _clock.DelayAsync(delay, cancellationToken);
            remaining -= delay;
        }

        CountdownChanged?.Invoke(this, new CountdownChangedEventArgs(TimeSpan.Zero));
    }

    private void EmitProgress(IReadOnlyCollection<PendingTicketRow> tickets, string? currentCaseId)
    {
        var completed = tickets.Count(ticket =>
            ticket.CloseState is
                TicketCloseState.Succeeded or
                TicketCloseState.Failed or
                TicketCloseState.PendingVerification);
        ProgressChanged?.Invoke(
            this,
            new TicketCloseProgressEventArgs(
                tickets.Count,
                tickets.Count(ticket => ticket.CloseState == TicketCloseState.Succeeded),
                tickets.Count(ticket => ticket.CloseState == TicketCloseState.Failed),
                tickets.Count(ticket => ticket.CloseState == TicketCloseState.PendingVerification),
                tickets.Count - completed,
                currentCaseId));
    }

    private void EmitLog(PendingTicketRow ticket, string message, bool isError = false) =>
        LogEmitted?.Invoke(
            this,
            new TicketCloseLogEventArgs(
                _clock.UtcNow,
                ticket.CaseId,
                ticket.CaseTitle,
                message,
                isError));

    private Task RecordCloseHistoryAsync(
        PendingTicketRow ticket,
        CancellationToken cancellationToken) =>
        _historyStore is null
            ? Task.CompletedTask
            : _historyStore.RecordCloseAsync(ticket, cancellationToken);

    private static string ToSafeMessage(string message)
    {
        var safe = message.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return safe.Length <= 300 ? safe : safe[..300];
    }
}
