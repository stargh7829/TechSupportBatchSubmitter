using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Wpf.Services;

public sealed class DiagnosticPackageExporter
{
    public async Task<string> ExportAsync(
        string outputDirectory,
        string? logPath,
        WorkbookLoadResult? workbook,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var packagePath = Path.Combine(outputDirectory, $"diagnostics_{timestamp}.zip");
        var tempDirectory = Path.Combine(outputDirectory, $".diagnostics_{timestamp}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory, "summary.txt"),
                BuildSummary(workbook, settings),
                Encoding.UTF8,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                var logText = await File.ReadAllTextAsync(logPath, cancellationToken);
                await File.WriteAllTextAsync(
                    Path.Combine(tempDirectory, "app-log-sanitized.txt"),
                    SafeFileLogger.Sanitize(logText),
                    Encoding.UTF8,
                    cancellationToken);
            }

            ZipFile.CreateFromDirectory(tempDirectory, packagePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return packagePath;
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static string BuildSummary(WorkbookLoadResult? workbook, AppSettings settings)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion ??
            assembly.GetName().Version?.ToString() ??
            "unknown";

        var builder = new StringBuilder();
        builder.AppendLine("技术支持批量提交工具诊断摘要");
        builder.AppendLine($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"程序版本：{version}");
        builder.AppendLine($".NET：{Environment.Version}");
        builder.AppendLine($"操作系统：{Environment.OSVersion.VersionString}");
        builder.AppendLine($"进程架构：{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"日志目录：{ApplicationPaths.LogDirectory}");
        builder.AppendLine($"数据库存在：{File.Exists(ApplicationPaths.DatabasePath)}");
        builder.AppendLine($"WebView 数据目录存在：{Directory.Exists(ApplicationPaths.WebViewUserDataDirectory)}");
        builder.AppendLine($"配置文件存在：{File.Exists(ApplicationPaths.SettingsPath)}");
        builder.AppendLine($"工作台地址：{settings.WorkbenchUri}");
        builder.AppendLine($"技术支持地址：{settings.SupportPlatformUri}");
        builder.AppendLine(
            $"默认提交间隔：{settings.DefaultSubmissionDelayMinSeconds}-{settings.DefaultSubmissionDelayMaxSeconds} 秒");
        builder.AppendLine(
            $"默认关闭间隔：{settings.DefaultCloseDelayMinSeconds}-{settings.DefaultCloseDelayMaxSeconds} 秒");
        builder.AppendLine();
        builder.AppendLine("当前 Excel 摘要");
        if (workbook is null)
        {
            builder.AppendLine("未选择 Excel。");
        }
        else
        {
            builder.AppendLine($"工作簿文件名：{Path.GetFileName(workbook.WorkbookPath)}");
            builder.AppendLine($"工作表：{workbook.WorksheetName}");
            builder.AppendLine($"备份文件名：{Path.GetFileName(workbook.BackupPath ?? string.Empty)}");
            builder.AppendLine(workbook.Diagnostics.ToSummary());
            foreach (var warning in workbook.Diagnostics.Warnings)
            {
                builder.AppendLine($"- {SafeFileLogger.Sanitize(warning)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("隐私说明：诊断包不包含 Excel 原文件、工单描述、账号密码、Cookie 或 Token。日志已做长数字和常见密钥脱敏。");
        return builder.ToString();
    }
}
