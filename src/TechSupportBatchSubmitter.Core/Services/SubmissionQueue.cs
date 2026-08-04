using TechSupportBatchSubmitter.Core.Exceptions;
using TechSupportBatchSubmitter.Core.Interfaces;
using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Services;

public sealed class SubmissionQueue : ISubmissionQueue
{
    public static readonly DelaySchedule DefaultInterval =
        new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90));
    public static readonly TimeSpan CreationVerificationDelay = TimeSpan.FromSeconds(2);

    private readonly IWorkbookRepository _workbookRepository;
    private readonly ISubmissionJournal _journal;
    private readonly ITicketPlatformClient _platformClient;
    private readonly IClock _clock;
    private readonly ITicketHistoryStore? _historyStore;
    private DelaySchedule _submissionInterval;

    public SubmissionQueue(
        IWorkbookRepository workbookRepository,
        ISubmissionJournal journal,
        ITicketPlatformClient platformClient,
        IClock? clock = null,
        TimeSpan? submissionInterval = null,
        ITicketHistoryStore? historyStore = null)
        : this(
            workbookRepository,
            journal,
            platformClient,
            clock,
            submissionInterval.HasValue ? DelaySchedule.Fixed(submissionInterval.Value) : null,
            historyStore)
    {
    }

    public SubmissionQueue(
        IWorkbookRepository workbookRepository,
        ISubmissionJournal journal,
        ITicketPlatformClient platformClient,
        IClock? clock,
        DelaySchedule? submissionInterval,
        ITicketHistoryStore? historyStore = null)
    {
        _workbookRepository = workbookRepository;
        _journal = journal;
        _platformClient = platformClient;
        _clock = clock ?? new SystemClock();
        _submissionInterval = submissionInterval ?? DefaultInterval;
        _historyStore = historyStore;
    }

    public event EventHandler<SubmissionLogEventArgs>? LogEmitted;
    public event EventHandler<SubmissionProgressEventArgs>? ProgressChanged;
    public event EventHandler<CountdownChangedEventArgs>? CountdownChanged;

    public DelaySchedule SubmissionInterval
    {
        get => _submissionInterval;
        set => _submissionInterval = value ?? throw new ArgumentNullException(nameof(value));
    }

    public async Task<IReadOnlyDictionary<string, ResolvedTicket>> ValidateAsync(
        WorkbookLoadResult workbook,
        CancellationToken cancellationToken = default)
    {
        var resolved = new Dictionary<string, ResolvedTicket>(StringComparer.Ordinal);
        var changed = new List<TicketRow>();

        try
        {
            foreach (var row in workbook.Rows.Where(row => string.IsNullOrWhiteSpace(row.TicketNumber)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requiredError = TicketInputValidator.Validate(row);
                if (requiredError is not null)
                {
                    row.State = SubmissionState.ValidationFailed;
                    row.FailureReason = requiredError;
                    changed.Add(row);
                    EmitLog(row, requiredError, isError: true);
                    EmitProgress(workbook.Rows, row.ExcelRowNumber);
                    continue;
                }

                try
                {
                    var ticket = await _platformClient.ResolveTicketAsync(row, cancellationToken);
                    resolved[row.Fingerprint] = ticket;
                    if (row.State == SubmissionState.ValidationFailed)
                    {
                        row.State = SubmissionState.Pending;
                    }

                    if (row.State == SubmissionState.Pending)
                    {
                        row.FailureReason = null;
                    }

                    changed.Add(row);
                    EmitLog(row, "平台字段校验通过");
                }
                catch (InvalidDataException ex)
                {
                    row.State = SubmissionState.ValidationFailed;
                    row.FailureReason = ex.Message;
                    changed.Add(row);
                    EmitLog(row, ex.Message, isError: true);
                }

                EmitProgress(workbook.Rows, row.ExcelRowNumber);
            }
        }
        finally
        {
            if (changed.Count > 0)
            {
                await _workbookRepository.UpdateRowsAsync(
                    workbook.WorkbookPath,
                    workbook.WorksheetName,
                    changed,
                    CancellationToken.None);
            }
        }

        EmitProgress(workbook.Rows, null);
        return resolved;
    }

    public async Task RunAsync(
        WorkbookLoadResult workbook,
        IReadOnlyDictionary<string, ResolvedTicket> resolvedTickets,
        SubmissionRunMode mode,
        CancellationToken cancellationToken = default)
    {
        await _journal.InitializeAsync(cancellationToken);
        if (_historyStore is not null)
        {
            await _historyStore.InitializeAsync(cancellationToken);
        }

        await RecoverUncertainRowsAsync(workbook, cancellationToken);

        var candidates = workbook.Rows
            .Where(row => string.IsNullOrWhiteSpace(row.TicketNumber))
            .Where(row => mode == SubmissionRunMode.FailedOnly
                ? row.State == SubmissionState.Failed
                : row.State == SubmissionState.Pending)
            .ToList();

        foreach (var row in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EmitProgress(workbook.Rows, row.ExcelRowNumber);

            if (!resolvedTickets.TryGetValue(row.Fingerprint, out var resolvedTicket))
            {
                row.State = SubmissionState.ValidationFailed;
                row.FailureReason = "该行尚未通过本次平台预检，请重新执行“校验清单”。";
                await PersistRowAsync(workbook, row, cancellationToken);
                await RecordSubmissionHistoryAsync(workbook, row, row.FailureReason, cancellationToken);
                EmitLog(row, row.FailureReason, isError: true);
                continue;
            }

            try
            {
                await SubmitRowAsync(workbook, row, resolvedTicket, cancellationToken);
            }
            catch (PlatformSessionExpiredException)
            {
                EmitLog(row, "登录失效，队列已暂停。重新登录后点击继续。", isError: true);
                throw;
            }
            catch (SubmissionOutcomeUnknownException ex)
            {
                row.State = SubmissionState.PendingVerification;
                row.FailureReason = ex.Message;
                await _journal.MarkStateAsync(
                    workbook.WorkbookPath,
                    workbook.WorksheetName,
                    row,
                    SubmissionState.PendingVerification,
                    ex.Message,
                    cancellationToken);
                await PersistRowAsync(workbook, row, cancellationToken);
                await RecordSubmissionHistoryAsync(workbook, row, ex.Message, cancellationToken);
                EmitLog(row, "保存结果不确定，已暂停且不会自动换号重提。", isError: true);
                throw;
            }
            catch (PlatformProtocolException ex)
            {
                EmitLog(row, $"平台协议异常，队列已暂停：{ex.Message}", isError: true);
                throw;
            }
            catch (IOException)
            {
                EmitLog(row, "Excel 无法写入，队列已暂停。", isError: true);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.State = SubmissionState.Failed;
                row.FailureReason = ToSafeError(ex);
                var entry = await _journal.GetAsync(
                    workbook.WorkbookPath,
                    workbook.WorksheetName,
                    row,
                    cancellationToken);
                if (entry is not null)
                {
                    await _journal.MarkStateAsync(
                        workbook.WorkbookPath,
                        workbook.WorksheetName,
                        row,
                        SubmissionState.Failed,
                        row.FailureReason,
                        cancellationToken);
                }

                await PersistRowAsync(workbook, row, cancellationToken);
                await RecordSubmissionHistoryAsync(workbook, row, row.FailureReason, cancellationToken);
                EmitLog(row, $"提交失败，已跳过：{row.FailureReason}", isError: true);
            }
        }

        CountdownChanged?.Invoke(this, new CountdownChangedEventArgs(TimeSpan.Zero));
        EmitProgress(workbook.Rows, null);
    }

    private async Task SubmitRowAsync(
        WorkbookLoadResult workbook,
        TicketRow row,
        ResolvedTicket resolvedTicket,
        CancellationToken cancellationToken)
    {
        var journalEntry = await _journal.GetAsync(
            workbook.WorkbookPath,
            workbook.WorksheetName,
            row,
            cancellationToken);

        var caseId = journalEntry?.State == SubmissionState.Pending
            ? journalEntry.CandidateCaseId
            : await _platformClient.AllocateCaseIdAsync(cancellationToken);

        if (journalEntry is null || journalEntry.State != SubmissionState.Pending)
        {
            await _journal.UpsertCandidateAsync(
                workbook.WorkbookPath,
                workbook.WorksheetName,
                row,
                caseId,
                SubmissionState.Pending,
                cancellationToken: cancellationToken);
        }

        await WaitForSubmissionIntervalAsync(cancellationToken);

        row.State = SubmissionState.Submitting;
        row.FailureReason = null;
        await _journal.MarkStateAsync(
            workbook.WorkbookPath,
            workbook.WorksheetName,
            row,
            SubmissionState.Submitting,
            cancellationToken: cancellationToken);
        await PersistRowAsync(workbook, row, cancellationToken);

        var attemptTime = _clock.UtcNow;
        await _journal.RecordSaveAttemptAsync(attemptTime, cancellationToken);
        EmitLog(row, $"正在提交候选编号 {caseId}");
        await _platformClient.SaveTicketAsync(resolvedTicket, caseId, cancellationToken);

        row.State = SubmissionState.PendingVerification;
        await _journal.MarkStateAsync(
            workbook.WorkbookPath,
            workbook.WorksheetName,
            row,
            SubmissionState.PendingVerification,
            cancellationToken: cancellationToken);
        await PersistRowAsync(workbook, row, cancellationToken);

        EmitLog(row, $"保存请求已返回，{CreationVerificationDelay.TotalSeconds:0} 秒后按编号 {caseId} 核验");
        await _clock.DelayAsync(CreationVerificationDelay, cancellationToken);
        var verification = await _platformClient.VerifyCreatedAsync(caseId, cancellationToken);
        if (!verification.IsCreated)
        {
            row.State = SubmissionState.PendingVerification;
            row.FailureReason = verification.Message;
            await _journal.MarkStateAsync(
                workbook.WorkbookPath,
                workbook.WorksheetName,
                row,
                SubmissionState.PendingVerification,
                verification.Message,
                cancellationToken);
            await PersistRowAsync(workbook, row, cancellationToken);
            await RecordSubmissionHistoryAsync(workbook, row, verification.Message, cancellationToken);
            EmitLog(row, "未查到匹配创建记录，保留旧编号等待再次核验。", isError: true);
            return;
        }

        row.TicketNumber = caseId;
        row.State = SubmissionState.Succeeded;
        row.SubmittedAt = _clock.UtcNow.ToLocalTime();
        row.FailureReason = null;
        await _journal.MarkStateAsync(
            workbook.WorkbookPath,
            workbook.WorksheetName,
            row,
            SubmissionState.Succeeded,
            cancellationToken: cancellationToken);
        await PersistRowAsync(workbook, row, cancellationToken);
        await RecordSubmissionHistoryAsync(workbook, row, "提交成功", cancellationToken);
        EmitLog(row, $"提交成功，技术支持编号 {caseId}");
    }

    private async Task RecoverUncertainRowsAsync(
        WorkbookLoadResult workbook,
        CancellationToken cancellationToken)
    {
        foreach (var row in workbook.Rows.Where(row => string.IsNullOrWhiteSpace(row.TicketNumber)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = await _journal.GetAsync(
                workbook.WorkbookPath,
                workbook.WorksheetName,
                row,
                cancellationToken);
            if (entry is null ||
                entry.State is not (
                    SubmissionState.Submitting or
                    SubmissionState.PendingVerification or
                    SubmissionState.Succeeded))
            {
                continue;
            }

            EmitLog(row, $"正在核验上次运行保留的编号 {entry.CandidateCaseId}");
            var verification = await _platformClient.VerifyCreatedAsync(
                entry.CandidateCaseId,
                cancellationToken);
            if (verification.IsCreated)
            {
                row.TicketNumber = entry.CandidateCaseId;
                row.State = SubmissionState.Succeeded;
                row.SubmittedAt ??= entry.UpdatedAt.ToLocalTime();
                row.FailureReason = null;
                await _journal.MarkStateAsync(
                    workbook.WorkbookPath,
                    workbook.WorksheetName,
                    row,
                    SubmissionState.Succeeded,
                    cancellationToken: cancellationToken);
                await PersistRowAsync(workbook, row, cancellationToken);
                await RecordSubmissionHistoryAsync(workbook, row, "崩溃恢复核验成功", cancellationToken);
                EmitLog(row, $"已恢复成功编号 {entry.CandidateCaseId}");
            }
            else
            {
                row.State = SubmissionState.PendingVerification;
                row.FailureReason = verification.Message;
                await _journal.MarkStateAsync(
                    workbook.WorkbookPath,
                    workbook.WorksheetName,
                    row,
                    SubmissionState.PendingVerification,
                    verification.Message,
                    cancellationToken);
                await PersistRowAsync(workbook, row, cancellationToken);
                await RecordSubmissionHistoryAsync(workbook, row, verification.Message, cancellationToken);
                EmitLog(row, "旧编号仍未查询到创建记录，不会自动重新提交。", isError: true);
            }
        }
    }

    private async Task WaitForSubmissionIntervalAsync(CancellationToken cancellationToken)
    {
        var lastAttempt = await _journal.GetLastSaveAttemptAsync(cancellationToken);
        if (lastAttempt is null)
        {
            CountdownChanged?.Invoke(this, new CountdownChangedEventArgs(TimeSpan.Zero));
            return;
        }

        var requiredInterval = _submissionInterval.NextDelay();
        var remaining = requiredInterval - (_clock.UtcNow - lastAttempt.Value);
        while (remaining > TimeSpan.Zero)
        {
            CountdownChanged?.Invoke(this, new CountdownChangedEventArgs(remaining));
            var delay = remaining > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : remaining;
            await _clock.DelayAsync(delay, cancellationToken);
            remaining = requiredInterval - (_clock.UtcNow - lastAttempt.Value);
        }

        CountdownChanged?.Invoke(this, new CountdownChangedEventArgs(TimeSpan.Zero));
    }

    private Task PersistRowAsync(
        WorkbookLoadResult workbook,
        TicketRow row,
        CancellationToken cancellationToken) =>
        _workbookRepository.UpdateRowsAsync(
            workbook.WorkbookPath,
            workbook.WorksheetName,
            [row],
            cancellationToken);

    private Task RecordSubmissionHistoryAsync(
        WorkbookLoadResult workbook,
        TicketRow row,
        string? message,
        CancellationToken cancellationToken) =>
        _historyStore is null
            ? Task.CompletedTask
            : _historyStore.RecordSubmissionAsync(
                workbook.WorkbookPath,
                workbook.WorksheetName,
                row,
                message,
                cancellationToken);

    private void EmitLog(TicketRow row, string message, bool isError = false) =>
        LogEmitted?.Invoke(
            this,
            new SubmissionLogEventArgs(_clock.UtcNow, row.ExcelRowNumber, message, isError));

    private void EmitProgress(IReadOnlyList<TicketRow> rows, int? currentRow)
    {
        ProgressChanged?.Invoke(
            this,
            new SubmissionProgressEventArgs(
                rows.Count,
                rows.Count(row => row.State == SubmissionState.Succeeded),
                rows.Count(row => row.State == SubmissionState.Pending),
                rows.Count(row => row.State == SubmissionState.Failed),
                rows.Count(row => row.State == SubmissionState.ValidationFailed),
                rows.Count(row => row.State == SubmissionState.PendingVerification),
                currentRow));
    }

    private static string ToSafeError(Exception exception)
    {
        var message = exception.Message.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return message.Length <= 300 ? message : message[..300];
    }
}
