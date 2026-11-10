using Avalonia.Controls;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;
using SvnWorkspaceManager.Core;
using SvnWorkspaceManager.Core.Configuration;
using SvnWorkspaceManager.Core.FileSystem;
using SvnWorkspaceManager.Core.Logging;
using SvnWorkspaceManager.Core.Models;
using SvnWorkspaceManager.Core.Process;
using SvnWorkspaceManager.Core.Svn;

namespace SvnWorkspaceManager.Gui;

public partial class MainWindow : Window
{
    private readonly WorkspaceStore _store;
    private readonly ProjectGuiSession? _guiSession;
    private readonly WorkspaceLogger _logger;
    private readonly DispatcherTimer _busyTimer;
    private WorkspaceService _service;
    private WorkspaceConfiguration _configuration;
    private bool _busy;
    private bool _refreshing;
    private string _busyAction = string.Empty;
    private DateTimeOffset _busySince;
    private DateTimeOffset _lastBusyLog;
    private string _lastProgress = string.Empty;
    private CancellationTokenSource? _operationCancellation;
    private IReadOnlyDictionary<string, TaskMappingAssessment> _taskAssessments = new Dictionary<string, TaskMappingAssessment>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<IsolationDirectorySnapshot> _isolationDirectories = [];
    private RestoreAssessment? _restoreAssessment;
    private bool _initialized;
    private bool _mappingInconsistent;

    public MainWindow() : this(null) { }

