namespace TechSupportBatchSubmitter.Core.Models;

public sealed record PlatformOption(string Id, string Text);

public sealed record ResolvedTicket(
    TicketRow Source,
    PlatformOption Discoverer,
    PlatformOption Applicant,
    PlatformOption EventType,
    PlatformOption System,
    PlatformOption Assignee);
