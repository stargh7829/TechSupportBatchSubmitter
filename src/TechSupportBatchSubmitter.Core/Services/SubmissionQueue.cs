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
        var session = await _platformClient.CheckSessionAsync(cancellationToken);
        if (!session.IsSupportPlatformReady || !session.IsAuthenticated)
        {
            throw new PlatformSessionExpiredException("技术支持系统登录已失效，请重新登录后再校验清单。");
        }

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
                    if (!IsCurrentUserAssignee(session.DisplayName, ticket.Assignee.Text))
                    {
                        row.State = SubmissionState.ValidationFailed;
                        row.FailureReason =
                            $"指定受理人“{ticket.Assignee.Text}”与当前登录人“{session.DisplayName}”不一致，无法自动受理并处理";
                        changed.Add(row);
                        EmitLog(row, row.FailureReason, isError: true);
                        EmitProgress(workbook.Rows, row.ExcelRowNumber);
                        continue;
                    }

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
        await ResumeAcceptanceRowsAsync(workbook, cancellationToken);

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
                var isAcceptanceStep = !string.IsNullOrWhiteSpace(row.TicketNumber);
                row.State = isAcceptanceStep
                    ? SubmissionState.PendingAcceptanceVerification
                    : SubmissionState.PendingVerification;
                if (isAcceptanceStep)
                {
                    row.AcceptanceState = TicketAcceptanceState.PendingVerification;
                    row.AcceptanceFailureReason = ex.Message;
                }
                else
                {
                    row.FailureReason = ex.Message;
                }
                await _journal.MarkStateAsync(
                    workbook.WorkbookPath,
                    workbook.WorksheetName,
                    row,
                    row.State,
                    ex.Message,
                    cancellationToken);
                await PersistRowAsync(workbook, row, cancellationToken);
                await RecordSubmissionHistoryAsync(workbook, row, ex.Message, cancellationToken);
                EmitLog(
                    row,
                    isAcceptanceStep
                        ? "受理并处理结果不确定，已暂停且不会自动重复受理。"
                        : "保存结果不确定，已暂停且不会自动换号重提。",
                    isError: true);
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
        row.State = SubmissionState.PendingAcceptance;
        row.SubmittedAt = _clock.UtcNow.ToLocalTime();
        row.FailureReason = null;
        row.AcceptanceState = TicketAcceptanceState.Processing;
        row.AcceptedAt = null;
        row.AcceptanceFailureReason = null;
        await _journal.MarkStateAsync(
            workbook.WorkbookPath,
            workbook.WorksheetName,
            row,
            SubmissionState.PendingAcceptance,
            cancellationToken: cancellationToken);
        await PersistRowAsync(workbook, row, cancellationToken);
        await RecordSubmissionHistoryAsync(workbook, row, "事件申请已创建，待受理并处理", cancellationToken);
        EmitLog(row, $"事件申请已创建，技术支持编号 {caseId}，正在受理并处理");

        await ProcessAcceptanceAsync(workbook, row, cancellationToken);
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
                    SubmissionState.Succeeded or
                    SubmissionState.PendingAcceptance or
                    SubmissionState.PendingAcceptanceVerification))
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
                row.SubmittedAt ??= entry.UpdatedAt.ToLocalTime();
                row.FailureReason = null;
                if (entry.State is SubmissionState.PendingAcceptance or
                    SubmissionState.PendingAcceptanceVerification)
                {
                    row.State = entry.State;
                    row.AcceptanceState = entry.State == SubmissionState.PendingAcceptanceVerification
                        ? TicketAcceptanceState.PendingVerification
                        : TicketAcceptanceState.Processing;
                    row.AcceptanceFailureReason = entry.LastError;
                }
                else
                {
                    // Journal records from versions before automatic acceptance retain their original meaning.
                    row.State = SubmissionState.Succeeded;
                    row.AcceptanceState = TicketAcceptanceState.NotStarted;
                    row.AcceptanceFailureReason = null;
                }
                await _journal.MarkStateAsync(
                    workbook.WorkbookPath,
                    workbook.WorksheetName,
                    row,
                    row.State,
                    cancellationToken: cancellationToken);
                await PersistRowAsync(workbook, row, cancellationToken);
                await RecordSubmissionHistoryAsync(
                    workbook,
                    row,
                    row.State == SubmissionState.Succeeded
                        ? "崩溃恢复核验成功"
                        : "已恢复已创建办件，待受理并处理",
                    cancellationToken);
                EmitLog(
                    row,
                    row.State == SubmissionState.Succeeded
                        ? $"已恢复成功编号 {entry.CandidateCaseId}"
                        : $"已恢复技术支持编号 {entry.CandidateCaseId}，将继续受理并处理");
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

    private async Task ResumeAcceptanceRowsAsync(
        WorkbookLoadResult workbook,
        CancellationToken cancellationToken)
    {
        var candidates = workbook.Rows
            .Where(row => !string.IsNullOrWhiteSpace(row.TicketNumber))
            .Where(row => row.AcceptanceState is
                TicketAcceptanceState.Processing or
                TicketAcceptanceState.Failed or
                TicketAcceptanceState.PendingVerification)
            .ToList();

        foreach (var row in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EmitProgress(workbook.Rows, row.ExcelRowNumber);

            if (row.AcceptanceState == TicketAcceptanceState.PendingVerification)
            {
                var verification = await _platformClient.VerifyAcceptedAndHandledAsync(
                    row.TicketNumber!,
                    cancellationToken);
                if (verification.IsAccepted)
                {
                    await MarkAcceptanceSucceededAsync(workbook, row, verification.Message, cancellationToken);
                    continue;
                }

                row.State = SubmissionState.PendingAcceptanceVerification;
                row.AcceptanceFailureReason = verification.Message;
                await _journal.MarkStateAsync(
                    workbook.WorkbookPath,
                    workbook.WorksheetName,
                    row,
                    SubmissionState.PendingAcceptanceVerification,
                    verification.Message,
                    cancellationToken);
                await PersistRowAsync(workbook, row, cancellationToken);
                EmitLog(row, "受理并处理结果仍无法确认，未自动重复提交。", isError: true);
                throw new SubmissionOutcomeUnknownException(verification.Message);
            }

            if (row.AcceptanceState == TicketAcceptanceState.Processing)
            {
                var verification = await _platformClient.VerifyAcceptedAndHandledAsync(
                    row.TicketNumber!,
                    cancellationToken);
                if (verification.IsAccepted)
                {
                    await MarkAcceptanceSucceededAsync(workbook, row, verification.Message, cancellationToken);
                    continue;
                }
            }

            await ProcessAcceptanceAsync(workbook, row, cancellationToken);
        }
    }

    private async Task ProcessAcceptanceAsync(
        WorkbookLoadResult workbook,
        TicketRow row,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.TicketNumber))
        {
            throw new InvalidOperationException("事件申请尚未生成技术支持编号，无法执行受理并处理。");
        }

        row.State = SubmissionState.PendingAcceptance;
        row.AcceptanceState = TicketAcceptanceState.Processing;
        row.AcceptedAt = null;
        row.AcceptanceFailureReason = null;
        await _journal.MarkStateAsync(
            workbook.WorkbookPath,
            workbook.WorksheetName,
            row,
            SubmissionState.PendingAcceptance,
            cancellationToken: cancellationToken);
        await PersistRowAsync(workbook, row, cancellationToken);
        EmitLog(row, $"正在受理并处理技术支持编号 {row.TicketNumber}");

        var result = await _platformClient.AcceptAndHandleAsync(
            row.TicketNumber,
            row.ProcessingRemark,
            cancellationToken);
        if (result.IsAccepted)
        {
            await MarkAcceptanceSucceededAsync(workbook, row, result.Message, cancellationToken);
            return;
        }

        row.State = SubmissionState.PendingAcceptance;
        row.AcceptanceState = TicketAcceptanceState.Failed;
        row.AcceptanceFailureReason = result.Message;
        await _journal.MarkStateAsync(
            workbook.WorkbookPath,
            workbook.WorksheetName,
            row,
            SubmissionState.PendingAcceptance,
            result.Message,
            cancellationToken);
        await PersistRowAsync(workbook, row, cancellationToken);
        await RecordSubmissionHistoryAsync(workbook, row, result.Message, cancellationToken);
        EmitLog(row, $"受理并处理失败，保留编号可在下次运行时补做：{result.Message}", isError: true);
    }

    private async Task MarkAcceptanceSucceededAsync(
        WorkbookLoadResult workbook,
        TicketRow row,
        string message,
        CancellationToken cancellationToken)
    {
        row.State = SubmissionState.Succeeded;
        row.AcceptanceState = TicketAcceptanceState.Succeeded;
        row.AcceptedAt = _clock.UtcNow.ToLocalTime();
        row.FailureReason = null;
        row.AcceptanceFailureReason = null;
        await _journal.MarkStateAsync(
            workbook.WorkbookPath,
            workbook.WorksheetName,
            row,
            SubmissionState.Succeeded,
            cancellationToken: cancellationToken);
        await PersistRowAsync(workbook, row, cancellationToken);
        await RecordSubmissionHistoryAsync(workbook, row, "提交并完成受理并处理", cancellationToken);
        EmitLog(row, $"提交及受理并处理完成，技术支持编号 {row.TicketNumber}（{message}）");
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
                rows.Count(row => row.State is
                    SubmissionState.PendingVerification or
                    SubmissionState.PendingAcceptanceVerification),
                currentRow));
    }

    private static string ToSafeError(Exception exception)
    {
        var message = exception.Message.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return message.Length <= 300 ? message : message[..300];
    }

    private static bool IsCurrentUserAssignee(string displayName, string assignee)
    {
        var current = GetPersonName(displayName);
        var target = GetPersonName(assignee);
        return !string.IsNullOrWhiteSpace(current) &&
            string.Equals(current, target, StringComparison.Ordinal);
    }

    private static string GetPersonName(string value)
    {
        return (value ?? string.Empty)
            .Trim()
            .Split(['-', '－'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
    }
}
