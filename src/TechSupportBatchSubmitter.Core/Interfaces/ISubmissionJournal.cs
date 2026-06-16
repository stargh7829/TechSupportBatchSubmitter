using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Interfaces;

public interface ISubmissionJournal
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertCandidateAsync(
        string workbookPath,
        string worksheetName,
        TicketRow row,
        string candidateCaseId,
        SubmissionState state,
        string? error = null,
        CancellationToken cancellationToken = default);
    Task<JournalEntry?> GetAsync(
        string workbookPath,
        string worksheetName,
        TicketRow row,
        CancellationToken cancellationToken = default);
    Task MarkStateAsync(
        string workbookPath,
        string worksheetName,
        TicketRow row,
        SubmissionState state,
        string? error = null,
        CancellationToken cancellationToken = default);
    Task<DateTimeOffset?> GetLastSaveAttemptAsync(CancellationToken cancellationToken = default);
    Task RecordSaveAttemptAsync(
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default);
}
