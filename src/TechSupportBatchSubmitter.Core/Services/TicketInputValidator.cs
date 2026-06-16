using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Services;

public static class TicketInputValidator
{
    public static string? Validate(TicketRow row)
    {
        var missing = new List<string>();
        AddIfMissing(missing, "序号", row.Sequence);
        AddIfMissing(missing, "标题", row.Title);
        AddIfMissing(missing, "发现人", row.Discoverer);
        AddIfMissing(missing, "申请人", row.Applicant);
        AddIfMissing(missing, "事件类型", row.EventType);
        AddIfMissing(missing, "所属系统", row.SystemName);
        AddIfMissing(missing, "指定受理人", row.Assignee);
        AddIfMissing(missing, "描述", row.Description);

        return missing.Count == 0 ? null : $"必填字段为空：{string.Join("、", missing)}";
    }

    private static void AddIfMissing(ICollection<string> missing, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            missing.Add(name);
        }
    }
}
