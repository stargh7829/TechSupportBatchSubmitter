using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Wpf.Services;

public static class CloseReplyCatalog
{
    public static IReadOnlyList<CloseTicketCauseOption> CauseOptions { get; } =
    [
        Branch(
            "软件",
            "",
            Leaf("软件故障", path: "软件 / 软件故障"),
            Leaf("功能改进", path: "软件 / 功能改进"),
            Leaf("功能增加", path: "软件 / 功能增加"),
            Leaf("权限申请", path: "软件 / 权限申请"),
            Leaf("新项目开发", path: "软件 / 新项目开发"),
            Leaf("操作疑问", path: "软件 / 操作疑问")),
        Branch("硬件", ""),
        Branch("数据", ""),
        Branch("专线", ""),
        Leaf("其它", CloseTicketSettings.Default.CauseTypeValue, "其它"),
        Branch("社保JRA", "")
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
