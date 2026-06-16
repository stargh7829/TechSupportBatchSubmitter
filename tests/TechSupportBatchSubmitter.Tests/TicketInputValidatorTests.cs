using TechSupportBatchSubmitter.Core.Models;
using TechSupportBatchSubmitter.Core.Services;

namespace TechSupportBatchSubmitter.Tests;

public sealed class TicketInputValidatorTests
{
    [Fact]
    public void Validate_WhenRequiredFieldsExist_ReturnsNull()
    {
        Assert.Null(TicketInputValidator.Validate(CreateRow()));
    }

    [Fact]
    public void Validate_WhenFieldsAreMissing_ReturnsAllMissingNames()
    {
        var row = CreateRow();
        row = new TicketRow
        {
            ExcelRowNumber = row.ExcelRowNumber,
            Sequence = row.Sequence,
            Title = string.Empty,
            Discoverer = row.Discoverer,
            Applicant = row.Applicant,
            EventType = row.EventType,
            SystemName = row.SystemName,
            Assignee = string.Empty,
            Description = row.Description,
            OriginalDate = row.OriginalDate,
            Fingerprint = row.Fingerprint,
            State = SubmissionState.Pending
        };

        var error = TicketInputValidator.Validate(row);

        Assert.Contains("标题", error);
        Assert.Contains("指定受理人", error);
    }

    private static TicketRow CreateRow() => new()
    {
        ExcelRowNumber = 2,
        Sequence = "1",
        Title = "测试标题",
        Discoverer = "发现人",
        Applicant = "申请人",
        EventType = "数据疑问",
        SystemName = "网签合同",
        Assignee = "受理人",
        Description = "测试描述",
        OriginalDate = "2026/6/15",
        Fingerprint = "fingerprint",
        State = SubmissionState.Pending
    };
}
