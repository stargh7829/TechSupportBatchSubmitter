namespace TechSupportBatchSubmitter.Core.Models;

public enum SubmissionRunMode
{
    Pending,
    FailedOnly
}

public sealed class SubmissionLogEventArgs : EventArgs
{
    public SubmissionLogEventArgs(
        DateTimeOffset timestamp,
        int? excelRowNumber,
        string message,
        bool isError = false)
    {
        Timestamp = timestamp;
        ExcelRowNumber = excelRowNumber;
        Message = message;
        IsError = isError;
    }

    public DateTimeOffset Timestamp { get; }
    public int? ExcelRowNumber { get; }
    public string Message { get; }
    public bool IsError { get; }
}

public sealed class SubmissionProgressEventArgs : EventArgs
{
    public SubmissionProgressEventArgs(
        int total,
        int succeeded,
        int pending,
        int failed,
        int validationFailed,
        int pendingVerification,
        int? currentExcelRowNumber)
    {
        Total = total;
        Succeeded = succeeded;
        Pending = pending;
        Failed = failed;
        ValidationFailed = validationFailed;
        PendingVerification = pendingVerification;
        CurrentExcelRowNumber = currentExcelRowNumber;
    }

    public int Total { get; }
    public int Succeeded { get; }
    public int Pending { get; }
    public int Failed { get; }
    public int ValidationFailed { get; }
    public int PendingVerification { get; }
    public int? CurrentExcelRowNumber { get; }
}

public sealed class CountdownChangedEventArgs : EventArgs
{
    public CountdownChangedEventArgs(TimeSpan remaining)
    {
        Remaining = remaining;
    }

    public TimeSpan Remaining { get; }
}