    public MainWindow(string? projectRoot)
    {
        InitializeComponent();
        var applicationDirectory = Environment.GetEnvironmentVariable("SVN_WORKSPACE_MANAGER_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Azure", "SvnWorkspaceManager");
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var context = ProjectLocator.CreateContext(projectRoot, applicationDirectory);
            if (!File.Exists(context.ConfigurationPath)) ProjectLocator.TryMigrateLegacy(applicationDirectory, context);
            if (!File.Exists(context.ConfigurationPath))
                throw new InvalidOperationException($"项目尚未导入：{context.ProjectRoot}。请先完成项目导入后再打开。");
            _store = new WorkspaceStore(context.ApplicationDirectory);
        }
        else
        {
            _store = new WorkspaceStore(applicationDirectory);
        }
        _configuration = _store.LoadConfiguration();
        var loadedContext = ProjectLocator.FromConfiguration(_configuration, applicationDirectory);
        var registry = new ProjectRegistry(applicationDirectory);
        registry.EnsureNoConflict(_configuration);
        if (!ProjectGuiSession.TryAcquire(ProjectIdentity.FromRoot(_configuration.ProjectRoot), out _guiSession))
        {
            MessageBox(IntPtr.Zero, $"项目已经在另一个 GUI 窗口中打开：{_configuration.ProjectRoot}", "SVN 工作区管理器", 0x00000040);
            throw new InvalidOperationException($"项目已被其他 GUI 窗口打开：{_configuration.ProjectRoot}");
        }
        registry.Register(loadedContext);
        _logger = new WorkspaceLogger(Path.Combine(applicationDirectory, "logs"));
        _logger.Session($"GUI 启动：pid={Environment.ProcessId}，日志={_logger.LogPath}");
        _service = CreateService(_configuration, _logger);
        _busyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _busyTimer.Tick += (_, _) => UpdateBusyIndicator();
        _ = RefreshStatusAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _guiSession?.Dispose();
        base.OnClosed(e);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private async Task RefreshStatusAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        BusyActionText.Text = "正在刷新工作区状态";
        BusyProgressText.Text = "正在读取 SVN 工作副本，请稍候…";
        BusyElapsedText.Text = "刷新过程中暂时无法操作窗口";
        BusyCancelButton.IsVisible = false;
        BusyCancelButton.IsEnabled = false;
        BusyOverlay.IsVisible = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await RefreshStatusCoreAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.Error("刷新状态超时（2 分钟），已终止未完成的 SVN 查询。");
            SetMessage("刷新状态超时（2 分钟）。请检查 VPN 或 SVN 服务器后点击“刷新状态”重试。", true);
        }
        finally
        {
            _refreshing = false;
            if (!_busy)
                BusyOverlay.IsVisible = false;
            UpdateActionAvailabilitySafe();
        }
    }

    private async Task RefreshStatusCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
        var configuration = _store.LoadConfiguration();
        var state = _store.LoadState();
        ProjectRootBox.Text = configuration.ProjectRoot;
        TaskRootBox.Text = configuration.TaskRoot;
        SvnUrlBox.Text = configuration.SvnTrunkUrl;
        IsolatedDirectoriesBox.Text = string.Join(Environment.NewLine, configuration.IsolatedDirectories);
        ProjectPathText.Text = $"项目根目录：{configuration.ProjectRoot}";
        CurrentTaskText.Text = $"当前任务：{state.ActiveTask ?? "无"}";
        WorkspacePathText.Text = "目录映射：检查中…";
        PublicStatusText.Text = "项目 SVN：正在检查…";
        SetValidity(ProjectRootBox, Directory.Exists(configuration.ProjectRoot));
        SetValidity(TaskRootBox, Directory.Exists(configuration.TaskRoot));
        SetValidity(SvnUrlBox, !string.IsNullOrWhiteSpace(configuration.SvnTrunkUrl));
        SetValidity(IsolatedDirectoriesBox, configuration.IsolatedDirectories.Count > 0 && configuration.IsolatedDirectories.All(IsSafeRelativePath));
        ModeText.Text = state.Mode == WorkspaceMode.Normal ? "普通模式" : $"目录映射：{state.ActiveTask}";
        var tasks = _store.ListTasks().OrderBy(task => task.Name).ToList();
        var taskAssessments = new Dictionary<string, TaskMappingAssessment>(StringComparer.OrdinalIgnoreCase);
        var checks = tasks.Select(async task =>
        {
            try
            {
                return (task.Name, Assessment: await _service.GetTaskMappingAssessmentAsync(task.Name, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return (task.Name, Assessment: new TaskMappingAssessment(task.Name, [], $"无法检查任务 revision：{exception.Message}"));
            }
        });
        foreach (var check in await Task.WhenAll(checks)) taskAssessments[check.Name] = check.Assessment;
        _taskAssessments = taskAssessments;
        var previousSelection = SelectedTaskName();
        var activeTask = state.ActiveTask is null
            ? null
            : _store.ListTasks().FirstOrDefault(task => string.Equals(task.Name, state.ActiveTask, StringComparison.OrdinalIgnoreCase));
        WorkspacePathText.Text = state.Mode == WorkspaceMode.TaskMapping
            ? $"目录映射：{activeTask?.Name ?? state.ActiveTask ?? "未知任务"}"
            : "目录映射：未启用";
        ToolTip.SetTip(WorkspacePathText, state.Mode == WorkspaceMode.TaskMapping
            ? activeTask?.RootPath ?? configuration.ProjectRoot
            : configuration.ProjectRoot);
        var snapshot = await _service.GetStatusAsync(cancellationToken);
        var assessment = snapshot.RestoreAssessment;
        _restoreAssessment = assessment;
        _isolationDirectories = snapshot.IsolationDirectories;
        _mappingInconsistent = HasInconsistentMapping(state, _isolationDirectories);
        PublicStatusText.Text = snapshot.ProjectStatus.Succeeded ? "项目 SVN：正常" : "项目 SVN：读取失败";

        ToolTip.SetTip(UpdateButton, "更新项目 SVN；任务映射模式下同时更新当前任务");
        ToolTip.SetTip(PublicStatusText, snapshot.ProjectStatus.Succeeded
            ? "项目根目录的 SVN 工作副本状态读取成功。"
            : snapshot.ProjectStatus.StandardError.Trim());
        var parentStatus = GetParentWorkspaceStatus();
        var taskItems = TaskListOrdering.OrderForDisplay(
                tasks.Select(task => new TaskListItem(task, IsTaskValid(task, configuration), taskAssessments[task.Name], state.ActiveTask, parentStatus, state.Mode)
                {
                    CanSync = state.Mode == WorkspaceMode.TaskMapping
                        && string.Equals(task.Name, state.ActiveTask, StringComparison.OrdinalIgnoreCase)
                        && assessment.IsReady && state.RestoreOperation is null,
                    CanExitMapping = state.Mode == WorkspaceMode.TaskMapping
                        && string.Equals(task.Name, state.ActiveTask, StringComparison.OrdinalIgnoreCase)
                        && !_mappingInconsistent && state.RestoreOperation is null,
                    SyncToolTip = state.Mode == WorkspaceMode.TaskMapping
                        && string.Equals(task.Name, state.ActiveTask, StringComparison.OrdinalIgnoreCase)
                        && assessment.IsReady && state.RestoreOperation is null
                        ? "退出映射并同步目录"
                        : $"退出映射并同步目录：{(state.RestoreOperation is not null ? "请先继续未完成的操作" : assessment.Summary)}"
                }),
                item => item.Status,
                item => item.Name)
            .ToList();
        TaskList.ItemsSource = taskItems;
        TaskList.SelectedItem = !_initialized
            ? taskItems.FirstOrDefault(item => string.Equals(item.Name, state.ActiveTask, StringComparison.OrdinalIgnoreCase))
            : taskItems.FirstOrDefault(item => string.Equals(item.Name, previousSelection, StringComparison.OrdinalIgnoreCase));
        _initialized = true;
        RenderIsolationDirectories(state);
        UpdateActionAvailability();
        var interrupted = state.RestoreOperation;
        RestoreButton.IsEnabled = assessment.IsReady || interrupted is not null;
        RestoreButton.IsVisible = interrupted is not null;
        RestoreButton.Content = interrupted is null
            ? "退出映射并同步目录"
            : interrupted.Kind switch
            {
                WorkspaceOperationKinds.Exit => "继续退出任务映射",
                WorkspaceOperationKinds.RollbackMapping => "继续回滚进入操作",
                _ => "继续恢复到原目录"
            };
        RestoreStatusText.IsVisible = interrupted is not null || (state.Mode == WorkspaceMode.TaskMapping && !assessment.IsReady);
        RestoreStatusText.Text = interrupted is not null
            ? $"上次操作未完成：{interrupted.Phase}"
            : assessment.Summary;
        ToolTip.SetTip(RestoreButton, interrupted is not null
            ? interrupted.Error ?? "继续上次未完成的操作。"
            : assessment.IsReady
                ? "将任务内容同步回项目目录并退出映射，任务工作区保留。"
                : assessment.Summary);
        if (MessageTitle.Text != "操作失败")
            SetMessage(state.Mode == WorkspaceMode.Normal && interrupted is null ? "状态已刷新。" : $"状态已刷新：{assessment.Summary}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error($"刷新状态失败：{exception}");
            RestoreButton.IsEnabled = false;
            RestoreButton.IsVisible = false;
            RestoreStatusText.Text = "! 状态检查失败";
            RestoreStatusText.IsVisible = true;
            SetMessage(exception.Message, true);
        }
    }

    private async void RefreshStatusClick(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            SetMessage("当前操作尚未结束，请等待完成后再刷新。");
            return;
        }
        if (_refreshing)
        {
            SetMessage("正在刷新状态，请稍候…");
            return;
        }
        await RefreshStatusAsync();
    }

    private void TaskListSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateActionAvailability();

    private void ProjectPickerClick(object? sender, RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var picker = new ProjectPickerWindow(desktop, new ProjectRegistry(ProjectLocator.DefaultApplicationDirectory).List());
        picker.Show(this);
    }

    private void MainWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || TaskList.SelectedItem is null) return;
        TaskList.SelectedItem = null;
        e.Handled = true;
    }

    private void TaskListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TaskList.SelectedItem is null || e.Source is not Control control) return;
        if (control.FindAncestorOfType<ListBoxItem>() is not null) return;
        TaskList.SelectedItem = null;
        e.Handled = true;
    }

    private async void NewTaskClick(object? sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { Text = $"TASK-{DateTime.Now:yyyyMMdd-HHmm}", Watermark = "任务名称，例如 TASK-101" };
        var dialog = new Window
        {
            Title = "新建任务工作区",
            Width = 560,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var create = new Button { Content = "创建任务工作区", IsDefault = true };
        create.Classes.Add("primary");
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "创建独立 SVN 任务工作区", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "仅从 trunk 检出一个新任务，不会设置映射，也不会改变当前原目录。", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#637083")) },
                nameBox,
                buttons
            }
        };
        cancel.Click += (_, _) => dialog.Close();
        create.Click += (_, _) => dialog.Close("create");
        var result = await dialog.ShowDialog<string?>(this);
        var name = nameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) { SetMessage("请输入任务名称。", true); return; }
        if (result == "create") await CreateTaskAsync(name);
    }

    private async void MigrateCurrentChangesClick(object? sender, RoutedEventArgs e)
    {
        if (_store.LoadState().Mode != WorkspaceMode.Normal)
        {
            SetMessage("当前已经处于任务映射模式，无法再从当前修改创建任务。", true);
            return;
        }

        var nameBox = new TextBox { Text = $"TASK-{DateTime.Now:yyyyMMdd-HHmm}", Watermark = "任务名称" };
        var dialog = new Window
        {
            Title = "从当前修改进入任务映射",
            Width = 560,
            Height = 270,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var confirm = new Button { Content = "迁移修改并进入映射", IsDefault = true };
        confirm.Classes.Add("primary");
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "从当前原目录创建任务", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "会把任务映射目录中的修改、未版本化和忽略文件迁移到新任务，并立即进入任务映射模式。", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#637083")) },
                nameBox,
                buttons
            }
        };
        cancel.Click += (_, _) => dialog.Close();
        confirm.Click += (_, _) => dialog.Close("migrate");
        var result = await dialog.ShowDialog<string?>(this);
        var name = nameBox.Text?.Trim();
        if (result == "migrate" && !string.IsNullOrWhiteSpace(name)) await CreateTaskFromChangesAsync(name);
    }

    private async void UpdateClick(object? sender, RoutedEventArgs e) => await RunActionAsync("更新工作区", token => _service.UpdateAsync(token));

    private async Task CreateTaskAsync(string task)
    {
        await RunActionAsync($"新建任务 {task}", async token =>
        {
            await _service.CreateTaskAsync(task, token);
        });
    }

    private async Task CreateTaskFromChangesAsync(string task)
    {
        SetMessage($"正在从当前修改创建任务 {task}，请稍候…");
        await RunActionAsync($"从当前修改创建任务 {task}", async token =>
        {
            await _service.CreateTaskFromCurrentChangesAsync(task, token);
        });
    }

    private async void RepairWorkingCopyClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("修复父级工作副本", "将清理 SVN 工作队列，并只对处于中断排除状态（Schedule: delete）的目录执行本地 revert。未版本化和忽略文件会保留；已跟踪修改或普通冲突不会被自动覆盖。是否继续？", "开始修复")) return;
        await RunActionAsync("修复父级工作副本", token => _service.RepairIsolatedDirectoriesAsync(token));
    }

    private async void TaskItemUseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string task } || string.IsNullOrWhiteSpace(task)) return;
        var item = TaskList.ItemsSource?.OfType<TaskListItem>()
            .FirstOrDefault(candidate => candidate.Name.Equals(task, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;

        switch (item.Status)
        {
            case TaskListStatus.NeedsUpdate:
                await RunActionAsync($"更新任务 {task}", token => _service.UpdateTaskAsync(task, token));
                return;
            case TaskListStatus.NeedsConflict:
                TaskList.SelectedItem = item;
                SetMessage($"任务“{task}”存在 SVN 冲突或阻断状态，更新命令已保留冲突内容，不会自动覆盖。{Environment.NewLine}{item.AssessmentDetails}{Environment.NewLine}请先在 SVN 工具中解决冲突并清理工作副本，再点击“刷新状态”。", true);
                return;
            case TaskListStatus.ParentBlocked:
            {
                TaskList.SelectedItem = item;
                var blocked = _isolationDirectories.Where(directory => !directory.IsClean).ToList();
                var description = blocked.Count == 0
                    ? "父级任务映射目录状态尚未读取完成。"
                    : string.Join("；", blocked.Select(DescribeIsolationProblem));
                var hint = blocked.Any(directory => !directory.Exists || directory.IsJunction || directory.Error is not null)
                    ? "可在“当前工作区”卡片点击“修复父级工作副本”。"
                    : "处理完父目录的修改或冲突后，点击右侧进入图标即可进入映射。";
                SetMessage($"任务“{task}”本身已经就绪，不需要更新。{Environment.NewLine}父目录需要先清理：{description}{Environment.NewLine}{hint}");
                return;
            }
            case TaskListStatus.NeedsMigration:
                if (!await ConfirmAsync("确认迁移本地文件", $"任务映射目录中有未版本化或忽略内容。进入映射前会把这些内容迁移到任务“{task}”，随后原目录将映射到该任务。是否继续？", "迁移并进入映射")) return;
                await RunActionAsync($"迁移本地文件并进入映射 {task}", token => _service.EnableTaskMappingWithUnversionedChangesAsync(task, token));
                return;
            case TaskListStatus.Ready:
                var state = _store.LoadState();
                if (state.Mode == WorkspaceMode.TaskMapping
                    && !task.Equals(state.ActiveTask, StringComparison.OrdinalIgnoreCase))
                {
                    if (!await ConfirmAsync(
                            "确认切换任务",
                            $"当前任务“{state.ActiveTask}”正在映射。切换到任务“{task}”后，原项目路径会改为使用该任务的代码，当前任务的工作区会保留。是否继续？",
                            "确认切换")) return;
                }
                await RunActionAsync(
                    state.Mode == WorkspaceMode.TaskMapping ? $"切换到任务 {task}" : $"进入任务映射 {task}",
                    token => state.Mode == WorkspaceMode.TaskMapping
                        ? _service.SwitchTaskAsync(task, token)
                        : _service.EnableTaskMappingAsync(task, token));
                return;
            default:
                TaskList.SelectedItem = item;
                SetMessage(item.UseToolTip, item.Status == TaskListStatus.Incomplete);
                return;
        }
    }

    private async void TaskItemDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string task } || string.IsNullOrWhiteSpace(task)) return;
        var state = _store.LoadState();
        if (state.Mode == WorkspaceMode.TaskMapping
            && task.Equals(state.ActiveTask, StringComparison.OrdinalIgnoreCase))
        {
            SetMessage("当前任务正在映射，不能直接删除。请先退出任务映射后再删除。", true);
            return;
        }

        await DeleteTaskAsync(task);
    }

    private async Task RemoveTaskRecordAsync(string task)
    {
        if (!await ConfirmAsync("确认移除任务记录", $"将从列表移除任务“{task}”的记录，但不会删除磁盘上的任务文件。是否继续？", "确认移除")) return;
        await RunActionAsync($"移除任务记录 {task}", _ => _service.RemoveTaskRecordAsync(task));
    }

    private async Task DeleteTaskAsync(string task)
    {
        if (!await ConfirmAsync("确认删除任务", $"将删除任务“{task}”及其全部文件。任务目录会移入 Windows 回收站，可从回收站恢复。是否继续？", "确认删除")) return;
        await RunActionAsync($"删除任务 {task}", _ => _service.DeleteTaskToRecycleBinAsync(task));
    }

    private void OpenTaskFolder(string task)
    {
        var metadata = _store.ListTasks().FirstOrDefault(item => string.Equals(item.Name, task, StringComparison.OrdinalIgnoreCase));
        if (metadata is null || !Directory.Exists(metadata.RootPath))
        {
            SetMessage("任务目录不存在。", true);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{metadata.RootPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            SetMessage($"无法打开任务目录：{exception.Message}", true);
        }
    }

    private void TaskItemMoreClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string task } anchor || string.IsNullOrWhiteSpace(task)) return;
        var state = _store.LoadState();
        var metadata = _store.ListTasks().FirstOrDefault(item => item.Name.Equals(task, StringComparison.OrdinalIgnoreCase));
        var isActive = state.Mode == WorkspaceMode.TaskMapping
            && task.Equals(state.ActiveTask, StringComparison.OrdinalIgnoreCase);
        var flyout = new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight
        };

        if (metadata is not null && Directory.Exists(metadata.RootPath))
        {
            flyout.Items.Add(CreateTaskMenuItem("\uE8B7", "打开任务目录", () =>
            {
                OpenTaskFolder(task);
                return Task.CompletedTask;
            }));

            if (!isActive)
                flyout.Items.Add(CreateTaskMenuItem("\uE72C", "更新到 trunk", () =>
                    RunActionAsync($"更新任务 {task}", token => _service.UpdateTaskAsync(task, token))));
        }
        if (isActive)
        {
            flyout.Items.Add(CreateTaskMenuItem("\uE8AC", "退出映射", ExitTaskMappingAsync));
            var syncItem = CreateTaskMenuItem("\uE8B5", "退出映射并同步目录", RestoreCurrentTaskAsync);
            syncItem.IsEnabled = !_busy && !_refreshing && state.RestoreOperation is null && _restoreAssessment?.IsReady == true;
            ToolTip.SetTip(syncItem, _restoreAssessment?.IsReady == true
                ? "将当前任务内容同步回项目目录，任务工作区保留。"
                : _restoreAssessment?.Summary ?? "正在检查同步条件。");
            ToolTip.SetShowOnDisabled(syncItem, true);
            flyout.Items.Add(syncItem);
        }

        if (!isActive)
        {
            if (metadata is not null && Directory.Exists(metadata.RootPath))
                flyout.Items.Add(new Separator());
            flyout.Items.Add(CreateTaskMenuItem("\uE74D", "删除任务", () => DeleteTaskAsync(task), danger: true));
            flyout.Items.Add(CreateTaskMenuItem("\uE711", "移除任务记录", () => RemoveTaskRecordAsync(task), danger: true));
        }

        flyout.ShowAt(anchor);
    }

    private MenuItem CreateTaskMenuItem(string glyph, string text, Func<Task> action, bool danger = false)
    {
        var item = new MenuItem
        {
            Header = text,
            Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse(danger ? "#B42318" : "#5A6B84"))
            },
            MinWidth = 196,
            Padding = new Avalonia.Thickness(10, 6),
            FontSize = 12.5
        };
        if (danger) item.Foreground = new SolidColorBrush(Color.Parse("#B42318"));
        item.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                _logger.Error($"任务菜单操作失败：{text}{Environment.NewLine}{exception}");
                SetMessage(exception.Message, true);
            }
        };
        return item;
    }

    private void UpdateActionAvailability()
    {
        var state = _store.LoadState();
        var selected = SelectedTaskName();
        var hasSelection = !string.IsNullOrWhiteSpace(selected);
        var isMapping = state.Mode == WorkspaceMode.TaskMapping;
        var isActive = hasSelection && string.Equals(selected, state.ActiveTask, StringComparison.OrdinalIgnoreCase);
        var selectedAssessment = hasSelection && selected is not null && _taskAssessments.TryGetValue(selected, out var assessment)
            ? assessment
            : null;

        var parentClean = _isolationDirectories.Count > 0 && _isolationDirectories.All(directory => directory.IsClean);
        var damaged = _isolationDirectories.Any(directory => directory.Error is not null);
        var hasMigratableChanges = _isolationDirectories.Any(directory => directory.Exists && directory.TotalChanges > 0);
        MigrateCurrentButton.IsVisible = !_busy && !isMapping && hasMigratableChanges && !damaged;
        MigrateCurrentButton.IsEnabled = !_busy;
        RepairWorkingCopyButton.IsVisible = !_busy && !isMapping && damaged;
        RepairWorkingCopyButton.IsEnabled = !_busy;
        NewTaskButton.IsEnabled = !_busy;
        RefreshButton.IsEnabled = !_busy && !_refreshing;
        UpdateButton.IsEnabled = !_busy && !_refreshing;
        SelectionHintText.Text = _busy ? "正在执行操作，请稍候…" : !hasSelection
            ? "悬停在图标上可查看操作说明"
            : isActive ? "当前任务已映射；可从 ⋯ 退出映射"
            : selectedAssessment is not null ? $"{selectedAssessment.Summary} 用右侧图标进入映射，⋯ 查看更多操作。"
            : "正在检查任务版本…";

        UpdateReadiness(state, selected, selectedAssessment, isMapping);
    }

    private ParentWorkspaceStatus GetParentWorkspaceStatus()
    {
        if (_isolationDirectories.Count == 0) return ParentWorkspaceStatus.Blocked;
        if (_isolationDirectories.Any(directory =>
                !directory.Exists || directory.IsJunction || directory.Error is not null || directory.TrackedChanges > 0))
            return ParentWorkspaceStatus.Blocked;
        return _isolationDirectories.Any(directory => directory.UnversionedEntries > 0)
            ? ParentWorkspaceStatus.UnversionedOnly
            : ParentWorkspaceStatus.Clean;
    }

    private void UpdateReadiness(WorkspaceState state, string? selected, TaskMappingAssessment? selectedAssessment, bool isMapping)
    {
        ReadinessPanel.IsVisible = true;
        if (isMapping)
        {
            WorkspaceStateText.Text = _mappingInconsistent
                ? "映射异常"
                : string.IsNullOrWhiteSpace(state.ActiveTask) ? "目录映射已启用" : $"目录映射 · {state.ActiveTask}";
            WorkspaceStateBadge.Background = new SolidColorBrush(Color.Parse(_mappingInconsistent ? "#FFE1E1" : "#DCEBFF"));
            WorkspaceStateText.Foreground = new SolidColorBrush(Color.Parse(_mappingInconsistent ? "#B42318" : "#1557A6"));
            ToolTip.SetTip(WorkspaceStateText, _mappingInconsistent
                ? "保存的活动任务与项目目录中的实际 junction 不一致，请先刷新或修复目录映射。"
                : "项目目录中的任务映射目录当前通过 junction 指向活动任务。 ");
            return;
        }

        var problems = _isolationDirectories.Where(directory => !directory.IsClean).ToList();
        var parentStatus = GetParentWorkspaceStatus();
        var parentClean = parentStatus == ParentWorkspaceStatus.Clean;
        var parentBlocked = parentStatus == ParentWorkspaceStatus.Blocked;
        var damaged = problems.Any(directory => directory.Error is not null);
        var parentDamaged = _isolationDirectories.Any(directory => !directory.Exists || directory.IsJunction || directory.Error is not null);
        var parentSummary =
            _isolationDirectories.Count == 0
                ? "父目录：未配置"
                : parentClean
                    ? "父目录：干净"
                    : parentDamaged
                        ? "父目录：状态异常"
                        : parentBlocked
                            ? "父目录：有已跟踪修改"
                            : "父目录：有待迁移文件";
        SetCheck(ParentCheckText, parentSummary, parentClean ? "#237A3B" : parentDamaged ? "#936400" : "#2C7A4B");
        ToolTip.SetTip(ParentCheckText, _isolationDirectories.Count == 0
            ? "尚未配置任务映射目录。"
            : parentClean
                ? string.Join("、", _isolationDirectories.Select(directory => $"{directory.RelativePath} r{directory.Revision}"))
                : string.Join("；", problems.Select(DescribeIsolationProblem)));

        var hasSelection = !string.IsNullOrWhiteSpace(selected);
        var taskReady = selectedAssessment?.IsReady == true;
        var taskSummary =
            !hasSelection
                ? "任务：请先选择"
                : taskReady
                    ? "任务：已就绪"
                    : selectedAssessment?.Directories.Any(directory => directory.HasBlockingStatus) == true
                        ? "任务：需处理冲突"
                        : "任务：需更新";
        SetCheck(TaskCheckText, taskSummary, taskReady ? "#237A3B" : hasSelection ? "#936400" : "#637083");
        ToolTip.SetTip(TaskCheckText, !hasSelection
            ? "选择任务后查看详细映射资格。"
            : string.Join(Environment.NewLine, selectedAssessment?.Directories.Select(directory => $"{directory.RelativePath}：{directory.Reason}") ?? []));

        var ready = !parentBlocked && taskReady;
        ActionHintText.Text = _busy
            ? "正在执行操作…"
            : damaged
                ? "下一步：修复父目录"
            : !hasSelection
                ? "下一步：选择任务"
            : !taskReady
                ? "下一步：更新任务"
            : parentBlocked
                ? "下一步：处理父目录"
            : !parentClean
                ? "下一步：迁移文件"
            : "下一步：进入映射";
        ActionHintText.Foreground = new SolidColorBrush(Color.Parse(parentBlocked ? "#2C7A4B" : ready ? "#237A3B" : "#637083"));

        WorkspaceStateText.Text = _busy
            ? "执行中…"
            : _mappingInconsistent
                ? "映射异常"
            : parentClean ? "● 普通目录"
            : parentDamaged ? "⚠ 目录状态异常"
            : _isolationDirectories.Count == 0 ? "未配置目录"
            : "普通目录 · 有本地修改";
        WorkspaceStateBadge.Background = new SolidColorBrush(Color.Parse(
            _mappingInconsistent ? "#FFE1E1" : parentClean ? "#DFF5E3" : "#FFF4D6"));
        WorkspaceStateText.Foreground = new SolidColorBrush(Color.Parse(
            _mappingInconsistent ? "#B42318" : parentClean ? "#237A3B" : "#936400"));
        ToolTip.SetTip(WorkspaceStateText, _mappingInconsistent
            ? "普通模式下仍发现 junction，或实际目录映射与保存状态不一致。"
            : "当前项目目录没有启用任务 junction。 ");
    }

    private void RenderIsolationDirectories(WorkspaceState state)
    {
        DirectoryHintText.IsVisible = _mappingInconsistent;
        if (state.Mode == WorkspaceMode.TaskMapping)
        {
            DirectoryStatusList.ItemsSource = _isolationDirectories.Select(DirectoryStatusItem.FromNormal).ToList();
            DirectoryHintText.Text = _mappingInconsistent ? "发现目录映射异常，请检查下面的实际目标" : "当前每个项目目录的实际 junction 目标";
            return;
        }

        DirectoryStatusList.ItemsSource = _isolationDirectories.Select(DirectoryStatusItem.FromNormal).ToList();
        DirectoryHintText.Text = _mappingInconsistent ? "发现目录映射异常，请检查下面的实际目标" : "当前每个项目目录的实际状态";
    }

    private static bool HasInconsistentMapping(WorkspaceState state, IReadOnlyList<IsolationDirectorySnapshot> directories)
    {
        if (directories.Count == 0) return false;
        var linked = directories.Where(directory => directory.IsJunction).ToList();
        if (state.Mode == WorkspaceMode.Normal) return linked.Count > 0;
        if (string.IsNullOrWhiteSpace(state.ActiveTask) || linked.Count != directories.Count) return true;
        return linked.Any(directory => !TargetBelongsToTask(directory.LinkTarget, state.ActiveTask));
    }

    private static bool TargetBelongsToTask(string? targetPath, string taskName)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return false;
        var normalized = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar);
        var marker = Path.DirectorySeparatorChar + taskName + Path.DirectorySeparatorChar;
        return normalized.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetCheck(TextBlock block, string text, string color)
    {
        block.Text = text;
        block.Foreground = new SolidColorBrush(Color.Parse(color));
    }

    private static string DescribeIsolationProblem(IsolationDirectorySnapshot directory)
    {
        if (!directory.Exists) return $"{directory.RelativePath} 目录不存在";
        if (directory.Error is not null) return $"{directory.RelativePath} {directory.Error}";
        if (directory.IsJunction) return $"{directory.RelativePath} 仍是 junction";
        if (directory.TrackedChanges > 0 && directory.UnversionedEntries > 0)
            return $"{directory.RelativePath} 已跟踪 {directory.TrackedChanges} 项、未版本化或忽略 {directory.UnversionedEntries} 项";
        if (directory.TrackedChanges > 0) return $"{directory.RelativePath} 已跟踪修改 {directory.TrackedChanges} 项";
        return $"{directory.RelativePath} 未版本化或忽略 {directory.UnversionedEntries} 项";
    }

    private void SetMessage(string text, bool isError = false)
    {
        StatusText.Text = text;
        MessageCard.Background = new SolidColorBrush(Color.Parse(isError ? "#FFF5F5" : "#FFFFFF"));
        MessageCard.BorderBrush = new SolidColorBrush(Color.Parse(isError ? "#E05B5B" : "#D9DEE7"));
        MessageCard.BorderThickness = new Avalonia.Thickness(isError ? 2 : 1);
        MessageTitle.Text = isError ? "操作失败" : "消息";
        MessageTitle.Foreground = new SolidColorBrush(Color.Parse(isError ? "#B42318" : "#637083"));
        StatusText.Foreground = new SolidColorBrush(Color.Parse(isError ? "#8A1C13" : "#20242B"));
    }

    private async Task ExitTaskMappingAsync()
    {
        if (!await ConfirmAsync("确认退出任务映射", "将移除原目录中的 junction，并把每个任务映射目录恢复到其直接父目录当前的 SVN revision。任务内容不会复制回原目录，任务工作区会保留。是否继续？", "确认退出任务映射")) return;
        await RunActionAsync("退出任务映射", token => _service.ExitTaskMappingAsync(token));
    }

    private async void ExitMappingClick(object? sender, RoutedEventArgs e) => await ExitTaskMappingAsync();

    private async void RestoreCurrentTaskClick(object? sender, RoutedEventArgs e) => await RestoreCurrentTaskAsync();

    private async void TaskItemSyncClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _refreshing || sender is not Button { Tag: string task }) return;
        var state = _store.LoadState();
        if (state.Mode != WorkspaceMode.TaskMapping ||
            !string.Equals(task, state.ActiveTask, StringComparison.OrdinalIgnoreCase) ||
            state.RestoreOperation is not null || _restoreAssessment?.IsReady != true) return;
        await RestoreCurrentTaskAsync();
    }

    private async void TaskItemExitMappingClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _refreshing || sender is not Button { Tag: string task }) return;
        var state = _store.LoadState();
        if (state.Mode != WorkspaceMode.TaskMapping ||
            !string.Equals(task, state.ActiveTask, StringComparison.OrdinalIgnoreCase) ||
            state.RestoreOperation is not null || _mappingInconsistent) return;
        await ExitTaskMappingAsync();
    }

    private async Task RestoreCurrentTaskAsync()
    {
        var state = _store.LoadState();
        if (state.RestoreOperation is not null)
        {
            if (!await ConfirmAsync("继续未完成的操作", $"上次操作在“{state.RestoreOperation.Phase}”阶段中断。现在继续执行吗？", "继续")) return;
            await RunActionAsync("继续未完成的操作", token => _service.ResumeInterruptedOperationAsync(token));
            return;
        }
        if (!await ConfirmAsync("确认退出映射并同步目录", "将把任务内容（包括未版本化文件）同步到原目录，并把每个父目录对齐到任务 revision，然后退出任务映射模式。任务工作区会保留。是否继续？", "确认同步")) return;
        await RunActionAsync("恢复到原目录", token => _service.RestoreCurrentTaskToProjectAsync(token));
    }

    private async Task RunActionAsync(string actionName, Func<CancellationToken, Task> action)
    {
        if (_busy)
        {
            var seconds = (int)(DateTimeOffset.Now - _busySince).TotalSeconds;
            SetMessage($"当前操作尚未结束：{_busyAction}（{seconds}s）。可点击“取消操作”中止后重试。");
            return;
        }
        if (_refreshing)
        {
            SetMessage("正在刷新状态，请稍候再操作。");
            return;
        }

        _busy = true;
        _busyAction = actionName;
        _busySince = DateTimeOffset.Now;
        _lastBusyLog = _busySince;
        _lastProgress = string.Empty;
        var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _busyTimer.Start();
        CancelOperationButton.IsVisible = true;
        CancelOperationButton.IsEnabled = true;
        BusyActionText.Text = actionName;
        BusyProgressText.Text = "正在准备…";
        BusyElapsedText.Text = "已用时 0 秒";
        BusyCancelButton.IsEnabled = true;
        BusyOverlay.IsVisible = true;
        SetMessage($"正在执行：{actionName}…");
        UpdateActionAvailabilitySafe();
        try
        {
            using var operationLock = ProjectOperationLock.Acquire(ProjectIdentity.FromRoot(_configuration.ProjectRoot), TimeSpan.FromSeconds(5));
            _logger.Session($"GUI 操作开始：{actionName}");
            await action(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.Error($"GUI 操作已取消：{actionName}");
            SetMessage($"操作已取消：{actionName}。如果 SVN 工作副本仍有未完成操作，请点击“修复父级工作副本”或执行 svn cleanup。", true);
        }
        catch (Exception exception)
        {
            _logger.Error($"GUI 操作失败：{actionName}{Environment.NewLine}{exception}");
            SetMessage(exception.Message, true);
        }
        finally
        {
            var elapsed = DateTimeOffset.Now - _busySince;
            _busy = false;
            _busyAction = string.Empty;
            _busyTimer.Stop();
            CancelOperationButton.IsVisible = false;
            BusyOverlay.IsVisible = false;
            BusyCancelButton.IsEnabled = false;
            _operationCancellation = null;
            cancellation.Dispose();
            _logger.Info($"GUI 操作结束：{actionName}，耗时 {elapsed.TotalSeconds:0.0} 秒");
            UpdateActionAvailabilitySafe();
        }

        await RefreshStatusAsync();
    }

    private async void CancelOperationClick(object? sender, RoutedEventArgs e)
    {
        var cancellation = _operationCancellation;
        if (cancellation is null)
        {
            SetMessage("当前没有可取消的操作。");
            return;
        }

        CancelOperationButton.IsEnabled = false;
        _logger.Session($"GUI 请求取消操作：{_busyAction}");
        SetMessage($"正在取消：{_busyAction}…");
        try
        {
            await cancellation.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // The operation finished between the click and the cancel request.
        }
    }

    private void OpenLogClick(object? sender, RoutedEventArgs e)
    {
        var logPath = _logger.LogPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{logPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            SetMessage($"无法打开日志：{exception.Message}（日志路径：{logPath}）", true);
        }
    }

    private void OpenIsolationDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string relativePath }) return;

        var directoryPath = Path.GetFullPath(Path.Combine(_configuration.ProjectRoot, relativePath));
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                SetMessage($"目录不存在：{directoryPath}", true);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{directoryPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            SetMessage($"无法打开目录：{exception.Message}（目录：{directoryPath}）", true);
        }
    }

    private void UpdateActionAvailabilitySafe()
    {
        try
        {
            UpdateActionAvailability();
        }
        catch (Exception exception)
        {
            _logger.Error($"更新界面可用状态失败：{exception}");
        }
    }

    private void UpdateBusyIndicator()
    {
        if (!_busy) return;
        var seconds = (int)(DateTimeOffset.Now - _busySince).TotalSeconds;
        WorkspaceStateText.Text = $"执行中… {seconds}s";
        BusyActionText.Text = _busyAction;
        BusyElapsedText.Text = $"已用时 {seconds} 秒";
        BusyProgressText.Text = string.IsNullOrWhiteSpace(_lastProgress) ? "正在处理，请稍候…" : _lastProgress;
        SelectionHintText.Text = string.IsNullOrWhiteSpace(_lastProgress)
            ? $"正在执行：{_busyAction}（{seconds}s）"
            : $"正在执行：{_busyAction}（{seconds}s）· {_lastProgress}";
        if (DateTimeOffset.Now - _lastBusyLog >= TimeSpan.FromSeconds(10))
        {
            _lastBusyLog = DateTimeOffset.Now;
            _logger.Session($"GUI 操作心跳：{_busyAction}，{seconds}s，{_lastProgress}");
        }
    }

    private void SaveConfigurationClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var directories = IsolatedDirectoriesBox.Text?.Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];
            var configuration = new WorkspaceConfiguration
            {
                ProjectRoot = ProjectRootBox.Text?.Trim() ?? string.Empty,
                TaskRoot = TaskRootBox.Text?.Trim() ?? string.Empty,
                SvnTrunkUrl = SvnUrlBox.Text?.Trim() ?? string.Empty,
                SvnExecutable = _configuration.SvnExecutable,
                IsolatedDirectories = directories,
                MonitoredProcesses = _configuration.MonitoredProcesses
            };
            if (!Directory.Exists(configuration.ProjectRoot)) throw new DirectoryNotFoundException($"项目根目录不存在：{configuration.ProjectRoot}");
            if (string.IsNullOrWhiteSpace(configuration.TaskRoot)) throw new InvalidOperationException("任务根目录不能为空。");
            if (configuration.IsolatedDirectories.Count == 0 || configuration.IsolatedDirectories.Any(path => !IsSafeRelativePath(path)))
                throw new InvalidOperationException("任务映射目录必须是项目根目录内的相对路径。");
            _store.SaveConfiguration(configuration);
            _configuration = configuration;
            _service = CreateService(configuration, _logger);
            SetMessage("配置已保存。");
            _ = RefreshStatusAsync();
        }
        catch (Exception exception) { SetMessage($"保存配置失败：{exception.Message}", true); }
    }

    private WorkspaceService CreateService(WorkspaceConfiguration configuration, WorkspaceLogger logger)
    {
        var service = new WorkspaceService(_store, configuration, new SvnClient(new ProcessRunner(logger), configuration.SvnExecutable), new JunctionManager());
        service.Progress += message =>
        {
            _lastProgress = message;
            _logger.Info($"进度：{message}");
            Dispatcher.UIThread.Post(() =>
            {
                _lastProgress = message;
                SetMessage(message);
            });
        };
        return service;
    }

    private string? SelectedTaskName() => (TaskList.SelectedItem as TaskListItem)?.Name;

    private static bool IsSafeRelativePath(string path) => !Path.IsPathRooted(path) && path is not ("." or "..") && !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(part => part == "..");

    private static bool IsTaskValid(TaskWorkspaceMetadata task, WorkspaceConfiguration configuration) =>
        Directory.Exists(task.RootPath) && configuration.IsolatedDirectories.All(relative => Directory.Exists(Path.Combine(task.RootPath, relative)));

    private static void SetValidity(TextBox box, bool valid)
    {
        box.Background = new SolidColorBrush(Color.Parse(valid ? "#DFF5E3" : "#FFE1E1"));
        box.BorderBrush = new SolidColorBrush(Color.Parse(valid ? "#53A869" : "#D64545"));
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children =
                        {
                            new Button { Content = "取消", IsCancel = true },
                            new Button { Content = confirmLabel, IsDefault = true }
                        }
                    }
                }
            }
        };
        var buttons = ((StackPanel)dialog.Content).Children[1] as StackPanel;
        if (buttons is not null)
        {
            ((Button)buttons.Children[0]).Click += (_, _) => dialog.Close();
            ((Button)buttons.Children[1]).Click += (_, _) => { result = true; dialog.Close(); };
        }
        await dialog.ShowDialog(this);
        return result;
    }
}

