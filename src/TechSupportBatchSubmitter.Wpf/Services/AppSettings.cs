using System.IO;
using System.Text.Json;
using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Wpf.Services;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string WorkbenchUrl { get; set; } = "https://172.18.75.21/index.action";
    public string SupportPlatformUrl { get; set; } = "https://172.18.75.21:18088/xzsw/pages/mini.jsp";
    public int DefaultSubmissionDelayMinSeconds { get; set; } = 30;
    public int DefaultSubmissionDelayMaxSeconds { get; set; } = 90;
    public int DefaultCloseDelayMinSeconds { get; set; } = 5;
    public int DefaultCloseDelayMaxSeconds { get; set; } = 10;
    public string? VersionNoticeUrl { get; set; }
    public string? LatestVersion { get; set; }
    public string? ReleaseNotes { get; set; }

    public static AppSettings LoadOrCreate(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            var created = new AppSettings();
            created.Save(path);
            return created;
        }

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Normalize();
            settings.Save(path);
            return settings;
        }
        catch
        {
            var fallback = new AppSettings();
            fallback.Save(path);
            return fallback;
        }
    }

    public void Save(string path)
    {
        Normalize();
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public Uri WorkbenchUri =>
        Uri.TryCreate(WorkbenchUrl, UriKind.Absolute, out var uri)
            ? uri
            : new Uri("https://172.18.75.21/index.action");

    public Uri SupportPlatformUri =>
        Uri.TryCreate(SupportPlatformUrl, UriKind.Absolute, out var uri)
            ? uri
            : new Uri("https://172.18.75.21:18088/xzsw/pages/mini.jsp");

    public DelaySchedule CreateSubmissionDelaySchedule() =>
        CreateDelaySchedule(DefaultSubmissionDelayMinSeconds, DefaultSubmissionDelayMaxSeconds, 30, 90);

    public DelaySchedule CreateCloseDelaySchedule() =>
        CreateDelaySchedule(DefaultCloseDelayMinSeconds, DefaultCloseDelayMaxSeconds, 5, 10);

    private static DelaySchedule CreateDelaySchedule(int minSeconds, int maxSeconds, int fallbackMin, int fallbackMax)
    {
        var min = minSeconds > 0 ? minSeconds : fallbackMin;
        var max = maxSeconds >= min ? maxSeconds : Math.Max(min, fallbackMax);
        return new DelaySchedule(TimeSpan.FromSeconds(min), TimeSpan.FromSeconds(max));
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(WorkbenchUrl))
        {
            WorkbenchUrl = "https://172.18.75.21/index.action";
        }

        if (string.IsNullOrWhiteSpace(SupportPlatformUrl))
        {
            SupportPlatformUrl = "https://172.18.75.21:18088/xzsw/pages/mini.jsp";
        }

        DefaultSubmissionDelayMinSeconds = Math.Max(1, DefaultSubmissionDelayMinSeconds);
        DefaultSubmissionDelayMaxSeconds = Math.Max(DefaultSubmissionDelayMinSeconds, DefaultSubmissionDelayMaxSeconds);
        DefaultCloseDelayMinSeconds = Math.Max(1, DefaultCloseDelayMinSeconds);
        DefaultCloseDelayMaxSeconds = Math.Max(DefaultCloseDelayMinSeconds, DefaultCloseDelayMaxSeconds);
    }
}
