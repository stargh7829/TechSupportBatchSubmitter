using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Wpf.Services;

public static class CloseReplyCatalog
{
    // Platform source: /xzsw/zcaseManager/listType.do, verified on 2026-06-17.
    // The solution form posts cause_type as the numeric id; text is display only.
    public static IReadOnlyList<CloseTicketCauseOption> CauseOptions { get; } =
    [
        Branch(
            "软件",
            "1",
            Leaf("软件故障", "6", "软件 / 软件故障"),
            Leaf("功能改进", "7", "软件 / 功能改进"),
            Leaf("功能增加", "8", "软件 / 功能增加"),
            Leaf("权限申请", "9", "软件 / 权限申请"),
            Leaf("新项目开发", "10", "软件 / 新项目开发"),
            Leaf("操作疑问", "11", "软件 / 操作疑问")),
        Branch(
            "硬件",
            "2",
            Leaf("PC机", "12", "硬件 / PC机"),
            Leaf("网络设备", "13", "硬件 / 网络设备"),
            Leaf("其它设备", "14", "硬件 / 其它设备"),
            Leaf("操作疑问", "15", "硬件 / 操作疑问")),
        Branch(
            "数据",
            "3",
            Leaf("历史迁移数据修改", "16", "数据 / 历史迁移数据修改"),
            Leaf("软件故障数据修改", "17", "数据 / 软件故障数据修改"),
            Leaf("操作失误数据修改", "18", "数据 / 操作失误数据修改"),
            Leaf("数据统计", "19", "数据 / 数据统计"),
            Leaf("批量处理", "20", "数据 / 批量处理"),
            Leaf("数据疑问", "21", "数据 / 数据疑问")),
        Branch(
            "专线",
            "4",
            Leaf("专线申请", "22", "专线 / 专线申请"),
            Leaf("专线移机", "23", "专线 / 专线移机"),
            Leaf("专线故障", "24", "专线 / 专线故障"),
            Leaf("12345急办件", "25", "专线 / 12345急办件")),
        Leaf("其它", "5", "其它"),
        Branch(
            "社保JIRA",
            "26",
            Leaf("JIRA故障申报", "27", "社保JIRA / JIRA故障申报"),
            Leaf("JIRA业务需求", "28", "社保JIRA / JIRA业务需求"),
            Leaf("JIRA数据维护", "29", "社保JIRA / JIRA数据维护"))
    ];

    public static IReadOnlyList<KeyValuePair<string, string>> SolveTypes { get; } =
    [
        new("1", "修改软件"),
        new("2", "修改数据"),
        new("3", "不能解决"),
        new("4", "其他方式")
    ];

    public static CloseTicketCauseOption DefaultCause =>
        CauseOptions.SelectMany(Flatten).First(option => option.Value == CloseTicketSettings.Default.CauseTypeValue);

    public static IEnumerable<CloseTicketCauseOption> Flatten(CloseTicketCauseOption option)
    {
        yield return option;
        if (option.Children is null)
        {
            yield break;
        }

        foreach (var child in option.Children.SelectMany(Flatten))
        {
            yield return child;
        }
    }

    public static IEnumerable<CloseTicketCauseOption> FlattenAll() => CauseOptions.SelectMany(Flatten);

    private static CloseTicketCauseOption Branch(
        string name,
        string value,
        params CloseTicketCauseOption[] children) =>
        new(value, name, name, children);

    private static CloseTicketCauseOption Leaf(string name, string value = "", string? path = null) =>
        new(value, name, path ?? name);
}
