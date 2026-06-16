using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Interfaces;

public interface IPlatformSession
{
    event EventHandler? SessionExpired;

    Task<PlatformSessionStatus> CheckSessionAsync(CancellationToken cancellationToken = default);
}
