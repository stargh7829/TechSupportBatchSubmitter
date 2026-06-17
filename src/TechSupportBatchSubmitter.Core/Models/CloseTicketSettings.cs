namespace TechSupportBatchSubmitter.Core.Models;

public sealed record CloseTicketCauseOption(
    string Value,
    string Name,
    string Path,
    IReadOnlyList<CloseTicketCauseOption>? Children = null);

public sealed record CloseTicketSettings(
    string CauseTypeValue,
    string CauseTypeName,
    string CauseTypePath,
    string CauseDescription,
    string SolveTypeValue,
    string SolveTypeName,
    string SolutionDescription)
{
    public static CloseTicketSettings Default { get; } = new(
        "21",
        "数据疑问",
        "数据 / 数据疑问",
        "已处理",
        "2",
        "修改数据",
        "已处理");

    public string ToConfirmationText() =>
        $"原因分类：{CauseTypePath}\n" +
        $"原因描述：{CauseDescription}\n" +
        $"解决方法：{SolveTypeName}\n" +
        $"解决方案：{SolutionDescription}";
}