public sealed class TaskListItem
{
    public TaskListItem(TaskWorkspaceMetadata task, bool valid, TaskMappingAssessment assessment, string? activeTask, ParentWorkspaceStatus parentStatus, WorkspaceMode mode)
    {
        Name = task.Name;
        RootPath = task.RootPath;
        AssessmentDetails = assessment.Directories.Count == 0
            ? assessment.Summary
            : string.Join(Environment.NewLine, assessment.Directories.Select(directory => $"{directory.RelativePath}：{directory.Reason}"));
        Status = TaskListOrdering.GetStatus(task.Name, valid, assessment, activeTask, parentStatus, mode);
        IsActive = Status == TaskListStatus.CurrentTask;
        DisplayName = IsActive ? $"当前任务 · {task.Name}" : task.Name;
        var green = Status is TaskListStatus.CurrentTask or TaskListStatus.Ready;
        var taskReadyWithParentAction = Status is TaskListStatus.ParentBlocked or TaskListStatus.NeedsMigration;
        (UseGlyph, UseToolTip) = Status switch
        {
            TaskListStatus.CurrentTask => ("\uE73E", "当前任务已处于映射状态"),
            TaskListStatus.Ready => ("\uE8AB", "进入或切换到此任务映射"),
            TaskListStatus.ParentBlocked => ("\uE90F", "任务本身已就绪；父目录存在已跟踪修改或冲突，先清理父目录即可进入映射"),
            TaskListStatus.NeedsMigration => ("\uE8B5", "任务已就绪；父目录有未版本化文件，点击迁移到任务并进入映射"),
            TaskListStatus.NeedsConflict => ("\uE783", "任务存在 SVN 冲突或阻断状态；请先解决冲突并刷新状态"),
            TaskListStatus.NeedsUpdate => ("\uE72C", "任务版本落后；点击直接更新到 trunk"),
            _ => ("\uE783", "任务目录不完整")
        };
        StatusText = Status switch
        {
            TaskListStatus.CurrentTask => "◆ 映射中",
            TaskListStatus.Ready => "● 就绪",
            TaskListStatus.ParentBlocked => "● 任务已就绪 · 先清理父目录",
            TaskListStatus.NeedsMigration => "● 任务已就绪 · 需迁移本地文件",
            TaskListStatus.NeedsConflict => "⚠ 需处理冲突",
            TaskListStatus.NeedsUpdate => "⚠ 需更新",
            _ => "⚠ 目录不完整"
        };
        var parentBlocked = Status == TaskListStatus.ParentBlocked;
        Background = new SolidColorBrush(Color.Parse(Status == TaskListStatus.CurrentTask ? "#DCEBFF" : Status is TaskListStatus.Incomplete or TaskListStatus.NeedsConflict ? "#FFE1E1" : parentBlocked ? "#E6F5EA" : taskReadyWithParentAction ? "#EAF6EE" : green ? "#DFF5E3" : "#FFF4D6"));
        Foreground = new SolidColorBrush(Color.Parse(Status == TaskListStatus.CurrentTask ? "#1557A6" : Status is TaskListStatus.Incomplete or TaskListStatus.NeedsConflict ? "#B42318" : parentBlocked ? "#2C7A4B" : taskReadyWithParentAction ? "#3C7A5A" : green ? "#237A3B" : "#936400"));
        ActionForeground = new SolidColorBrush(Color.Parse(Status switch
        {
            TaskListStatus.Ready => "#2F6FEB",
            TaskListStatus.CurrentTask => "#237A3B",
            TaskListStatus.ParentBlocked => "#2C7A4B",
            TaskListStatus.NeedsMigration => "#3C7A5A",
            TaskListStatus.NeedsConflict => "#B42318",
            TaskListStatus.Incomplete => "#B42318",
            _ => "#936400"
        }));
        BorderBrush = new SolidColorBrush(Color.Parse(IsActive ? "#2F6FEB" : "#E1E6EE"));
        BorderThickness = IsActive ? new Avalonia.Thickness(3, 1, 1, 1) : new Avalonia.Thickness(1);
        DisplayWeight = IsActive ? FontWeight.Bold : FontWeight.SemiBold;
    }

