using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace TechSupportBatchSubmitter.Wpf.Services;

public sealed class VersionNoticeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<VersionNotice?> CheckAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var notice = await LoadRemoteNoticeAsync(settings.VersionNoticeUrl, cancellationToken) ??
            LoadLocalNotice(settings);
        if (notice is null || string.IsNullOrWhiteSpace(notice.LatestVersion))
        {
            return null;
        }

        var currentVersion = GetCurrentVersion();
        return IsNewer(notice.LatestVersion, currentVersion)
            ? notice with { CurrentVersion = currentVersion }
            : null;
    }

    private static VersionNotice? LoadLocalNotice(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LatestVersion))
        {
            return null;
        }

        return new VersionNotice(
            settings.LatestVersion.Trim(),
            settings.ReleaseNotes?.Trim(),
            CurrentVersion: null);
    }

    private static async Task<VersionNotice?> LoadRemoteNoticeAsync(
        string? url,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var json = await http.GetStringAsync(uri, cancellationToken);
            return JsonSerializer.Deserialize<VersionNotice>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion ??
            assembly.GetName().Version?.ToString() ??
            "0.0.0";
    }

    private static bool IsNewer(string candidate, string current)
    {
        return Version.TryParse(NormalizeVersion(candidate), out var candidateVersion) &&
            Version.TryParse(NormalizeVersion(current), out var currentVersion) &&
            candidateVersion > currentVersion;
    }

    private static string NormalizeVersion(string value)
    {
        var clean = value.Trim().TrimStart('v', 'V');
        var plusIndex = clean.IndexOf('+', StringComparison.Ordinal);
        return plusIndex >= 0 ? clean[..plusIndex] : clean;
    }
}

public sealed record VersionNotice(string LatestVersion, string? ReleaseNotes, string? CurrentVersion);
