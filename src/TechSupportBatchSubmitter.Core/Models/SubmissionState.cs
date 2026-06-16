namespace TechSupportBatchSubmitter.Core.Models;

public enum SubmissionState
{
    Pending,
    ValidationFailed,
    Submitting,
    PendingVerification,
    Succeeded,
    Failed
}

public static class SubmissionStateExtensions
{
    public static string ToDisplayText(this SubmissionState state) => state switch
    {
        SubmissionState.Pending => "待提交",
        SubmissionState.ValidationFailed => "校验失败",
        SubmissionState.Submitting => "提交中",
        SubmissionState.PendingVerification => "待核验",
        SubmissionState.Succeeded => "成功",
        SubmissionState.Failed => "失败",
        _ => "待提交"
    };

    public static SubmissionState FromDisplayText(string? value) => value?.Trim() switch
    {
        "校验失败" => SubmissionState.ValidationFailed,
        "提交中" => SubmissionState.Submitting,
        "待核验" => SubmissionState.PendingVerification,
        "成功" => SubmissionState.Succeeded,
        "失败" => SubmissionState.Failed,
        _ => SubmissionState.Pending
    };
}
