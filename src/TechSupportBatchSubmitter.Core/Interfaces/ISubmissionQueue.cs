using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Interfaces;

public interface ISubmissionQueue
{
    event EventHandler<SubmissionLogEventArgs>? LogEmitted;
    event EventHandler<SubmissionProgressEventArgs>? ProgressChanged;
    event EventHandler<CountdownChangedEventArgs>? CountdownChanged;

    DelaySchedule SubmissionInterval { get; set; }

    Task<IReadOnlyDictionary<string, ResolvedTicket>> ValidateAsync(
        WorkbookLoadResult workbook,
        CancellationToken cancellationToken = default);

    Task RunAsync(
        WorkbookLoadResult workbook,
        IReadOnlyDictionary<string, ResolvedTicket> resolvedTickets,
        SubmissionRunMode mode,
        CancellationToken cancellationToken = default);
}
