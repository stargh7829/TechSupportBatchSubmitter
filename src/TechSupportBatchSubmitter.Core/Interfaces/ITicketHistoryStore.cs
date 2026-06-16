using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Interfaces;

public interface ITicketHistoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task RecordSubmissionAsync(
        string workbookPath,
        string worksheetName,
        TicketRow row,
        string? message = null,
        CancellationToken cancellationToken = default);

    Task RecordCloseAsync(
        PendingTicketRow ticket,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SubmissionHistoryRecord>> GetSubmissionHistoryAsync(
        int limit = 500,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloseHistoryRecord>> GetCloseHistoryAsync(
        int limit = 500,
        CancellationToken cancellationToken = default);
}