    public string Name { get; }
    public string RootPath { get; }
    public string DisplayName { get; }
    public bool CanUse => Status is TaskListStatus.Ready or TaskListStatus.ParentBlocked or TaskListStatus.NeedsMigration or TaskListStatus.NeedsUpdate or TaskListStatus.NeedsConflict;
    public bool CanDelete => !IsActive;
    public bool CanSync { get; init; }
    public bool CanExitMapping { get; init; }
    public string SyncToolTip { get; init; } = "退出映射并同步目录";
    public string UseGlyph { get; }
    public string UseToolTip { get; }
    public string AssessmentDetails { get; }
    public IBrush ActionForeground { get; }
    public string StatusText { get; }
    public TaskListStatus Status { get; }
    public bool IsActive { get; }
    public IBrush Background { get; }
    public IBrush Foreground { get; }
    public IBrush BorderBrush { get; }
    public Avalonia.Thickness BorderThickness { get; }
    public FontWeight DisplayWeight { get; }
}

public sealed class DirectoryStatusItem
{
    private DirectoryStatusItem()
    {
    }

    public static DirectoryStatusItem FromRestore(RestoreDirectoryAssessment assessment) => new()
    {
        RelativePath = assessment.RelativePath,
        RevisionText = $"任务 r{assessment.TaskRevision?.ToString() ?? "未知"} / 恢复目标 r{assessment.TargetRevision?.ToString() ?? "未知"}",
        StateText = assessment.IsReady ? "● 可恢复" : "⚠ 不可恢复",
        DetailText = assessment.Reason,
        Background = new SolidColorBrush(Color.Parse(assessment.IsReady ? "#DFF5E3" : assessment.MappingValid ? "#FFF4D6" : "#FFE1E1")),
        Foreground = new SolidColorBrush(Color.Parse(assessment.IsReady ? "#237A3B" : assessment.MappingValid ? "#936400" : "#B42318"))
    };

