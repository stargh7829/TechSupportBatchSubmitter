using System.IO;

namespace TechSupportBatchSubmitter.Wpf.Services;

public static class ApplicationPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TechSupportBatchSubmitter");

    public static string DatabasePath => Path.Combine(RootDirectory, "recovery.db");

    public static string WebViewUserDataDirectory => Path.Combine(RootDirectory, "WebView2");

    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static string DiagnosticDirectory => Path.Combine(RootDirectory, "diagnostics");

    public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(WebViewUserDataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(DiagnosticDirectory);
    }
}
