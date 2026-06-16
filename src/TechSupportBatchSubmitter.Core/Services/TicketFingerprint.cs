using System.Security.Cryptography;
using System.Text;
using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Services;

public static class TicketFingerprint
{
    public static string Compute(
        int excelRowNumber,
        string sequence,
        string title,
        string discoverer,
        string applicant,
        string eventType,
        string systemName,
        string assignee,
        string description,
        string originalDate)
    {
        var normalized = string.Join(
            "\u001f",
            excelRowNumber,
            Normalize(sequence),
            Normalize(title),
            Normalize(discoverer),
            Normalize(applicant),
            Normalize(eventType),
            Normalize(systemName),
            Normalize(assignee),
            Normalize(description),
            Normalize(originalDate));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public static string Compute(TicketRow row) => Compute(
        row.ExcelRowNumber,
        row.Sequence,
        row.Title,
        row.Discoverer,
        row.Applicant,
        row.EventType,
        row.SystemName,
        row.Assignee,
        row.Description,
        row.OriginalDate);

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
