namespace TechSupportBatchSubmitter.Core.Models;

public enum SubmissionState
{
    Pending,
    ValidationFailed,
    Submitting,
    PendingVerification,
    Succeeded,
    Failed,
    // Append new values so existing SQLite journal records retain their meaning.
    PendingAcceptance,
    PendingAcceptanceVerification
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
        SubmissionState.PendingAcceptance => "待受理并处理",
        SubmissionState.PendingAcceptanceVerification => "受理并处理待核验",
        _ => "待提交"
    };

    public static SubmissionState FromDisplayText(string? value) => value?.Trim() switch
    {
        "校验失败" => SubmissionState.ValidationFailed,
        "提交中" => SubmissionState.Submitting,
        "待核验" => SubmissionState.PendingVerification,
        "成功" => SubmissionState.Succeeded,
        "失败" => SubmissionState.Failed,
        "待受理并处理" => SubmissionState.PendingAcceptance,
        "受理并处理待核验" => SubmissionState.PendingAcceptanceVerification,
        _ => SubmissionState.Pending
    };
}
