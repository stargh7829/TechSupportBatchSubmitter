using TechSupportBatchSubmitter.Core.Services;

namespace TechSupportBatchSubmitter.Tests;

public sealed class SupportPlatformRoutesTests
{
    [Fact]
    public void BuildSolutionFormPath_IncludesRequiredFlag()
    {
        var path = SupportPlatformRoutes.BuildSolutionFormPath("20600577");

        Assert.Equal(
            "/xzsw/zcaseManager/toSolution.do?case_id=20600577&flag=11",
            path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("2060 0577")]
    public void BuildSolutionFormPath_RejectsInvalidCaseId(string caseId)
    {
        Assert.Throws<ArgumentException>(
            () => SupportPlatformRoutes.BuildSolutionFormPath(caseId));
    }
}
