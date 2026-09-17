using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TechSupportBatchSubmitter.Core.Models;

public sealed class TicketRow : INotifyPropertyChanged
{
    private string? _ticketNumber;
    private SubmissionState _state;
    private DateTimeOffset? _submittedAt;
    private string? _failureReason;
    private TicketAcceptanceState _acceptanceState;
    private DateTimeOffset? _acceptedAt;
    private string? _acceptanceFailureReason;
    private TicketCloseState _closeState;
    private DateTimeOffset? _closedAt;
    private string? _closeFailureReason;

    public required int ExcelRowNumber { get; init; }
    public required string Sequence { get; init; }
    public required string Title { get; init; }
    public required string Discoverer { get; init; }
    public required string Applicant { get; init; }
    public required string EventType { get; init; }
    /// <summary>平台处理类型：运营或运维。</summary>
    public string ProcessingType { get; init; } = "运营";
    /// <summary>提交后“受理并处理”环节写入平台的处理说明。</summary>
    public string ProcessingRemark { get; init; } = string.Empty;
    public required string SystemName { get; init; }
    public required string Assignee { get; init; }
    public required string Description { get; init; }
    public required string OriginalDate { get; init; }
    public required string Fingerprint { get; init; }

    public string? TicketNumber
    {
        get => _ticketNumber;
        set => SetField(ref _ticketNumber, value);
    }

    public SubmissionState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public string StateText => State.ToDisplayText();

    public DateTimeOffset? SubmittedAt
    {
        get => _submittedAt;
        set => SetField(ref _submittedAt, value);
    }

    public string? FailureReason
    {
        get => _failureReason;
        set => SetField(ref _failureReason, value);
    }

    public TicketAcceptanceState AcceptanceState
    {
        get => _acceptanceState;
        set
        {
            if (SetField(ref _acceptanceState, value))
            {
                OnPropertyChanged(nameof(AcceptanceStateText));
            }
        }
    }

    public string AcceptanceStateText => AcceptanceState.ToDisplayText();

    public DateTimeOffset? AcceptedAt
    {
        get => _acceptedAt;
        set => SetField(ref _acceptedAt, value);
    }

    public string? AcceptanceFailureReason
    {
        get => _acceptanceFailureReason;
        set => SetField(ref _acceptanceFailureReason, value);
    }

    public TicketCloseState CloseState
    {
        get => _closeState;
        set
        {
            if (SetField(ref _closeState, value))
            {
                OnPropertyChanged(nameof(CloseStateText));
            }
        }
    }

    public string CloseStateText => CloseState.ToDisplayText();

    public DateTimeOffset? ClosedAt
    {
        get => _closedAt;
        set => SetField(ref _closedAt, value);
    }

    public string? CloseFailureReason
    {
        get => _closeFailureReason;
        set => SetField(ref _closeFailureReason, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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
