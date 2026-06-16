using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Interfaces;

public interface IWorkbookRepository
{
    Task<WorkbookLoadResult> PrepareAndLoadAsync(
        string workbookPath,
        CancellationToken cancellationToken = default);

    Task UpdateRowsAsync(
        string workbookPath,
        string worksheetName,
        IReadOnlyCollection<TicketRow> rows,
        CancellationToken cancellationToken = default);
}
