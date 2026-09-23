namespace TechSupportBatchSubmitter.Core.Services;

public static class InternalPlatformEndpointPolicy
{
    public static bool IsCertificateBypassAllowed(string? requestUri)
    {
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return
            (string.Equals(uri.Host, "172.18.75.21", StringComparison.OrdinalIgnoreCase) &&
             uri.Port == 443) ||
            (string.Equals(uri.Host, "172.18.75.6", StringComparison.OrdinalIgnoreCase) &&
             uri.Port == 18005) ||
            (string.Equals(uri.Host, "172.18.75.21", StringComparison.OrdinalIgnoreCase) &&
             uri.Port == 18088);
    }
}
