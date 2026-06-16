using System.IO;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace TechSupportBatchSubmitter.Wpf;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        EnsureWindowsDirectoryEnvironment();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"程序发生未处理异常，队列已停止。\n\n{args.Exception.Message}",
                "技术支持批量提交工具",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            WriteStartupDiagnostic(ex);
            MessageBox.Show(
                $"程序启动失败。\n\n{ex.Message}",
                "技术支持批量提交工具",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void EnsureWindowsDirectoryEnvironment()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
        {
            return;
        }

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot) && Directory.Exists(systemRoot))
        {
            Environment.SetEnvironmentVariable("windir", systemRoot, EnvironmentVariableTarget.Process);
        }
    }

    private static void WriteStartupDiagnostic(Exception exception)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "TechSupportBatchSubmitter-startup-error.txt");
            File.WriteAllText(
                path,
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}");
        }
        catch
        {
            // The diagnostic path is best-effort only.
        }
    }
}
