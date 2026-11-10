using SvnWorkspaceManager.Core.Configuration;
using SvnWorkspaceManager.Core.FileSystem;
using SvnWorkspaceManager.Core.Models;
using SvnWorkspaceManager.Core.Security;
using SvnWorkspaceManager.Core.Svn;

namespace SvnWorkspaceManager.Core;

public sealed partial class WorkspaceService
{
    private readonly WorkspaceStore _store;
    private readonly WorkspaceConfiguration _configuration;
    private readonly PathGuard _guard;
    private readonly SvnClient _svn;
    private readonly JunctionManager _junctions;
    private readonly IRecycleBin _recycleBin;

    public WorkspaceService(WorkspaceStore store, WorkspaceConfiguration configuration, SvnClient svn, JunctionManager junctions, IRecycleBin? recycleBin = null)
    {
        _store = store;
        _configuration = configuration;
        _guard = new PathGuard(configuration.ProjectRoot, configuration.TaskRoot);
        _svn = svn;
        _junctions = junctions;
        _recycleBin = recycleBin ?? new WindowsRecycleBin();
    }

    public WorkspaceConfiguration Configuration => _configuration;
    public event Action<string>? Progress;

    internal void Report(string message) => Progress?.Invoke(message);

    public IEnumerable<TaskWorkspaceMetadata> ListTasks() => _store.ListTasks().OrderBy(task => task.Name, StringComparer.OrdinalIgnoreCase);

