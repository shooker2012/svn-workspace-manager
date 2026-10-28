using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    public MainWindow()
    {
        InitializeComponent();
        var applicationDirectory = Environment.GetEnvironmentVariable("SVN_WORKSPACE_MANAGER_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Azure", "SvnWorkspaceManager");
        _store = new WorkspaceStore(applicationDirectory);
        _configuration = _store.LoadConfiguration();
        _logger = new WorkspaceLogger(Path.Combine(applicationDirectory, "logs"));
        _logger.Session($"GUI 启动：pid={Environment.ProcessId}，日志={_logger.LogPath}");
        _service = CreateService(_configuration, _logger);
        _busyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _busyTimer.Tick += (_, _) => UpdateBusyIndicator();
        _ = RefreshStatusAsync();
    }

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
        WorkspacePathText.Text = "任务路径：无";
        PublicStatusText.Text = "公共工作区状态：正在检查…";
        SetValidity(ProjectRootBox, Directory.Exists(configuration.ProjectRoot));
        SetValidity(TaskRootBox, Directory.Exists(configuration.TaskRoot));
        SetValidity(SvnUrlBox, !string.IsNullOrWhiteSpace(configuration.SvnTrunkUrl));
        SetValidity(IsolatedDirectoriesBox, configuration.IsolatedDirectories.Count > 0 && configuration.IsolatedDirectories.All(IsSafeRelativePath));
        ModeText.Text = state.Mode == WorkspaceMode.Normal ? "普通模式" : $"任务映射模式：{state.ActiveTask}";
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
            ? $"任务路径：{activeTask?.RootPath ?? configuration.ProjectRoot}"
            : $"项目路径：{configuration.ProjectRoot}";
        var snapshot = await _service.GetStatusAsync(cancellationToken);
        var assessment = snapshot.RestoreAssessment;
        _restoreAssessment = assessment;
        _isolationDirectories = snapshot.IsolationDirectories;
        PublicStatusText.Text = snapshot.ProjectStatus.Succeeded
            ? "公共工作区状态：已读取"
            : $"公共工作区状态：检查失败（{snapshot.ProjectStatus.StandardError.Trim()}）";
        var parentStatus = GetParentWorkspaceStatus();
        var taskItems = TaskListOrdering.OrderForDisplay(
                tasks.Select(task => new TaskListItem(task, IsTaskValid(task, configuration), taskAssessments[task.Name], state.ActiveTask, parentStatus, state.Mode)),
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
        RestoreButton.IsVisible = assessment.IsReady || interrupted is not null;
        RestoreButton.Content = interrupted is null
            ? "恢复到原目录并退出任务映射"
            : interrupted.Kind switch
            {
                WorkspaceOperationKinds.Exit => "继续退出任务映射",
                WorkspaceOperationKinds.RollbackMapping => "继续回滚进入操作",
                _ => "继续恢复到原目录"
            };
        RestoreCard.IsVisible = state.Mode == WorkspaceMode.TaskMapping || interrupted is not null;
        RestoreCard.Background = new SolidColorBrush(Color.Parse(assessment.IsReady ? "#DFF5E3" : state.Mode == WorkspaceMode.TaskMapping ? "#FFE1E1" : "#FFF4D6"));
        RestoreStatusText.Text = interrupted is not null
            ? $"! 上次操作未完成：{interrupted.Phase}"
            : assessment.IsReady ? "● 可以恢复到原目录" : state.Mode == WorkspaceMode.TaskMapping ? "! 不能恢复到原目录" : "— 当前不是任务映射模式";
        RevisionStatusText.Text = interrupted is not null
            ? $"未完成操作：{interrupted.Kind} / {interrupted.Phase} / {interrupted.CurrentDirectory ?? "无"}{Environment.NewLine}{(string.IsNullOrWhiteSpace(interrupted.Error) ? "点击右侧按钮继续。" : interrupted.Error)}"
            : assessment.Directories.Count == 0
                ? "进入任务映射模式后，这里会显示任务 svnversion 和恢复目标。"
                : string.Join(Environment.NewLine, assessment.Directories.Select(directory =>
                    $"{directory.RelativePath}：任务工作副本 {(directory.TaskRevision is null ? "未知" : $"r{directory.TaskRevision}")} / 恢复目标 {(directory.TargetRevision is null ? "未知" : $"r{directory.TargetRevision}")} — {directory.Reason}"));
        SetMessage($"项目目录：{configuration.ProjectRoot}\n任务目录：{configuration.TaskRoot}\n隔离目录：{string.Join(", ", configuration.IsolatedDirectories)}\n\n{assessment.Summary}");
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
            RevisionStatusText.Text = exception.Message;
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
                new TextBlock { Text = "会把隔离目录中的修改、未版本化和忽略文件迁移到新任务，并立即进入任务映射模式。", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#637083")) },
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
                    ? "父级隔离目录状态尚未读取完成。"
                    : string.Join("；", blocked.Select(DescribeIsolationProblem));
                var hint = blocked.Any(directory => !directory.Exists || directory.IsJunction || directory.Error is not null)
                    ? "可在“当前工作区”卡片点击“修复父级工作副本”。"
                    : "处理完父目录的修改或冲突后，点击右侧进入图标即可进入映射。";
                SetMessage($"任务“{task}”本身已经就绪，不需要更新。{Environment.NewLine}父目录需要先清理：{description}{Environment.NewLine}{hint}");
                return;
            }
            case TaskListStatus.NeedsMigration:
                if (!await ConfirmAsync("确认迁移本地文件", $"隔离目录中有未版本化或忽略内容。进入映射前会把这些内容迁移到任务“{task}”，随后原目录将映射到该任务。是否继续？", "迁移并进入映射")) return;
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
            flyout.Items.Add(CreateTaskMenuItem("\uE72B", "退出任务映射（保留工作区）", ExitTaskMappingAsync));

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
        ReadinessPanel.IsVisible = !isMapping;
        if (isMapping)
        {
            WorkspaceStateText.Text = string.IsNullOrWhiteSpace(state.ActiveTask) ? "任务映射" : $"任务映射 · {state.ActiveTask}";
            WorkspaceStateBadge.Background = new SolidColorBrush(Color.Parse("#DCEBFF"));
            WorkspaceStateText.Foreground = new SolidColorBrush(Color.Parse("#1557A6"));
            return;
        }

        var problems = _isolationDirectories.Where(directory => !directory.IsClean).ToList();
        var parentStatus = GetParentWorkspaceStatus();
        var parentClean = parentStatus == ParentWorkspaceStatus.Clean;
        var parentBlocked = parentStatus == ParentWorkspaceStatus.Blocked;
        var damaged = problems.Any(directory => directory.Error is not null);
        var parentDamaged = _isolationDirectories.Any(directory => !directory.Exists || directory.IsJunction || directory.Error is not null);
        SetCheck(ParentCheckText,
            _isolationDirectories.Count == 0
                ? "✗ 尚未配置隔离目录"
                : parentClean
                    ? $"✓ 父级隔离目录干净：{string.Join("、", _isolationDirectories.Select(directory => $"{directory.RelativePath} r{directory.Revision}"))}"
                    : parentDamaged
                        ? $"⚠ 父级隔离目录状态异常：{string.Join("；", problems.Select(DescribeIsolationProblem))}"
                        : parentBlocked
                            ? $"● 父级隔离目录存在已跟踪修改或冲突：{string.Join("；", problems.Select(DescribeIsolationProblem))}"
                            : $"● 父级隔离目录有未版本化或忽略内容：{string.Join("；", problems.Select(DescribeIsolationProblem))}（进入映射时会迁移到任务）",
            parentClean ? "#237A3B" : parentDamaged ? "#936400" : "#2C7A4B");

        var hasSelection = !string.IsNullOrWhiteSpace(selected);
        var taskReady = selectedAssessment?.IsReady == true;
        SetCheck(TaskCheckText,
            !hasSelection
                ? "— 先在左侧选择一个任务"
                : taskReady
                    ? $"✓ 任务 {selected} 已包含 trunk 目录的最新变更（r{selectedAssessment!.Directories.FirstOrDefault()?.TargetRevision}）"
                    : $"✗ 任务 {selected}：{selectedAssessment?.Summary ?? "尚未检查完成"}",
            taskReady ? "#237A3B" : hasSelection ? "#936400" : "#637083");

        var ready = !parentBlocked && taskReady;
        ActionHintText.Text = _busy
            ? "正在执行操作…"
            : damaged
                ? "下一步：点击“修复父级工作副本”，清理中断的 SVN 排除状态。"
            : !hasSelection
                ? "下一步：选择一个任务查看映射资格。"
            : !taskReady
                ? "下一步：点击任务右侧的刷新图标，把任务更新到 trunk 目录的最新变更。"
            : parentBlocked
                ? "任务本身已就绪，不需要更新；下一步：处理父目录中已跟踪的修改或冲突，之后即可进入映射。"
            : !parentClean
                ? "下一步：点击任务右侧的迁移图标，把未版本化或忽略内容移入任务并进入映射。"
            : "下一步：点击任务右侧的进入图标，进入任务映射。";
        ActionHintText.Foreground = new SolidColorBrush(Color.Parse(parentBlocked ? "#2C7A4B" : ready ? "#237A3B" : "#637083"));

        WorkspaceStateText.Text = _busy
            ? "执行中…"
            : taskReady && parentBlocked
                ? "● 任务已就绪 · 先清理父目录"
                : taskReady && parentClean
                    ? "● 可进入映射"
                    : taskReady
                        ? "● 任务已就绪 · 可迁移后进入"
                        : hasSelection ? "⚠ 任务需更新" : "⚠ 存在阻塞";
        WorkspaceStateBadge.Background = new SolidColorBrush(Color.Parse(
            taskReady && parentClean ? "#DFF5E3" : taskReady ? "#E6F5EA" : "#FFF4D6"));
        WorkspaceStateText.Foreground = new SolidColorBrush(Color.Parse(
            taskReady && parentClean ? "#237A3B" : taskReady ? "#2C7A4B" : "#936400"));
    }

    private void RenderIsolationDirectories(WorkspaceState state)
    {
        if (state.Mode == WorkspaceMode.TaskMapping)
        {
            DirectoryStatusList.ItemsSource = (_restoreAssessment?.Directories ?? []).Select(DirectoryStatusItem.FromRestore).ToList();
            DirectoryHintText.Text = "绿色表示可以把该目录安全恢复到原目录；黄色或红色表示还需要先处理映射、revision 或工作副本状态。";
            return;
        }

        DirectoryStatusList.ItemsSource = _isolationDirectories.Select(DirectoryStatusItem.FromNormal).ToList();
        DirectoryHintText.Text = "普通模式下显示每个隔离目录的位置、revision 和未提交内容。进入映射后，这里会显示任务 revision 与恢复目标。";
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
        if (!await ConfirmAsync("确认退出任务映射", "将移除原目录中的 junction，并把每个隔离目录恢复到其直接父目录当前的 SVN revision。任务内容不会复制回原目录，任务工作区会保留。是否继续？", "确认退出任务映射")) return;
        await RunActionAsync("退出任务映射", token => _service.ExitTaskMappingAsync(token));
    }

    private async void RestoreCurrentTaskClick(object? sender, RoutedEventArgs e)
    {
        var state = _store.LoadState();
        if (state.RestoreOperation is not null)
        {
            if (!await ConfirmAsync("继续未完成的操作", $"上次操作在“{state.RestoreOperation.Phase}”阶段中断。现在继续执行吗？", "继续")) return;
            await RunActionAsync("继续未完成的操作", token => _service.ResumeInterruptedOperationAsync(token));
            return;
        }
        if (!await ConfirmAsync("确认恢复到原目录", "将把任务内容（包括未版本化文件）同步到原目录，并把每个父目录对齐到任务 revision，然后退出任务映射模式。任务工作区会保留。是否继续？", "确认恢复")) return;
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
                throw new InvalidOperationException("隔离目录必须是项目根目录内的相对路径。");
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
            TaskListStatus.Ready => "● 可进入映射",
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
        var isError = !directory.Exists || directory.Error is not null || directory.IsJunction;
        var stateText = !directory.Exists ? "⚠ 目录缺失"
            : directory.Error is not null ? "⚠ 工作副本中断"
            : directory.IsJunction ? "⚠ 仍为 junction"
            : directory.IsClean ? "● 干净"
            : "⚠ 有未提交内容";
        var detailText = !directory.Exists ? "配置的目录不存在"
            : directory.Error ?? (directory.IsJunction ? "普通模式下不应存在 junction，请先处理"
            : directory.IsClean ? "可以进入任务映射"
            : $"已跟踪修改 {directory.TrackedChanges} 项 · 未版本化或忽略 {directory.UnversionedEntries} 项");
        var location = directory.IsJunction ? "junction" : "普通目录";
        return new DirectoryStatusItem
        {
            RelativePath = directory.RelativePath,
            RevisionText = directory.Revision is null ? $"{location} · revision 未知" : $"{location} · r{directory.Revision}",
            StateText = stateText,
            DetailText = detailText,
            Background = new SolidColorBrush(Color.Parse(isError ? "#FFE1E1" : directory.IsClean ? "#DFF5E3" : "#FFF4D6")),
            Foreground = new SolidColorBrush(Color.Parse(isError ? "#B42318" : directory.IsClean ? "#237A3B" : "#936400"))
        };
    }

    public string RelativePath { get; private init; } = string.Empty;
    public string RevisionText { get; private init; } = string.Empty;
    public string StateText { get; private init; } = string.Empty;
    public string DetailText { get; private init; } = string.Empty;
    public IBrush Background { get; private init; } = new SolidColorBrush(Colors.Transparent);
    public IBrush Foreground { get; private init; } = new SolidColorBrush(Colors.Black);
}
