using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Services;

public static class TicketInputValidator
{
    private const int MaxTitleLength = 120;
    private const int MaxDescriptionLength = 1800;

    public static string? Validate(TicketRow row)
    {
        var errors = new List<string>();
        var missing = new List<string>();
        AddIfMissing(missing, "序号", row.Sequence);
        AddIfMissing(missing, "标题", row.Title);
        AddIfMissing(missing, "发现人", row.Discoverer);
        AddIfMissing(missing, "申请人", row.Applicant);
        AddIfMissing(missing, "事件类型", row.EventType);
        AddIfMissing(missing, "所属系统", row.SystemName);
        AddIfMissing(missing, "指定受理人", row.Assignee);
        AddIfMissing(missing, "描述", row.Description);

        if (missing.Count > 0)
        {
            errors.Add($"必填字段为空：{string.Join("、", missing)}");
        }

        if (row.Title.Trim().Length > MaxTitleLength)
        {
            errors.Add($"标题过长：{row.Title.Trim().Length} 字，建议不超过 {MaxTitleLength} 字");
        }

        if (row.Description.Trim().Length > MaxDescriptionLength)
        {
            errors.Add($"描述过长：{row.Description.Trim().Length} 字，建议不超过 {MaxDescriptionLength} 字");
        }

        if (!string.IsNullOrWhiteSpace(row.TicketNumber) &&
            !IsValidTicketNumber(row.TicketNumber))
        {
            errors.Add("技术支持编号格式异常，应为 6-12 位数字");
        }

        return errors.Count == 0 ? null : string.Join("；", errors);
    }

    private static void AddIfMissing(ICollection<string> missing, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            missing.Add(name);
        }
    }

    private static bool IsValidTicketNumber(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length is >= 6 and <= 12 &&
            trimmed.All(char.IsDigit);
    }
}
