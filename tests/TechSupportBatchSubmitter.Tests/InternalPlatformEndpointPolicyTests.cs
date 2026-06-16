using TechSupportBatchSubmitter.Core.Services;

namespace TechSupportBatchSubmitter.Tests;

public sealed class InternalPlatformEndpointPolicyTests
{
    [Theory]
    [InlineData("https://172.18.75.21/index.action")]
    [InlineData("https://172.18.75.6:18005/xzsw/pages/mini.jsp")]
    public void IsCertificateBypassAllowed_ApprovedEndpoint_ReturnsTrue(string uri)
    {
        Assert.True(InternalPlatformEndpointPolicy.IsCertificateBypassAllowed(uri));
    }

    [Theory]
    [InlineData("http://172.18.75.21/index.action")]
    [InlineData("https://172.18.75.6/xzsw/pages/mini.jsp")]
    [InlineData("https://172.18.75.18/")]
    [InlineData("https://example.com/")]
    [InlineData("")]
    public void IsCertificateBypassAllowed_OtherEndpoint_ReturnsFalse(string uri)
    {
        Assert.False(InternalPlatformEndpointPolicy.IsCertificateBypassAllowed(uri));
    }
}
