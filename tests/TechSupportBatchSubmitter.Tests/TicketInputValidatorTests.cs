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
            ProcessingType = row.ProcessingType,
            ProcessingRemark = row.ProcessingRemark,
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

    [Fact]
    public void Validate_WhenTitleIsTooLong_ReturnsLengthError()
    {
        var row = CreateRow();
        row = Clone(row, title: new string('测', 121));

        var error = TicketInputValidator.Validate(row);

        Assert.Contains("标题过长", error);
    }

    [Fact]
    public void Validate_WhenTicketNumberIsInvalid_ReturnsTicketNumberError()
    {
        var row = CreateRow();
        row.TicketNumber = "ABC-1";

        var error = TicketInputValidator.Validate(row);

        Assert.Contains("技术支持编号格式异常", error);
    }

    [Fact]
    public void Validate_WhenProcessingTypeIsInvalid_ReturnsProcessingTypeError()
    {
        var row = CreateRow();
        row = Clone(row);
        row = new TicketRow
        {
            ExcelRowNumber = row.ExcelRowNumber,
            Sequence = row.Sequence,
            Title = row.Title,
            Discoverer = row.Discoverer,
            Applicant = row.Applicant,
            EventType = row.EventType,
            ProcessingType = "其他",
            ProcessingRemark = row.ProcessingRemark,
            SystemName = row.SystemName,
            Assignee = row.Assignee,
            Description = row.Description,
            OriginalDate = row.OriginalDate,
            Fingerprint = row.Fingerprint,
            State = row.State
        };

        var error = TicketInputValidator.Validate(row);

        Assert.Contains("处理类型无效", error);
    }

    private static TicketRow CreateRow() => new()
    {
        ExcelRowNumber = 2,
        Sequence = "1",
        Title = "测试标题",
        Discoverer = "发现人",
        Applicant = "申请人",
        EventType = "数据疑问",
        ProcessingType = "运营",
        ProcessingRemark = "已处理",
        SystemName = "网签合同",
        Assignee = "受理人",
        Description = "测试描述",
        OriginalDate = "2026/6/15",
        Fingerprint = "fingerprint",
        State = SubmissionState.Pending
    };

    private static TicketRow Clone(TicketRow row, string? title = null) => new()
    {
        ExcelRowNumber = row.ExcelRowNumber,
        Sequence = row.Sequence,
        Title = title ?? row.Title,
        Discoverer = row.Discoverer,
        Applicant = row.Applicant,
        EventType = row.EventType,
        ProcessingType = row.ProcessingType,
        ProcessingRemark = row.ProcessingRemark,
        SystemName = row.SystemName,
        Assignee = row.Assignee,
        Description = row.Description,
        OriginalDate = row.OriginalDate,
        Fingerprint = row.Fingerprint,
        State = row.State
    };
}
