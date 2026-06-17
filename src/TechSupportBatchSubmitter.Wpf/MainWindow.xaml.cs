using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using TechSupportBatchSubmitter.Core.Exceptions;
using TechSupportBatchSubmitter.Core.Interfaces;
using TechSupportBatchSubmitter.Core.Models;
using TechSupportBatchSubmitter.Core.Services;
using TechSupportBatchSubmitter.Wpf.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace TechSupportBatchSubmitter.Wpf;

public partial class MainWindow : Window
{
    private enum CloseQueueSource
    {
        PendingQuery,
        ExcelSubmissionList
    }

    private readonly IWorkbookRepository _workbookRepository = new ExcelWorkbookRepository();
    private readonly ObservableCollection<TicketRow> _rows = [];
    private readonly ObservableCollection<PendingTicketRow> _pendingTickets = [];
    private readonly ObservableCollection<PendingTicketRow> _excelCloseTickets = [];
    private readonly ObservableCollection<SubmissionHistoryRecord> _submissionHistory = [];
    private readonly ObservableCollection<CloseHistoryRecord> _closeHistory = [];
    private readonly DiagnosticPackageExporter _diagnosticExporter = new();
    private readonly VersionNoticeService _versionNoticeService = new();
    private readonly AppSettings _settings;

    private SafeFileLogger? _logger;
    private ITicketPlatformClient? _platformClient;
    private ITicketHistoryStore? _historyStore;
    private ISubmissionQueue? _queue;
    private TicketCloseQueue? _closeQueue;
    private WorkbookLoadResult? _workbook;
    private IReadOnlyDictionary<string, ResolvedTicket> _resolvedTickets =
        new Dictionary<string, ResolvedTicket>();
    private CancellationTokenSource? _operationCancellation;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Drawing.Icon? _trayIconImage;
    private GridLength _normalLoginColumnWidth = new(420);
    private bool _validated;
    private bool _running;
    private bool _closing;
    private CloseQueueSource _activeCloseSource = CloseQueueSource.PendingQuery;
    private bool _loginPaneExpanded;
    private bool _loginPaneCollapsed;
    private bool _pendingDetailColumnsVisible;
    private bool _excelCloseDetailColumnsVisible;
    private bool _pendingCustomReplyVisible;
    private bool _excelCloseCustomReplyVisible;
    private CloseTicketCauseOption _pendingCloseCause = CloseReplyCatalog.DefaultCause;
    private CloseTicketCauseOption _excelCloseCause = CloseReplyCatalog.DefaultCause;

    private const double DetailTableRightPadding = 240;

    private DelaySchedule DefaultSubmissionInterval => _settings.CreateSubmissionDelaySchedule();
    private DelaySchedule DefaultCloseInterval => _settings.CreateCloseDelaySchedule();

    public MainWindow()
    {
        ApplicationPaths.EnsureCreated();
        _settings = AppSettings.LoadOrCreate(ApplicationPaths.SettingsPath);
        _logger = new SafeFileLogger(ApplicationPaths.LogDirectory);
        InitializeComponent();
        InitializeTrayIcon();
        Rows = _rows;
        PendingTickets = _pendingTickets;
        ExcelCloseTickets = _excelCloseTickets;
        SubmissionHistory = _submissionHistory;
        CloseHistory = _closeHistory;
        CloseCauseOptions = CloseReplyCatalog.CauseOptions;
        CloseSolveTypes = CloseReplyCatalog.SolveTypes;
        DataContext = this;
        InitializeCloseReplyDefaults();
    }

    public ObservableCollection<TicketRow> Rows { get; }
    public ObservableCollection<PendingTicketRow> PendingTickets { get; }
    public ObservableCollection<PendingTicketRow> ExcelCloseTickets { get; }
    public ObservableCollection<SubmissionHistoryRecord> SubmissionHistory { get; }
    public ObservableCollection<CloseHistoryRecord> CloseHistory { get; }
    public IReadOnlyList<CloseTicketCauseOption> CloseCauseOptions { get; }
    public IReadOnlyList<KeyValuePair<string, string>> CloseSolveTypes { get; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            AddLog($"WebView2 Runtime {runtimeVersion} 已就绪");

            var client = new WebViewTicketPlatformClient(PlatformWebView, _settings);
            await client.InitializeAsync(ApplicationPaths.WebViewUserDataDirectory);
            client.SessionExpired += PlatformClient_SessionExpired;
            _platformClient = client;

            var journal = new SqliteSubmissionJournal(ApplicationPaths.DatabasePath);
            await journal.InitializeAsync();
            var historyStore = new SqliteTicketHistoryStore(ApplicationPaths.DatabasePath);
            await historyStore.InitializeAsync();
            _historyStore = historyStore;

            var queue = new SubmissionQueue(
                _workbookRepository,
                journal,
                client,
                historyStore: historyStore);
            queue.LogEmitted += Queue_LogEmitted;
            queue.ProgressChanged += Queue_ProgressChanged;
            queue.CountdownChanged += Queue_CountdownChanged;
            _queue = queue;

            var closeQueue = new TicketCloseQueue(client, historyStore: historyStore);
            closeQueue.LogEmitted += CloseQueue_LogEmitted;
            closeQueue.ProgressChanged += CloseQueue_ProgressChanged;
            closeQueue.CountdownChanged += CloseQueue_CountdownChanged;
            _closeQueue = closeQueue;

            await RefreshHistoryAsync(CancellationToken.None);
            client.NavigateToWorkbench();
            SessionStatusText.Text = "请在左侧登录页面完成登录";
            FooterStatusText.Text = "程序已就绪";
            AddLog($"已加载外置配置：{ApplicationPaths.SettingsPath}");
            await CheckVersionNoticeAsync(CancellationToken.None);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            SessionStatusText.Text = "缺少 WebView2 Runtime";
            ShowWarning(
                "缺少运行组件",
                "未检测到 Microsoft Edge WebView2 Runtime。请运行便携包中的 MicrosoftEdgeWebview2Setup.exe 后重试。");
        }
        catch (Exception ex)
        {
            AddLog($"初始化失败：{ex.Message}", true);
            ShowError("初始化失败", ex.Message);
        }