    public static DirectoryStatusItem FromNormal(IsolationDirectorySnapshot directory)
    {
        var targetTask = GetTargetTask(directory.LinkTarget);
        var isError = !directory.Exists || directory.Error is not null || directory.IsJunction;
        var stateText = !directory.Exists ? "\u26a0 \u76ee\u5f55\u7f3a\u5931"
            : directory.Error is not null ? "\u26a0 \u5de5\u4f5c\u526f\u672c\u4e2d\u65ad"
            : directory.IsJunction ? $"\u76ee\u5f55\u6620\u5c04 \u00b7 {targetTask ?? "\u4efb\u52a1\u672a\u77e5"}"
            : directory.IsClean ? "\u25cf \u5e72\u51c0"
            : "\u26a0 \u6709\u672a\u63d0\u4ea4\u5185\u5bb9";
        var detailText = !directory.Exists ? "\u914d\u7f6e\u7684\u76ee\u5f55\u4e0d\u5b58\u5728"
            : directory.Error ?? (directory.IsJunction ? $"\u5b9e\u9645\u76ee\u6807\uff1a{directory.LinkTarget}"
            : directory.IsClean ? "\u53ef\u4ee5\u8fdb\u5165\u4efb\u52a1\u6620\u5c04"
            : $"\u5df2\u8ddf\u8e2a\u4fee\u6539 {directory.TrackedChanges} \u9879 \u00b7 \u672a\u7248\u672c\u5316\u6216\u5ffd\u7565 {directory.UnversionedEntries} \u9879");
        var location = directory.IsJunction ? "\u76ee\u5f55\u6620\u5c04\uff08junction\uff09" : "\u9879\u76ee\u76ee\u5f55";
        return new DirectoryStatusItem
        {
            RelativePath = directory.RelativePath,
            RevisionText = directory.Revision is null ? $"{location} \u00b7 revision \u672a\u77e5" : $"{location} \u00b7 r{directory.Revision}",
            TargetText = directory.IsJunction ? $"\u2192 {directory.LinkTarget}" : string.Empty,
            StateText = stateText,
            DetailText = detailText,
            Background = new SolidColorBrush(Color.Parse(isError ? "#FFE1E1" : directory.IsClean ? "#DFF5E3" : "#FFF4D6")),
            Foreground = new SolidColorBrush(Color.Parse(isError ? "#B42318" : directory.IsClean ? "#237A3B" : "#936400"))
        };
    }

    private static string? GetTargetTask(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return null;
        var directory = new DirectoryInfo(Path.GetFullPath(targetPath));
        return directory.Parent?.Parent?.Name;
    }
    public string RelativePath { get; private init; } = string.Empty;
    public string RevisionText { get; private init; } = string.Empty;
    public string TargetText { get; private init; } = string.Empty;
    public bool HasTarget => !string.IsNullOrWhiteSpace(TargetText);
    public string StateText { get; private init; } = string.Empty;
    public string DetailText { get; private init; } = string.Empty;
    public IBrush Background { get; private init; } = new SolidColorBrush(Colors.Transparent);
    public IBrush Foreground { get; private init; } = new SolidColorBrush(Colors.Black);
}
