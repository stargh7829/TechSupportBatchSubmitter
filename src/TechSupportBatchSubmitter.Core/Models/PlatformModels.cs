using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TechSupportBatchSubmitter.Core.Models;

public sealed record PlatformSessionStatus(
    bool IsSupportPlatformReady,
    bool IsAuthenticated,
    string DisplayName,
    string Message);

public sealed record SaveTicketResult(bool RequestAccepted, string? ResponseText);

public sealed record VerificationResult(bool IsCreated, string Message);

public enum TicketCloseState
{
    Ready,
    Processing,
    Succeeded,
    Failed,
    PendingVerification
}

public static class TicketCloseStateExtensions
{
    public static string ToDisplayText(this TicketCloseState state) => state switch
    {
        TicketCloseState.Ready => "待关闭",
        TicketCloseState.Processing => "处理中",
        TicketCloseState.Succeeded => "成功",
        TicketCloseState.Failed => "失败",
        TicketCloseState.PendingVerification => "待核验",
        _ => string.Empty
    };

    public static TicketCloseState? FromDisplayText(string? text)
    {
        return text?.Trim() switch
        {
            "待关闭" => TicketCloseState.Ready,
            "处理中" => TicketCloseState.Processing,
            "成功" => TicketCloseState.Succeeded,
            "失败" => TicketCloseState.Failed,
            "待核验" => TicketCloseState.PendingVerification,
            _ => null
        };
    }
}

public sealed class PendingTicketRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private TicketCloseState _closeState;
    private string _closeMessage = string.Empty;
    private DateTimeOffset? _closedAt;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string CaseId { get; init; } = string.Empty;
    public string CaseTitle { get; init; } = string.Empty;
    public string TypeName { get; init; } = string.Empty;
    public string SystemName { get; init; } = string.Empty;
    public string Assignee { get; init; } = string.Empty;
    public string CurrentHandler { get; init; } = string.Empty;
    public string Applicant { get; init; } = string.Empty;
    public string CreateTime { get; init; } = string.Empty;
    public string LatestReplyTime { get; init; } = string.Empty;
    public string StatusName { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public string? WorkbookPath { get; init; }
    public string? WorksheetName { get; init; }
    public int? ExcelRowNumber { get; init; }
    public string? Fingerprint { get; init; }

    public TicketCloseState CloseState
    {
        get => _closeState;
        set
        {
            if (!SetField(ref _closeState, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CloseStateText));
            OnPropertyChanged(nameof(CanClose));
        }
    }

    public string CloseMessage
    {
        get => _closeMessage;
        set => SetField(ref _closeMessage, value);
    }

    public DateTimeOffset? ClosedAt
    {
        get => _closedAt;
        set => SetField(ref _closedAt, value);
    }

    public string CloseStateText => CloseState.ToDisplayText();

    public bool CanClose =>
        !string.IsNullOrWhiteSpace(CaseId) &&
        CloseState is TicketCloseState.Ready or TicketCloseState.Failed;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class PendingTicketQueryResult
{
    public int Total { get; init; }
    public List<PendingTicketRow> Items { get; init; } = [];
}

public sealed record PendingTicketQueryCriteria(
    string Title,
    string CaseId = "",
    string TypeId = "",
    string SystemId = "",
    string StatusId = "",
    string AssigneeName = "",
    string ApplicantName = "",
    string CreatorOrg = "",
    string CurrentHandlerName = "",
    string StartDate = "",
    string EndDate = "",
    string CloseStartDate = "",
    string CloseEndDate = "",
    string SortField = "",
    string HaveJr = "",
    string JiraId = "",
    string JiraStatus = "",
    string Description = "")
{
    public static PendingTicketQueryCriteria FromTitle(string title) => new(title.Trim());
}

public sealed record TicketValidationResult(
    TicketRow Row,
    ResolvedTicket? ResolvedTicket,
    string? Error)
{
    public bool IsValid => ResolvedTicket is not null && string.IsNullOrWhiteSpace(Error);
}

public sealed record CloseTicketResult(bool IsClosed, string Message);

public sealed class TicketCloseProgressEventArgs : EventArgs
{
    public TicketCloseProgressEventArgs(
        int total,
        int succeeded,
        int failed,
        int pendingVerification,
        int remaining,
        string? currentCaseId)
    {
        Total = total;
        Succeeded = succeeded;
        Failed = failed;
        PendingVerification = pendingVerification;
        Remaining = remaining;
        CurrentCaseId = currentCaseId;
    }

    public int Total { get; }
    public int Succeeded { get; }
    public int Failed { get; }
    public int PendingVerification { get; }
    public int Remaining { get; }
    public string? CurrentCaseId { get; }
}

public sealed class TicketCloseLogEventArgs : EventArgs
{
    public TicketCloseLogEventArgs(
        DateTimeOffset timestamp,
        string caseId,
        string title,
        string message,
        bool isError)
    {
        Timestamp = timestamp;
        CaseId = caseId;
        Title = title;
        Message = message;
        IsError = isError;
    }

    public DateTimeOffset Timestamp { get; }
    public string CaseId { get; }
    public string Title { get; }
    public string Message { get; }
    public bool IsError { get; }
}