    public async Task<WorkspaceSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = _store.LoadState();
        var projectStatus = await _svn.StatusAsync(_configuration.ProjectRoot, cancellationToken);
        var taskStatuses = new Dictionary<string, CommandResult>(StringComparer.OrdinalIgnoreCase);
        if (state.Mode == WorkspaceMode.TaskMapping && state.ActiveTask is not null)
        {
            foreach (var relativePath in _configuration.IsolatedDirectories)
                taskStatuses[relativePath] = await _svn.StatusAsync(_guard.TaskPath(state.ActiveTask, relativePath), cancellationToken);
        }
        var restoreAssessment = await GetRestoreAssessmentAsync(cancellationToken);
        var isolationDirectories = await GetIsolationDirectoriesAsync(cancellationToken);
        return new WorkspaceSnapshot(state, projectStatus, taskStatuses, restoreAssessment, isolationDirectories);
    }

    private async Task<IReadOnlyList<IsolationDirectorySnapshot>> GetIsolationDirectoriesAsync(CancellationToken cancellationToken)
    {
        var directories = new List<IsolationDirectorySnapshot>();
        foreach (var relativePath in _configuration.IsolatedDirectories)
        {
            var projectPath = _guard.ProjectPath(relativePath);
            if (!Directory.Exists(projectPath))
            {
                directories.Add(new IsolationDirectorySnapshot(relativePath, false, false, null, null, null, 0, 0, "目录不存在"));
                continue;
            }

            try
            {
                var isJunction = _junctions.IsJunction(projectPath);
                var linkTarget = isJunction ? _junctions.GetTarget(projectPath) : null;
                var info = await _svn.WorkingCopyInfoAsync(projectPath, cancellationToken);
                var status = await _svn.StatusIncludingUnversionedAsync(projectPath, cancellationToken);
                var entries = status.Succeeded ? SvnStatusParser.Parse(status.StandardOutput, projectPath) : [];
                var hasBlockingStatus = entries.Any(entry => entry.HasBlockingFlag);
                directories.Add(new IsolationDirectorySnapshot(
                    relativePath,
                    true,
                    isJunction,
                    linkTarget,
                    info.Info?.Revision,
                    info.Info?.Url,
                    entries.Count(entry => entry.Code is not ('?' or 'I')),
                    entries.Count(entry => entry.Code is '?' or 'I'),
                    !status.Succeeded ? status.StandardError.Trim()
                        : hasBlockingStatus ? "存在 SVN 冲突、缺失或中断状态，请先清理工作副本" : null));
            }
            catch (Exception exception)
            {
                directories.Add(new IsolationDirectorySnapshot(relativePath, true, false, null, null, null, 0, 0, exception.Message));
            }
        }
        return directories;
    }

    public async Task<TaskWorkspaceMetadata> CreateTaskAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateIsolationLayout();
        PathGuard.ValidateTaskName(name);
        if (_store.ListTasks().Any(task => task.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"任务已存在：{name}");

        var root = Path.Combine(Path.GetFullPath(_configuration.TaskRoot), name);
        if (Directory.Exists(root)) throw new IOException($"任务目录已存在但没有有效记录：{root}");

        Report("正在检查 SVN 服务器连接…");
        var reachable = await _svn.RepositoryRevisionAsync(_configuration.SvnTrunkUrl, cancellationToken);
        if (reachable.Revision is null)
            throw new InvalidOperationException($"无法连接 SVN 服务器或读取 trunk revision：{reachable.Result.StandardError.Trim()}。请检查 VPN 或网络后重试。");

        var metadata = new TaskWorkspaceMetadata
        {
            Name = name,
            RootPath = root,
            SvnUrl = _configuration.SvnTrunkUrl,
            IsolatedDirectories = [.. _configuration.IsolatedDirectories]
        };
        try
        {
            foreach (var relativePath in metadata.IsolatedDirectories)
            {
                var url = CombineSvnUrl(_configuration.SvnTrunkUrl, relativePath);
                var destination = _guard.TaskPath(name, relativePath);
                Report($"正在从 trunk 检出 {relativePath}（任务 {name}）…");
                var result = await _svn.CheckoutAsync(url, destination, cancellationToken);
                if (!result.Succeeded) throw new InvalidOperationException($"SVN checkout 失败：{result.StandardError}");
                Report($"已检出 {relativePath}。");
            }
            _store.SaveTask(metadata);
            return metadata;
        }
        catch
        {
            TryDeleteFreshTaskDirectory(root);
            throw;
        }
    }

    public Task RemoveTaskRecordAsync(string taskName)
    {
        var state = _store.LoadState();
        if (state.Mode == WorkspaceMode.TaskMapping && state.ActiveTask?.Equals(taskName, StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("当前任务正在映射中，请先退出任务映射后再移除记录。");
        _store.RemoveTaskRecord(FindTask(taskName));
        return Task.CompletedTask;
    }

    public Task DeleteTaskToRecycleBinAsync(string taskName)
    {
        var state = _store.LoadState();
        if (state.Mode == WorkspaceMode.TaskMapping && state.ActiveTask?.Equals(taskName, StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("当前任务正在映射中，请先退出任务映射后再删除任务。");
        if (state.RestoreOperation is { } operation && operation.TaskName.Equals(taskName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("该任务仍有未完成的恢复操作，请先继续完成或清理该操作。");

        var task = FindTask(taskName);
        PathGuard.ValidateTaskName(task.Name);
        var taskDirectory = _guard.TaskPath(task.Name, ".");
        var recordedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(task.RootPath));
        if (!recordedRoot.Equals(taskDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"任务记录中的目录与任务根目录不一致，拒绝删除：{recordedRoot}");

        _recycleBin.DeleteDirectory(taskDirectory);
        _store.RemoveTaskRecord(task);
        return Task.CompletedTask;
    }

    public async Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        var state = _store.LoadState();
        await EnsureSuccess(_svn.UpdateAsync(_configuration.ProjectRoot, cancellationToken), "更新公共工作区");
        if (state.Mode != WorkspaceMode.TaskMapping || state.ActiveTask is null) return;
        foreach (var relativePath in _configuration.IsolatedDirectories)
            await EnsureSuccess(_svn.UpdateAsync(_guard.TaskPath(state.ActiveTask, relativePath), cancellationToken), $"更新任务目录 {relativePath}");
    }

    public async Task UpdateTaskAsync(string taskName, CancellationToken cancellationToken = default)
    {
        var task = FindTask(taskName);
        foreach (var relativePath in _configuration.IsolatedDirectories)
            await EnsureSuccess(_svn.UpdateAsync(_guard.TaskPath(task.Name, relativePath), cancellationToken), $"更新任务目录 {relativePath}");
    }

    private TaskWorkspaceMetadata FindTask(string name) => _store.ListTasks().FirstOrDefault(task => task.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"任务不存在：{name}");

    private static async Task EnsureSuccess(Task<CommandResult> operation, string action)
    {
        var result = await operation;
        if (!result.Succeeded) throw new InvalidOperationException($"{action}失败：{result.StandardError}");
    }

    private static string CombineSvnUrl(string root, string relativePath) => root.TrimEnd('/') + "/" + relativePath.Replace('\\', '/').Trim('/');

    private void ValidateIsolationLayout()
    {
        var directories = _configuration.IsolatedDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(_guard.ProjectPath)
            .ToList();
        if (directories.Count == 0) throw new InvalidOperationException("至少需要配置一个任务映射目录。");
        if (directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != directories.Count)
            throw new InvalidOperationException("任务映射目录不能重复。");
        for (var index = 0; index < directories.Count; index++)
        {
            for (var other = index + 1; other < directories.Count; other++)
            {
                var first = directories[index];
                var second = directories[other];
                if (first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"任务映射目录不能互相包含：{first} 与 {second}");
            }
        }
    }

    private static void TryDeleteFreshTaskDirectory(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
            // The caller receives the original checkout failure.
        }
    }
}

public sealed record WorkspaceSnapshot(WorkspaceState State, CommandResult ProjectStatus, IReadOnlyDictionary<string, CommandResult> TaskStatuses, RestoreAssessment RestoreAssessment, IReadOnlyList<IsolationDirectorySnapshot> IsolationDirectories);
