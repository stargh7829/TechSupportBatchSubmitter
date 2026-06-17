using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Interfaces;

public interface ITicketPlatformClient : IPlatformSession
{
    Task<ResolvedTicket> ResolveTicketAsync(TicketRow row, CancellationToken cancellationToken = default);
    Task<string> AllocateCaseIdAsync(CancellationToken cancellationToken = default);
    Task<SaveTicketResult> SaveTicketAsync(
        ResolvedTicket ticket,
        string caseId,
        CancellationToken cancellationToken = default);
    Task<VerificationResult> VerifyCreatedAsync(
        string caseId,
        TicketRow expected,
        CancellationToken cancellationToken = default);
    Task<PendingTicketQueryResult> QueryPendingAsync(
        string title,
        CancellationToken cancellationToken = default);
    Task<PendingTicketQueryResult> QueryPendingAsync(
        PendingTicketQueryCriteria criteria,
        CancellationToken cancellationToken = default);
    Task<CloseTicketResult> CloseTicketAsync(
        PendingTicketRow ticket,
        CloseTicketSettings settings,
        CancellationToken cancellationToken = default);
}