        SetUiState();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _operationCancellation?.Cancel();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _trayMenu?.Dispose();
        _trayIconImage?.Dispose();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowMaximized();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowMaximized();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeWindowButton is not null)
        {
            MaximizeWindowButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
            MaximizeWindowButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        }
    }

    private void ToggleWindowMaximized()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void InitializeTrayIcon()
    {
        if (string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return;
        }

        using var extractedIcon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        if (extractedIcon is null)
        {
            return;
        }

        _trayIconImage = (Drawing.Icon)extractedIcon.Clone();
        _trayMenu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("打开主窗口");
        openItem.Click += (_, _) => RestoreFromTray();
        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.Invoke(Close);
        _trayMenu.Items.Add(openItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "技术支持批量提交工具",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private async Task CheckVersionNoticeAsync(CancellationToken cancellationToken)
    {
        var notice = await _versionNoticeService.CheckAsync(_settings, cancellationToken);
        if (notice is null)
        {
            return;
        }

        var message = $"当前版本：{notice.CurrentVersion}\n最新版本：{notice.LatestVersion}";
        if (!string.IsNullOrWhiteSpace(notice.ReleaseNotes))
        {
            message += $"\n\n{notice.ReleaseNotes}";
        }

        AddLog($"检测到新版本：{notice.LatestVersion}");
        ShowInfo("发现新版本", message);
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        });
    }

    private async void BrowseWorkbook_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择技术支持提交清单",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            WorkbookPathText.Text = dialog.FileName;
            await RunBusyActionAsync(LoadWorkbookAsync);
        }
    }

    private async Task LoadWorkbookAsync(CancellationToken token)
    {
        _workbook = await _workbookRepository.PrepareAndLoadAsync(WorkbookPathText.Text, token);
        _rows.Clear();
        foreach (var row in _workbook.Rows)
        {
            _rows.Add(row);
        }

        RebuildExcelCloseTickets();
        _resolvedTickets = new Dictionary<string, ResolvedTicket>();
        _validated = false;
        AddLog($"已自动读取工作表“{_workbook.WorksheetName}”，共 {_workbook.Rows.Count} 条");
        AddLog($"模板校验摘要：{_workbook.Diagnostics.ToSummary()}");
        foreach (var warning in _workbook.Diagnostics.Warnings)
        {
            AddLog(warning, true);
        }

        if (!string.IsNullOrWhiteSpace(_workbook.BackupPath))
        {
            AddLog($"已自动创建 Excel 备份：{Path.GetFileName(_workbook.BackupPath)}");
        }

        FooterStatusText.Text = "清单已就绪，点击开始提交将自动预检";
        RefreshStatistics();
        UpdateExcelCloseSelectionState();
    }

    private async Task ValidateWorkbookAsync(CancellationToken token)
    {
        if (_workbook is null || _queue is null)
        {
            throw new InvalidOperationException("请先选择 Excel 清单。");
        }

        await EnsureSupportSessionAsync(token);
        AddLog("自动预检必填字段及平台人员、事件类型、所属系统");
        _resolvedTickets = await _queue.ValidateAsync(_workbook, token);
        _validated = true;
        RefreshStatistics();
        FooterStatusText.Text = $"预检完成，可提交 {_resolvedTickets.Count} 条";
        AddLog($"自动预检完成，通过 {_resolvedTickets.Count} 条");
    }

    private async Task<bool> EnsureWorkbookValidatedAsync()
    {
        if (_validated)
        {
            return true;
        }

        await RunBusyActionAsync(ValidateWorkbookAsync);
        return _validated;
    }

    private async void StartSubmission_Click(object sender, RoutedEventArgs e)
    {
        if (_workbook is null)
        {
            ShowInfo("提示", "请先选择 Excel 清单。");
            return;
        }

        if (!await EnsureWorkbookValidatedAsync())
        {
            return;
        }

        var count = _rows.Count(row => row.State == SubmissionState.Pending);
        if (count == 0)
        {
            ShowInfo(
                "提示",
                "预检后没有可提交记录，请查看“校验失败”及失败原因。");
            return;
        }

        var submissionInterval = TryGetSubmissionIntervalSchedule();
        if (submissionInterval is null)
        {
            return;
        }

        var result = ShowConfirm(
            "确认真实提交",
            $"即将向技术支持系统提交 {count} 条记录。\n" +
            $"第一条立即提交，之后每条{DescribeInterval(submissionInterval)}。\n\n确认开始吗？");
        if (result)
        {
            await RunQueueAsync(SubmissionRunMode.Pending, submissionInterval);
        }
    }

    private async void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureWorkbookValidatedAsync())
        {
            return;
        }

        var count = _rows.Count(row => row.State == SubmissionState.Failed);
        if (count == 0)
        {
            ShowInfo("提示", "没有失败记录。");
            return;
        }

        var submissionInterval = TryGetSubmissionIntervalSchedule();
        if (submissionInterval is null)
        {
            return;
        }

        var result = ShowConfirm(
            "确认重试",
            $"仅重试 {count} 条失败记录，仍按当前提交间隔执行：" +
            $"{DescribeInterval(submissionInterval)}。\n\n确认开始吗？");
        if (result)
        {
            await RunQueueAsync(SubmissionRunMode.FailedOnly, submissionInterval);
        }
    }

    private void PauseSubmission_Click(object sender, RoutedEventArgs e)
    {
        AddLog("用户请求暂停，当前未发出的请求将停止");
        _operationCancellation?.Cancel();
    }

    private void StopSubmission_Click(object sender, RoutedEventArgs e)
    {
        AddLog("用户请求停止队列");
        _operationCancellation?.Cancel();
    }

    private void OpenWorkbench_Click(object sender, RoutedEventArgs e)
    {
        (_platformClient as WebViewTicketPlatformClient)?.NavigateToWorkbench();
        SessionStatusText.Text = "请在左侧登录页面完成登录";
    }

    private async void OpenSupport_Click(object sender, RoutedEventArgs e)
    {
        if (_platformClient is not WebViewTicketPlatformClient client)
        {
            return;
        }

        try
        {
            SessionStatusText.Text = "正在打开技术支持系统";
            await client.OpenSupportPlatformAsync();
        }
        catch (Exception ex)
        {
            AddLog($"打开技术支持系统失败：{ex.Message}", true);
            ShowError("打开失败", ex.Message);
        }
    }

    private void ToggleLoginPane_Click(object sender, RoutedEventArgs e)
    {
        if (_loginPaneCollapsed)
        {
            RestoreLoginPane();
        }

        if (!_loginPaneExpanded)
        {
            _normalLoginColumnWidth = LoginColumn.Width;
            WorkspaceTabs.Visibility = Visibility.Collapsed;
            LoginSplitter.Visibility = Visibility.Collapsed;
            SplitterColumn.Width = new GridLength(0);
            WorkspaceColumn.Width = new GridLength(0);
            LoginColumn.Width = new GridLength(1, GridUnitType.Star);
            ExpandLoginButton.Content = "恢复布局";
            _loginPaneExpanded = true;
            return;
        }

        LoginColumn.Width = _normalLoginColumnWidth;
        SplitterColumn.Width = new GridLength(8);
        WorkspaceColumn.Width = new GridLength(1, GridUnitType.Star);
        LoginSplitter.Visibility = Visibility.Visible;
        WorkspaceTabs.Visibility = Visibility.Visible;
        ExpandLoginButton.Content = "放大页面";
        _loginPaneExpanded = false;
    }

    private void ToggleLoginCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (_loginPaneCollapsed)
        {
            RestoreLoginPane();
            return;
        }

        if (!_loginPaneExpanded)
        {
            _normalLoginColumnWidth = LoginColumn.Width;
        }

        LoginPane.Visibility = Visibility.Collapsed;
        CollapsedLoginBar.Visibility = Visibility.Visible;
        WorkspaceTabs.Visibility = Visibility.Visible;
        LoginSplitter.Visibility = Visibility.Collapsed;
        LoginColumn.Width = new GridLength(44);
        SplitterColumn.Width = new GridLength(0);
        WorkspaceColumn.Width = new GridLength(1, GridUnitType.Star);
        ExpandLoginButton.Content = "放大页面";
        _loginPaneExpanded = false;
        _loginPaneCollapsed = true;
    }

    private void RestoreLoginPane()
    {
        LoginPane.Visibility = Visibility.Visible;
        CollapsedLoginBar.Visibility = Visibility.Collapsed;
        LoginColumn.Width = _normalLoginColumnWidth.Value >= 300
            ? _normalLoginColumnWidth
            : new GridLength(420);
        SplitterColumn.Width = new GridLength(8);
        WorkspaceColumn.Width = new GridLength(1, GridUnitType.Star);
        LoginSplitter.Visibility = Visibility.Visible;
        WorkspaceTabs.Visibility = Visibility.Visible;
        _loginPaneCollapsed = false;
    }

    private async void QueryPending_Click(object sender, RoutedEventArgs e)
    {
        if (_platformClient is null)
        {
            ShowInfo("提示", "平台组件尚未初始化。");
            return;
        }

        await RunBusyActionAsync(async token =>
        {
            await EnsureSupportSessionAsync(token);
            await RefreshPendingTicketsAsync(token);
        });
    }

    private void ResetPendingQuery_Click(object sender, RoutedEventArgs e)
    {
        PendingTitleText.Clear();
        ClearPendingAdvancedFields();
        PendingAdvancedPanel.Visibility = Visibility.Collapsed;
        TogglePendingAdvancedButton.Content = "更多条件";
        _pendingTickets.Clear();
        PendingResultTotalText.Text = "0";
        SelectAllPendingCheckBox.IsChecked = false;
        CloseProgressText.Text = "请选择需要关闭的记录";
        FooterStatusText.Text = "待受理查询条件已清空";
        SetUiState();
    }

    private void TogglePendingAdvanced_Click(object sender, RoutedEventArgs e)
    {
        var expand = PendingAdvancedPanel.Visibility != Visibility.Visible;
        PendingAdvancedPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        TogglePendingAdvancedButton.Content = expand ? "收起条件" : "更多条件";
    }

    private void TogglePendingDetailColumns_Click(object sender, RoutedEventArgs e)
    {
        _pendingDetailColumnsVisible = !_pendingDetailColumnsVisible;
        SetColumnVisibility(
            _pendingDetailColumnsVisible,
            PendingTypeColumn,
            PendingSystemColumn,
            PendingAssigneeColumn,
            PendingHandlerColumn,
            PendingApplicantColumn,
            PendingCreateTimeColumn,
            PendingLatestReplyTimeColumn,
            PendingCloseMessageColumn);
        SetDetailTableWidth(
            PendingTicketHorizontalScroller,
            PendingTicketGrid,
            _pendingDetailColumnsVisible);
        if (_pendingDetailColumnsVisible)
        {
            Dispatcher.BeginInvoke(
                () => SetDetailTableWidth(PendingTicketHorizontalScroller, PendingTicketGrid, true),
                DispatcherPriority.Loaded);
        }
        TogglePendingDetailColumnsButton.Content = _pendingDetailColumnsVisible
            ? "收起详情"
            : "展开详情";
        FooterStatusText.Text = _pendingDetailColumnsVisible
            ? "已显示待受理详情字段"
            : "已收起待受理详情字段";
    }

    private void ToggleExcelCloseDetailColumns_Click(object sender, RoutedEventArgs e)
    {
        _excelCloseDetailColumnsVisible = !_excelCloseDetailColumnsVisible;
        SetColumnVisibility(
            _excelCloseDetailColumnsVisible,
            ExcelCloseTypeColumn,
            ExcelCloseSystemColumn,
            ExcelCloseAssigneeColumn,
            ExcelCloseApplicantColumn,
            ExcelCloseCreateTimeColumn,
            ExcelCloseClosedAtColumn,
            ExcelCloseMessageColumn);
        SetDetailTableWidth(
            ExcelCloseTicketHorizontalScroller,
            ExcelCloseTicketGrid,
            _excelCloseDetailColumnsVisible);
        if (_excelCloseDetailColumnsVisible)
        {
            Dispatcher.BeginInvoke(
                () => SetDetailTableWidth(ExcelCloseTicketHorizontalScroller, ExcelCloseTicketGrid, true),
                DispatcherPriority.Loaded);
        }
        ToggleExcelCloseDetailColumnsButton.Content = _excelCloseDetailColumnsVisible
            ? "收起详情"
            : "展开详情";
        FooterStatusText.Text = _excelCloseDetailColumnsVisible
            ? "已显示提交清单关闭详情字段"
            : "已收起提交清单关闭详情字段";
    }

    private void InitializeCloseReplyDefaults()
    {
        ResetCloseReplyFields(
            PendingCloseCauseText,
            PendingCloseSolveTypeComboBox,
            PendingCloseCauseDescriptionText,
            PendingCloseSolutionText,
            ref _pendingCloseCause);
        ResetCloseReplyFields(
            ExcelCloseCauseText,
            ExcelCloseSolveTypeComboBox,
            ExcelCloseCauseDescriptionText,
            ExcelCloseSolutionText,
            ref _excelCloseCause);
    }

    private static void ResetCloseReplyFields(
        System.Windows.Controls.TextBox causeText,
        System.Windows.Controls.ComboBox solveTypeComboBox,
        System.Windows.Controls.TextBox causeDescriptionText,
        System.Windows.Controls.TextBox solutionText,
        ref CloseTicketCauseOption selectedCause)
    {
        selectedCause = CloseReplyCatalog.DefaultCause;
        causeText.Text = selectedCause.Path;
        solveTypeComboBox.SelectedValue = CloseTicketSettings.Default.SolveTypeValue;
        causeDescriptionText.Text = CloseTicketSettings.Default.CauseDescription;
        solutionText.Text = CloseTicketSettings.Default.SolutionDescription;
    }

    private void TogglePendingCustomReply_Click(object sender, RoutedEventArgs e)
    {
        _pendingCustomReplyVisible = !_pendingCustomReplyVisible;
        PendingCustomReplyPanel.Visibility = _pendingCustomReplyVisible ? Visibility.Visible : Visibility.Collapsed;
        TogglePendingCustomReplyButton.Content = _pendingCustomReplyVisible ? "默认回复" : "自定义回复";
        FooterStatusText.Text = _pendingCustomReplyVisible
            ? "已打开待受理自定义回复"
            : "待受理关闭将使用默认回复";
    }

    private void ToggleExcelCloseCustomReply_Click(object sender, RoutedEventArgs e)
    {
        _excelCloseCustomReplyVisible = !_excelCloseCustomReplyVisible;
        ExcelCloseCustomReplyPanel.Visibility =
            _excelCloseCustomReplyVisible ? Visibility.Visible : Visibility.Collapsed;
        ToggleExcelCloseCustomReplyButton.Content = _excelCloseCustomReplyVisible ? "默认回复" : "自定义回复";
        FooterStatusText.Text = _excelCloseCustomReplyVisible
            ? "已打开提交清单关闭自定义回复"
            : "提交清单关闭将使用默认回复";
    }

    private void ResetPendingCustomReply_Click(object sender, RoutedEventArgs e)
    {
        ResetCloseReplyFields(
            PendingCloseCauseText,
            PendingCloseSolveTypeComboBox,
            PendingCloseCauseDescriptionText,
            PendingCloseSolutionText,
            ref _pendingCloseCause);
    }

    private void ResetExcelCloseCustomReply_Click(object sender, RoutedEventArgs e)
    {
        ResetCloseReplyFields(
            ExcelCloseCauseText,
            ExcelCloseSolveTypeComboBox,
            ExcelCloseCauseDescriptionText,
            ExcelCloseSolutionText,
            ref _excelCloseCause);
    }

    private void TogglePendingCloseCausePopup_Click(object sender, RoutedEventArgs e) =>
        PendingCloseCausePopup.IsOpen = !PendingCloseCausePopup.IsOpen;

    private void ToggleExcelCloseCausePopup_Click(object sender, RoutedEventArgs e) =>
        ExcelCloseCausePopup.IsOpen = !ExcelCloseCausePopup.IsOpen;

    private void PendingCloseCauseTree_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not CloseTicketCauseOption option)
        {
            return;
        }

        _pendingCloseCause = option;
        PendingCloseCauseText.Text = option.Path;
        PendingCloseCausePopup.IsOpen = false;
    }

    private void ExcelCloseCauseTree_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not CloseTicketCauseOption option)
        {
            return;
        }

        _excelCloseCause = option;
        ExcelCloseCauseText.Text = option.Path;
        ExcelCloseCausePopup.IsOpen = false;
    }

    private CloseTicketSettings? TryGetCloseSettings(CloseQueueSource source)
    {
        var customEnabled = source == CloseQueueSource.ExcelSubmissionList
            ? _excelCloseCustomReplyVisible
            : _pendingCustomReplyVisible;
        if (!customEnabled)
        {
            return CloseTicketSettings.Default;
        }

        var cause = source == CloseQueueSource.ExcelSubmissionList
            ? _excelCloseCause
            : _pendingCloseCause;
        var causeDescription = (source == CloseQueueSource.ExcelSubmissionList
            ? ExcelCloseCauseDescriptionText.Text
            : PendingCloseCauseDescriptionText.Text).Trim();
        var solutionDescription = (source == CloseQueueSource.ExcelSubmissionList
            ? ExcelCloseSolutionText.Text
            : PendingCloseSolutionText.Text).Trim();
        var solveTypeComboBox = source == CloseQueueSource.ExcelSubmissionList
            ? ExcelCloseSolveTypeComboBox
            : PendingCloseSolveTypeComboBox;
        var solveTypeValue = solveTypeComboBox.SelectedValue?.ToString() ?? string.Empty;
        var solveTypeName = solveTypeComboBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(cause.Path))
        {
            ShowInfo("自定义回复未完整", "请选择原因分类。");
            return null;
        }

        if (string.IsNullOrWhiteSpace(causeDescription))
        {
            ShowInfo("自定义回复未完整", "请填写原因描述。");
            return null;
        }

        if (string.IsNullOrWhiteSpace(solveTypeValue) || string.IsNullOrWhiteSpace(solveTypeName))
        {
            ShowInfo("自定义回复未完整", "请选择解决方法。");
            return null;
        }

        if (string.IsNullOrWhiteSpace(solutionDescription))
        {
            ShowInfo("自定义回复未完整", "请填写解决方案。");
            return null;
        }

        return new CloseTicketSettings(
            cause.Value,
            cause.Name,
            cause.Path,
            causeDescription,
            solveTypeValue,
            solveTypeName,
            solutionDescription);
    }

    private static void SetColumnVisibility(
        bool visible,
        params System.Windows.Controls.DataGridColumn[] columns)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var column in columns)
        {
            column.Visibility = visibility;
        }
    }

    private static void SetDetailTableWidth(
        ScrollViewer scroller,
        DataGrid grid,
        bool detailVisible)
    {
        if (detailVisible)
        {
            scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
            var detailWidth = CalculateVisibleColumnWidth(grid);
            grid.MinWidth = detailWidth;
            grid.Width = detailWidth;
            grid.Margin = new Thickness(0, 0, DetailTableRightPadding, 0);
            return;
        }

        scroller.ScrollToHorizontalOffset(0);
        scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        grid.ClearValue(FrameworkElement.MinWidthProperty);
        grid.ClearValue(FrameworkElement.WidthProperty);
        grid.ClearValue(FrameworkElement.MarginProperty);
    }

    private static double CalculateVisibleColumnWidth(DataGrid grid)
    {
        var total = grid.Columns
            .Where(column => column.Visibility == Visibility.Visible)
            .Sum(GetColumnRequestedWidth);
        return Math.Max(total, grid.ActualWidth);
    }

    private static double GetColumnRequestedWidth(DataGridColumn column)
    {
        var width = column.Width;
        if (width.IsAbsolute)
        {
            return Math.Max(width.Value, column.MinWidth);
        }

        if (column.ActualWidth > 0)
        {
            return Math.Max(column.ActualWidth, column.MinWidth);
        }

        if (column.MinWidth > 0)
        {
            return column.MinWidth;
        }

        return 120;
    }

    private async Task RefreshPendingTicketsAsync(
        CancellationToken cancellationToken,
        bool writeLog = true)
    {
        if (_platformClient is null)
        {
            throw new InvalidOperationException("平台组件尚未初始化。");
        }

        var result = await _platformClient.QueryPendingAsync(
            BuildPendingQueryCriteria(),
            cancellationToken);
        _pendingTickets.Clear();
        foreach (var item in result.Items)
        {
            AttachPendingTicketSelectionHandler(item);
            _pendingTickets.Add(item);
        }

        PendingResultTotalText.Text = result.Total.ToString();
        SelectAllPendingCheckBox.IsChecked = false;
        FooterStatusText.Text = $"待受理查询完成，共 {result.Total} 条";
        if (writeLog)
        {
            AddLog($"待受理查询完成，共 {result.Total} 条");
        }

        SetUiState();
    }

    private PendingTicketQueryCriteria BuildPendingQueryCriteria() => new(
        PendingTitleText.Text.Trim(),
        PendingCaseIdText.Text.Trim(),
        PendingTypeText.Text.Trim(),
        PendingSystemText.Text.Trim(),
        PendingStatusText.Text.Trim(),
        PendingAssigneeText.Text.Trim(),
        PendingApplicantText.Text.Trim(),
        PendingCreatorOrgText.Text.Trim(),
        PendingHandlerText.Text.Trim(),
        FormatPendingDate(PendingStartDatePicker.SelectedDate),
        FormatPendingDate(PendingEndDatePicker.SelectedDate),
        FormatPendingDate(PendingCloseStartDatePicker.SelectedDate),
        FormatPendingDate(PendingCloseEndDatePicker.SelectedDate));

    private void ClearPendingAdvancedFields()
    {
        PendingCaseIdText.Clear();
        PendingTypeText.Clear();
        PendingSystemText.Clear();
        PendingStatusText.Clear();
        PendingAssigneeText.Clear();
        PendingApplicantText.Clear();
        PendingCreatorOrgText.Clear();
        PendingHandlerText.Clear();
        PendingStartDatePicker.SelectedDate = null;
        PendingEndDatePicker.SelectedDate = null;
        PendingCloseStartDatePicker.SelectedDate = null;
        PendingCloseEndDatePicker.SelectedDate = null;
    }

    private static string FormatPendingDate(DateTime? value) =>
        value?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private void SelectAllPending_Click(object sender, RoutedEventArgs e)
    {
        var select = SelectAllPendingCheckBox.IsChecked == true;
        foreach (var ticket in _pendingTickets)
        {
            ticket.IsSelected = select && ticket.CanClose;
        }

        UpdatePendingSelectionState();
    }

    private void PendingSelection_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(UpdatePendingSelectionState);
    }

    private void AttachPendingTicketSelectionHandler(PendingTicketRow ticket)
    {
        ticket.PropertyChanged += PendingTicket_PropertyChanged;
    }

    private void PendingTicket_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PendingTicketRow.IsSelected) or nameof(PendingTicketRow.CloseState)))
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (sender is PendingTicketRow ticket && _pendingTickets.Contains(ticket))
            {
                UpdatePendingSelectionState();
            }
        });
    }

    private void UpdatePendingSelectionState()
    {
        var selectable = _pendingTickets.Where(ticket => ticket.CanClose).ToList();
        var selected = selectable.Count(ticket => ticket.IsSelected);
        SelectAllPendingCheckBox.IsChecked = selectable.Count == 0 || selected == 0
            ? false
            : selected == selectable.Count
                ? true
                : null;
        if (!_closing)
        {
            CloseProgressText.Text = selected == 0
                ? "请选择需要关闭的记录"
                : $"已选择 {selected} 条";
        }
        SetUiState();
    }

    private async void ClosePendingTicket_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PendingTicketRow ticket } ||
            !ticket.CanClose)
        {
            return;
        }

        var closeInterval = TryGetCloseIntervalSchedule(CloseQueueSource.PendingQuery);
        if (closeInterval is null)
        {
            return;
        }

        var closeSettings = TryGetCloseSettings(CloseQueueSource.PendingQuery);
        if (closeSettings is null)
        {
            return;
        }

        if (!ConfirmClose(1, false, closeInterval, closeSettings))
        {
            return;
        }

        await RunCloseQueueAsync([ticket], CloseQueueSource.PendingQuery, closeInterval, closeSettings);
    }

    private async void BulkClose_Click(object sender, RoutedEventArgs e)
    {
        var selected = _pendingTickets
            .Where(ticket => ticket.IsSelected && ticket.CanClose)
            .ToList();
        if (selected.Count == 0)
        {
            ShowInfo(
                "提示",
                "请先勾选需要关闭的记录。");
            return;
        }

        var closeInterval = TryGetCloseIntervalSchedule(CloseQueueSource.PendingQuery);
        if (closeInterval is null)
        {
            return;
        }

        var closeSettings = TryGetCloseSettings(CloseQueueSource.PendingQuery);
        if (closeSettings is null)
        {
            return;
        }

        if (!ConfirmClose(selected.Count, true, closeInterval, closeSettings))
        {
            return;
        }

        await RunCloseQueueAsync(selected, CloseQueueSource.PendingQuery, closeInterval, closeSettings);
    }

    private void RebuildExcelCloseTickets()
    {
        _excelCloseTickets.Clear();
        if (_workbook is null)
        {
            ExcelCloseResultTotalText.Text = "0";
            ExcelCloseProgressText.Text = "请先选择 Excel 清单";
            SetUiState();
            return;
        }

        foreach (var ticket in ExcelCloseTicketFactory.CreateCloseRows(_workbook))
        {
            AttachExcelCloseTicketSelectionHandler(ticket);
            _excelCloseTickets.Add(ticket);
        }

        ExcelCloseResultTotalText.Text = _excelCloseTickets.Count.ToString();
        ExcelCloseProgressText.Text = _excelCloseTickets.Count == 0
            ? "当前 Excel 没有已生成编号的记录"
            : $"已从当前 Excel 清单生成 {_excelCloseTickets.Count} 条可核对记录";
        SelectAllExcelCloseCheckBox.IsChecked = false;
        SetUiState();
    }

    private void RefreshExcelCloseList_Click(object sender, RoutedEventArgs e)
    {
        if (_workbook is null)
        {
            ShowInfo("提示", "请先在“批量提交”页选择 Excel 清单。");
            return;
        }

        RebuildExcelCloseTickets();
        UpdateExcelCloseSelectionState();
        FooterStatusText.Text = $"已刷新提交清单关闭列表，共 {_excelCloseTickets.Count} 条";
    }

    private void SelectAllExcelClose_Click(object sender, RoutedEventArgs e)
    {
        var select = SelectAllExcelCloseCheckBox.IsChecked == true;
        foreach (var ticket in _excelCloseTickets)
        {
            ticket.IsSelected = select && ticket.CanClose;
        }

        UpdateExcelCloseSelectionState();
    }

    private void ExcelCloseSelection_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(UpdateExcelCloseSelectionState);
    }

    private void AttachExcelCloseTicketSelectionHandler(PendingTicketRow ticket)
    {
        ticket.PropertyChanged += ExcelCloseTicket_PropertyChanged;
    }

    private void ExcelCloseTicket_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PendingTicketRow.IsSelected) or nameof(PendingTicketRow.CloseState)))
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (sender is PendingTicketRow ticket && _excelCloseTickets.Contains(ticket))
            {
                UpdateExcelCloseSelectionState();
            }
        });
    }

    private void UpdateExcelCloseSelectionState()
    {
        var selectable = _excelCloseTickets.Where(ticket => ticket.CanClose).ToList();
        var selected = selectable.Count(ticket => ticket.IsSelected);
        SelectAllExcelCloseCheckBox.IsChecked = selectable.Count == 0 || selected == 0
            ? false
            : selected == selectable.Count
                ? true
                : null;
        if (!_closing || _activeCloseSource != CloseQueueSource.ExcelSubmissionList)
        {
            ExcelCloseProgressText.Text = selected == 0
                ? "请选择需要关闭的提交清单记录"
                : $"已选择 {selected} 条";
        }

        SetUiState();
    }

    private async void CloseExcelTicket_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PendingTicketRow ticket } ||
            !ticket.CanClose)
        {
            return;
        }

        var closeInterval = TryGetCloseIntervalSchedule(CloseQueueSource.ExcelSubmissionList);
        if (closeInterval is null)
        {
            return;
        }

        var closeSettings = TryGetCloseSettings(CloseQueueSource.ExcelSubmissionList);
        if (closeSettings is null)
        {
            return;
        }

        if (!ConfirmClose(1, false, closeInterval, closeSettings))
        {
            return;
        }

        await RunCloseQueueAsync([ticket], CloseQueueSource.ExcelSubmissionList, closeInterval, closeSettings);
    }

    private async void BulkCloseExcel_Click(object sender, RoutedEventArgs e)
    {
        var selected = _excelCloseTickets
            .Where(ticket => ticket.IsSelected && ticket.CanClose)
            .ToList();
        if (selected.Count == 0)
        {
            ShowInfo(
                "提示",
                "请先勾选需要关闭的提交清单记录。");
            return;
        }

        var closeInterval = TryGetCloseIntervalSchedule(CloseQueueSource.ExcelSubmissionList);
        if (closeInterval is null)
        {
            return;
        }

        var closeSettings = TryGetCloseSettings(CloseQueueSource.ExcelSubmissionList);
        if (closeSettings is null)
        {
            return;
        }

        if (!ConfirmClose(selected.Count, true, closeInterval, closeSettings))
        {
            return;
        }

        await RunCloseQueueAsync(selected, CloseQueueSource.ExcelSubmissionList, closeInterval, closeSettings);
    }

    private bool ConfirmClose(
        int count,
        bool isBulk,
        DelaySchedule closeInterval,
        CloseTicketSettings settings)
    {
        var interval = isBulk
            ? $"\n每条核验完成后{DescribeInterval(closeInterval)}再处理下一条。"
            : string.Empty;
        return ShowConfirm(
            "确认关闭技术支持单",
            $"即将对真实平台中的 {count} 条技术支持单执行“受理并总结”。\n\n" +
            $"{settings.ToConfirmationText()}\n" +
            $"事件描述保持平台默认引用值，不修改。{interval}\n\n确认继续吗？");
    }

    private async Task RunCloseQueueAsync(
        IReadOnlyCollection<PendingTicketRow> tickets,
        CloseQueueSource source,
        DelaySchedule closeInterval,
        CloseTicketSettings closeSettings)
    {
        if (_closeQueue is null)
        {
            ShowInfo(
                "提示",
                "关闭组件尚未初始化。");
            return;
        }

        _closeQueue.Interval = closeInterval;
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _running = true;
        _closing = true;
        _activeCloseSource = source;
        SetUiState();

        try
        {
            await EnsureSupportSessionAsync(_operationCancellation.Token);
            FooterStatusText.Text = "关闭队列运行中";
            await _closeQueue.RunAsync(tickets, closeSettings, _operationCancellation.Token);
            await PersistExcelCloseResultsIfNeededAsync(source, tickets, _operationCancellation.Token);
            FooterStatusText.Text = "关闭队列执行完成";
            AddLog("关闭队列执行完成");

            if (source == CloseQueueSource.PendingQuery)
            {
                try
                {
                    await RefreshPendingTicketsAsync(_operationCancellation.Token, writeLog: false);
                    AddLog("已刷新待受理查询结果");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AddLog($"关闭已完成，但刷新查询结果失败：{ex.Message}", true);
                }
            }
            else
            {
                RebuildExcelCloseTickets();
                AddLog("已回写 Excel 关闭结果");
            }
        }
        catch (OperationCanceledException)
        {
            await PersistExcelCloseResultsIfNeededAsync(source, tickets, CancellationToken.None, showMessage: false);
            FooterStatusText.Text = "关闭队列已暂停";
            AddLog("关闭队列已暂停，可检查状态后继续");
        }
        catch (SubmissionOutcomeUnknownException ex)
        {
            await PersistExcelCloseResultsIfNeededAsync(source, tickets, CancellationToken.None, showMessage: false);
            FooterStatusText.Text = "关闭结果待核验，队列已暂停";
            AddLog(ex.Message, true);
            ShowWarning(
                "关闭结果待核验",
                "当前工单的保存请求已经发出，但结果无法确认。请在平台人工核验，程序不会自动重复关闭。");
        }
        catch (PlatformSessionExpiredException ex)
        {
            await PersistExcelCloseResultsIfNeededAsync(source, tickets, CancellationToken.None, showMessage: false);
            FooterStatusText.Text = "登录失效，关闭队列已暂停";
            AddLog(ex.Message, true);
            ShowWarning(
                "关闭队列已暂停",
                "登录已失效。请在左侧重新登录并人工确认当前工单状态后再继续。");
        }
        catch (Exception ex)
        {
            await PersistExcelCloseResultsIfNeededAsync(source, tickets, CancellationToken.None, showMessage: false);
            FooterStatusText.Text = "关闭队列因异常暂停";
            AddLog(ex.Message, true);
            ShowError("关闭队列已暂停", ex.Message);
        }
        finally
        {
            _running = false;
            _closing = false;
            if (source == CloseQueueSource.PendingQuery)
            {
                CloseCountdownText.Text = "00:00";
            }
            else
            {
                ExcelCloseCountdownText.Text = "00:00";
            }

            await RefreshHistoryAsync(CancellationToken.None);
            UpdatePendingSelectionState();
            UpdateExcelCloseSelectionState();
        }
    }

    private void PauseClose_Click(object sender, RoutedEventArgs e)
    {
        AddLog("用户请求暂停关闭队列，当前未发出的请求将停止");
        _operationCancellation?.Cancel();
    }

    private void StopClose_Click(object sender, RoutedEventArgs e)
    {
        AddLog("用户请求停止关闭队列");
        _operationCancellation?.Cancel();
    }

    private async Task PersistExcelCloseResultsIfNeededAsync(
        CloseQueueSource source,
        IReadOnlyCollection<PendingTicketRow> tickets,
        CancellationToken cancellationToken,
        bool showMessage = true)
    {
        if (source != CloseQueueSource.ExcelSubmissionList || _workbook is null || tickets.Count == 0)
        {
            return;
        }

        var changedRows = new List<TicketRow>();
        foreach (var ticket in tickets)
        {
            var row = _rows.FirstOrDefault(sourceRow =>
                sourceRow.ExcelRowNumber == ticket.ExcelRowNumber &&
                string.Equals(sourceRow.Fingerprint, ticket.Fingerprint, StringComparison.Ordinal));
            if (row is null)
            {
                continue;
            }

            row.CloseState = ticket.CloseState;
            row.ClosedAt = ticket.ClosedAt;
            row.CloseFailureReason = ticket.CloseState switch
            {
                TicketCloseState.Succeeded => null,
                TicketCloseState.Ready when string.IsNullOrWhiteSpace(ticket.CloseMessage) => null,
                _ => string.IsNullOrWhiteSpace(ticket.CloseMessage) ? null : ticket.CloseMessage
            };
            changedRows.Add(row);
        }

        if (changedRows.Count == 0)
        {
            return;
        }

        try
        {
            await _workbookRepository.UpdateRowsAsync(
                _workbook.WorkbookPath,
                _workbook.WorksheetName,
                changedRows,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FooterStatusText.Text = "关闭已执行，但 Excel 回写失败";
            AddLog($"Excel 关闭结果回写失败：{ex.Message}", true);
            if (showMessage)
            {
                ShowWarning(
                    "Excel 回写失败",
                    "平台关闭操作已经执行，但 Excel 关闭结果回写失败。请关闭正在打开的 Excel 文件，重新选择清单后核对关闭状态。");
            }
        }
    }

    private async Task RefreshHistoryAsync(CancellationToken cancellationToken)
    {
        if (_historyStore is null)
        {
            return;
        }

        var submissions = await _historyStore.GetSubmissionHistoryAsync(500, cancellationToken);
        var closes = await _historyStore.GetCloseHistoryAsync(500, cancellationToken);

        _submissionHistory.Clear();
        foreach (var record in submissions)
        {
            _submissionHistory.Add(record);
        }

        _closeHistory.Clear();
        foreach (var record in closes)
        {
            _closeHistory.Add(record);
        }
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyActionAsync(async token =>
        {
            await RefreshHistoryAsync(token);
            FooterStatusText.Text = "历史记录已刷新";
        });
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyActionAsync(async token =>
        {
            var packagePath = await _diagnosticExporter.ExportAsync(
                ApplicationPaths.DiagnosticDirectory,
                _logger?.LogPath,
                _workbook,
                _settings,
                token);
            AddLog($"诊断包已导出：{packagePath}");
            FooterStatusText.Text = "诊断包已导出";

            var result = ShowConfirm(
                "诊断包已导出",
                $"已生成脱敏诊断包：\n{packagePath}\n\n是否打开所在文件夹？");
            if (result)
            {
                OpenDirectory(ApplicationPaths.DiagnosticDirectory);
            }
        });
    }

    private async void CheckSession_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshSessionStatusAsync();
        }
        catch (Exception ex)
        {
            AddLog($"登录检查失败：{ex.Message}", true);
        }
    }

    private async Task RunQueueAsync(SubmissionRunMode mode, DelaySchedule submissionInterval)
    {
        if (_workbook is null || _queue is null || !_validated)
        {
            ShowInfo("提示", "请先读取并校验清单。");
            return;
        }

        _queue.SubmissionInterval = submissionInterval;
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _running = true;
        SetUiState();

        try
        {
            await EnsureSupportSessionAsync(_operationCancellation.Token);
            FooterStatusText.Text = "队列运行中";
            await _queue.RunAsync(
                _workbook,
                _resolvedTickets,
                mode,
                _operationCancellation.Token);
            FooterStatusText.Text = "队列执行完成";
            AddLog("队列执行完成");
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = "队列已暂停";
            AddLog("队列已暂停，可检查状态后继续");
        }
        catch (PlatformSessionExpiredException ex)
        {
            FooterStatusText.Text = "登录失效，队列已暂停";
            AddLog(ex.Message, true);
            ShowWarning(
                "队列已暂停",
                "登录已失效。请在左侧重新登录，检查登录状态后再继续。");
        }
        catch (Exception ex)
        {
            FooterStatusText.Text = "队列因异常暂停";
            AddLog(ex.Message, true);
            ShowError("队列已暂停", ex.Message);
        }
        finally
        {
            _running = false;
            CountdownText.Text = "00:00";
            CurrentRowText.Text = "-";
            RebuildExcelCloseTickets();
            await RefreshHistoryAsync(CancellationToken.None);
            RefreshStatistics();
            SetUiState();
        }
    }

    private async Task RunBusyActionAsync(Func<CancellationToken, Task> action)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _running = true;
        SetUiState();
        try
        {
            await action(_operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            AddLog("操作已取消");
        }
        catch (Exception ex)
        {
            AddLog(ex.Message, true);
            ShowError("操作失败", ex.Message);
        }
        finally
        {
            _running = false;
            SetUiState();
        }
    }

    private async Task EnsureSupportSessionAsync(CancellationToken cancellationToken)
    {
        if (_platformClient is null)
        {
            throw new InvalidOperationException("平台组件尚未初始化。");
        }

        var status = await _platformClient.CheckSessionAsync(cancellationToken);
        ApplySessionStatus(status);
        if (!status.IsSupportPlatformReady)
        {
            throw new PlatformSessionExpiredException(
                "技术支持系统尚未登录。请先在左侧完成登录并进入技术支持系统。");
        }
    }

    private async Task RefreshSessionStatusAsync()
    {
        if (_platformClient is null)
        {
            return;
        }

        var status = await _platformClient.CheckSessionAsync();
        ApplySessionStatus(status);
        AddLog(status.Message);
    }

    private void ApplySessionStatus(PlatformSessionStatus status)
    {
        SessionStatusText.Text = status.Message;
        SessionStatusText.Foreground = status.IsSupportPlatformReady
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.Gold;
        CurrentUserText.Text = string.IsNullOrWhiteSpace(status.DisplayName)
            ? string.Empty
            : $"当前用户：{status.DisplayName}";
    }

    private void PlatformClient_SessionExpired(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            SessionStatusText.Text = "登录已失效";
            SessionStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            _operationCancellation?.Cancel();
        });
    }

    private void Queue_LogEmitted(object? sender, SubmissionLogEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var row = e.ExcelRowNumber is null ? string.Empty : $" Excel行{e.ExcelRowNumber}";
            AddLog($"{row} {e.Message}".Trim(), e.IsError);
        });
    }

    private void Queue_ProgressChanged(object? sender, SubmissionProgressEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            TotalText.Text = e.Total.ToString();
            SucceededText.Text = e.Succeeded.ToString();
            PendingText.Text = e.Pending.ToString();
            FailedText.Text = e.Failed.ToString();
            ValidationFailedText.Text = e.ValidationFailed.ToString();
            PendingVerificationText.Text = e.PendingVerification.ToString();
            CurrentRowText.Text = e.CurrentExcelRowNumber?.ToString() ?? "-";
            SetUiState();
        });
    }

    private void Queue_CountdownChanged(object? sender, CountdownChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var seconds = Math.Max(0, (int)Math.Ceiling(e.Remaining.TotalSeconds));
            CountdownText.Text = $"{seconds / 60:00}:{seconds % 60:00}";
        });
    }

    private void CloseQueue_LogEmitted(object? sender, TicketCloseLogEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            AddLog($"技术支持编号 {e.CaseId} {e.Message}", e.IsError);
        });
    }

    private void CloseQueue_ProgressChanged(object? sender, TicketCloseProgressEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var current = string.IsNullOrWhiteSpace(e.CurrentCaseId) ? "-" : e.CurrentCaseId;
            var text =
                $"当前：{current}  成功 {e.Succeeded}  失败 {e.Failed}  " +
                $"待核验 {e.PendingVerification}  剩余 {e.Remaining}";
            if (_activeCloseSource == CloseQueueSource.ExcelSubmissionList)
            {
                ExcelCloseProgressText.Text = text;
                UpdateExcelCloseSelectionState();
            }
            else
            {
                CloseProgressText.Text = text;
                UpdatePendingSelectionState();
            }
        });
    }

    private void CloseQueue_CountdownChanged(object? sender, CountdownChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var seconds = Math.Max(0, (int)Math.Ceiling(e.Remaining.TotalSeconds));
            if (_activeCloseSource == CloseQueueSource.ExcelSubmissionList)
            {
                ExcelCloseCountdownText.Text = $"{seconds / 60:00}:{seconds % 60:00}";
            }
            else
            {
                CloseCountdownText.Text = $"{seconds / 60:00}:{seconds % 60:00}";
            }
        });
    }

    private void RefreshStatistics()
    {
        TotalText.Text = _rows.Count.ToString();
        SucceededText.Text = _rows.Count(row => row.State == SubmissionState.Succeeded).ToString();
        PendingText.Text = _rows.Count(row => row.State == SubmissionState.Pending).ToString();
        FailedText.Text = _rows.Count(row => row.State == SubmissionState.Failed).ToString();
        ValidationFailedText.Text =
            _rows.Count(row => row.State == SubmissionState.ValidationFailed).ToString();
        PendingVerificationText.Text =
            _rows.Count(row => row.State == SubmissionState.PendingVerification).ToString();
    }

    private void SetUiState()
    {
        var hasWorkbook = _workbook is not null;
        BrowseButton.IsEnabled = !_running;
        SubmissionIntervalText.IsEnabled = !_running;
        CloseIntervalText.IsEnabled = !_running;
        ExcelCloseIntervalText.IsEnabled = !_running;
        TogglePendingCustomReplyButton.IsEnabled = !_running;
        PendingCustomReplyPanel.IsEnabled = !_running;
        ToggleExcelCloseCustomReplyButton.IsEnabled = !_running;
        ExcelCloseCustomReplyPanel.IsEnabled = !_running;
        StartButton.IsEnabled =
            hasWorkbook && !_running && _queue is not null &&
            _rows.Any(row => row.State == SubmissionState.Pending);
        RetryButton.IsEnabled =
            hasWorkbook && !_running && _queue is not null &&
            _rows.Any(row => row.State == SubmissionState.Failed);
        QueryPendingButton.IsEnabled = !_running && _platformClient is not null;
        PendingTicketGrid.IsEnabled = !_running;
        SelectAllPendingCheckBox.IsEnabled =
            !_running && _pendingTickets.Any(ticket => ticket.CanClose);
        BulkCloseButton.IsEnabled =
            !_running && _closeQueue is not null &&
            _pendingTickets.Any(ticket => ticket.IsSelected && ticket.CanClose);
        RefreshExcelCloseButton.IsEnabled = !_running && hasWorkbook;
        ExcelCloseTicketGrid.IsEnabled = !_running;
        SelectAllExcelCloseCheckBox.IsEnabled =
            !_running && _excelCloseTickets.Any(ticket => ticket.CanClose);
        BulkCloseExcelButton.IsEnabled =
            !_running && _closeQueue is not null &&
            _excelCloseTickets.Any(ticket => ticket.IsSelected && ticket.CanClose);
        PauseButton.IsEnabled = _running && !_closing;
        StopButton.IsEnabled = _running && !_closing;
        ExportDiagnosticsButton.IsEnabled = !_running;
        PauseCloseButton.IsEnabled =
            _running && _closing && _activeCloseSource == CloseQueueSource.PendingQuery;
        StopCloseButton.IsEnabled =
            _running && _closing && _activeCloseSource == CloseQueueSource.PendingQuery;
        PauseCloseButton.Visibility = PauseCloseButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        StopCloseButton.Visibility = StopCloseButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        PauseExcelCloseButton.IsEnabled =
            _running && _closing && _activeCloseSource == CloseQueueSource.ExcelSubmissionList;
        StopExcelCloseButton.IsEnabled =
            _running && _closing && _activeCloseSource == CloseQueueSource.ExcelSubmissionList;
        PauseExcelCloseButton.Visibility = PauseExcelCloseButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        StopExcelCloseButton.Visibility = StopExcelCloseButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyStates();
    }

    private void UpdateEmptyStates()
    {
        TicketEmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingEmptyState.Visibility = _pendingTickets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExcelCloseEmptyState.Visibility = _excelCloseTickets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddLog(string message, bool isError = false)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {(isError ? "[错误] " : string.Empty)}{message}";
        LogList.Items.Add(line);
        LogList.ScrollIntoView(line);
        if (_logger is not null)
        {
            _ = _logger.WriteAsync(line);
        }
    }

    private DelaySchedule? TryGetSubmissionIntervalSchedule() =>
        TryReadOptionalSeconds(SubmissionIntervalText.Text, "提交间隔", DefaultSubmissionInterval);

    private DelaySchedule? TryGetCloseIntervalSchedule(CloseQueueSource source) =>
        TryReadOptionalSeconds(
            source == CloseQueueSource.ExcelSubmissionList
                ? ExcelCloseIntervalText.Text
                : CloseIntervalText.Text,
            "关贴间隔",
            DefaultCloseInterval);

    private DelaySchedule? TryReadOptionalSeconds(
        string text,
        string displayName,
        DelaySchedule defaultSchedule)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return defaultSchedule;
            }

            if (!int.TryParse(text.Trim(), out var seconds) || seconds < 0)
            {
                throw new InvalidOperationException($"{displayName}请填写 0 或正整数秒，或留空使用默认随机区间。");
            }

            return DelaySchedule.Fixed(TimeSpan.FromSeconds(seconds));
        }
        catch (InvalidOperationException ex)
        {
            ShowInfo("间隔设置无效", ex.Message);
            return null;
        }
    }

    private bool ShowConfirm(string title, string message)
    {
        var dialog = new CuteDialog(title, message, showCancel: true) { Owner = this };
        return dialog.ShowDialog() == true;
    }

    private void ShowInfo(string title, string message) => ShowNotice(title, message);

    private void ShowWarning(string title, string message, string? windowTitle = null) =>
        ShowNotice(windowTitle ?? title, message);

    private void ShowError(string title, string message) => ShowNotice(title, message);

    private void ShowNotice(string title, string message)
    {
        var dialog = new CuteDialog(title, message, showCancel: false) { Owner = this };
        dialog.ShowDialog();
    }

    private static string DescribeInterval(DelaySchedule schedule)
    {
        if (schedule.IsFixed)
        {
            return $"等待 {FormatSeconds(schedule.Minimum)}";
        }

        return $"随机等待 {FormatSeconds(schedule.Minimum)}-{FormatSeconds(schedule.Maximum)}";
    }

    private static string FormatSeconds(TimeSpan value) =>
        $"{Math.Ceiling(value.TotalSeconds):0} 秒";

    private static void OpenDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }
}
