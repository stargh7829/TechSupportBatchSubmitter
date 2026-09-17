namespace TechSupportBatchSubmitter.Core.Services;

public static class SupportPlatformRoutes
{
    public static string BuildSolutionFormPath(string caseId)
    {
        if (string.IsNullOrWhiteSpace(caseId) || !caseId.All(char.IsDigit))
        {
            throw new ArgumentException("技术支持编号必须为数字。", nameof(caseId));
        }

        return $"/xzsw/zcaseManager/toSolution.do?case_id={caseId}&flag=11";
    }

    public static string BuildAcceptAndHandleFormPath(string caseId)
    {
        if (string.IsNullOrWhiteSpace(caseId) || !caseId.All(char.IsDigit))
        {
            throw new ArgumentException("技术支持编号必须为数字。", nameof(caseId));
        }

        return $"/xzsw/zcaseManager/toAcceptAndHandle.do?case_id={caseId}&flag=1";
    }
}
