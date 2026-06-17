using System.IO;
using System.Text.RegularExpressions;

namespace TechSupportBatchSubmitter.Wpf.Services;

public sealed partial class SafeFileLogger
{
    private readonly string _logPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SafeFileLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
    }

    public string LogPath => _logPath;

    public async Task WriteAsync(string message)
    {
        var sanitized = Sanitize(message);
        await _gate.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(
                _logPath,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {sanitized}{Environment.NewLine}");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string Sanitize(string value)
    {
        var singleLine = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        singleLine = SecretPattern().Replace(singleLine, "$1=[已隐藏]");
        singleLine = LongNumberPattern().Replace(singleLine, "[长数字已隐藏]");
        return singleLine.Length <= 1000 ? singleLine : singleLine[..1000];
    }

    [GeneratedRegex(@"(?i)\b(cookie|authorization|password|passwd|token)\s*[:=]\s*([^\s;]+)")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\b\d{11,18}[0-9Xx]?\b")]
    private static partial Regex LongNumberPattern();
}
