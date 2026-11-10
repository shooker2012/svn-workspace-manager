using SvnWorkspaceManager.Core.FileSystem;
using SvnWorkspaceManager.Core.Models;
using SvnWorkspaceManager.Core.Svn;

namespace SvnWorkspaceManager.Core;

public sealed partial class WorkspaceService
{
    public async Task<TaskMappingAssessment> GetTaskMappingAssessmentAsync(string taskName, CancellationToken cancellationToken = default)
    {
        ValidateIsolationLayout();
        var task = FindTask(taskName);
        var directories = new List<TaskMappingDirectoryAssessment>();
        foreach (var relativePath in _configuration.IsolatedDirectories)
        {
            var expectedUrl = CombineSvnUrl(_configuration.SvnTrunkUrl, relativePath);
            var taskPath = _guard.TaskPath(task.Name, relativePath);
            var taskInfoResult = await _svn.WorkingCopyInfoAsync(taskPath, cancellationToken);
            var targetRevisionResult = await _svn.RepositoryLastChangedRevisionAsync(expectedUrl, cancellationToken);
            var status = await _svn.StatusAsync(taskPath, cancellationToken);
            var taskInfo = taskInfoResult.Info;
            var taskRevision = taskInfo?.Revision;
            var targetRevision = targetRevisionResult.Revision;
            var urlMatches = taskInfo is not null && taskInfo.Url.Equals(expectedUrl, StringComparison.OrdinalIgnoreCase);
            var revisionMatches = taskRevision is not null && targetRevision is not null && taskRevision >= targetRevision;
            var hasBlockingStatus = !status.Succeeded || SvnStatusParser.ContainsBlockingStatus(status.StandardOutput, Path.GetDirectoryName(taskPath)!);
            var reason = taskInfo is null ? "无法读取任务工作副本信息"
                : targetRevision is null ? $"无法读取 trunk 目录的最后变更 revision：{targetRevisionResult.Result.StandardError.Trim()}"
                : !urlMatches ? "任务 SVN URL 与配置的 trunk 路径不一致"
                : hasBlockingStatus ? "任务工作区存在冲突、缺失、目录受阻或锁定"
                : !revisionMatches ? $"任务 r{taskRevision} 落后于 trunk 目录的最后变更 r{targetRevision}，请先更新任务"
                : "任务已包含 trunk 目录的最新变更";
            directories.Add(new TaskMappingDirectoryAssessment(relativePath, taskRevision, targetRevision, expectedUrl, taskInfo?.Url, revisionMatches, urlMatches, hasBlockingStatus, reason));
        }

        var summary = directories.Count > 0 && directories.All(directory => directory.IsReady)
            ? "任务已与 trunk 对齐，可以进入任务映射模式。"
            : "当前任务不能进入任务映射模式，请先处理版本不一致或阻断状态的任务映射目录。";
        return new TaskMappingAssessment(task.Name, directories, summary);
    }

    public async Task EnableTaskMappingAsync(string taskName, CancellationToken cancellationToken = default)
    {
        var state = _store.LoadState();
        if (state.Mode != WorkspaceMode.Normal)
            throw new InvalidOperationException("当前已处于任务映射模式。");

        var task = FindTask(taskName);
        var assessment = await GetTaskMappingAssessmentAsync(task.Name, cancellationToken);
        if (!assessment.IsReady)
            throw new InvalidOperationException($"不能进入任务映射：{assessment.Summary}{Environment.NewLine}{DescribeBlockingDirectories(assessment.Directories)}");

        await EnsureIsolatedDirectoriesCleanAsync(cancellationToken);

        var previousRevisions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in _configuration.IsolatedDirectories)
        {
            var projectPath = _guard.ProjectPath(relativePath);
            if (!Directory.Exists(projectPath)) throw new DirectoryNotFoundException($"原目录不存在：{projectPath}");
            if (_junctions.IsJunction(projectPath)) throw new InvalidOperationException($"原目录已经是 junction：{projectPath}");
            var info = await _svn.WorkingCopyInfoAsync(projectPath, cancellationToken);
            if (info.Info is null) throw new InvalidOperationException($"无法读取原目录 {relativePath} 的 SVN 信息：{info.Result.StandardError}");
            var expectedUrl = CombineSvnUrl(_configuration.SvnTrunkUrl, relativePath);
            if (!info.Info.Url.Equals(expectedUrl, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"原目录 {relativePath} 的 SVN URL 与配置不一致：{info.Info.Url}");
            previousRevisions[relativePath] = info.Info.Revision;
        }

        var operation = new RestoreOperationState
        {
            TaskName = task.Name,
            Kind = WorkspaceOperationKinds.RollbackMapping,
            Phase = "Preparing",
            TargetRevisions = previousRevisions
        };
        SaveOperation(task.Name, operation);
        try
        {
            foreach (var relativePath in _configuration.IsolatedDirectories)
            {
                var projectPath = _guard.ProjectPath(relativePath);
                Report($"正在排除父级目录 {relativePath}…");
                await ExcludeWithCleanupRetryAsync(projectPath, cancellationToken);
                operation.ExcludedDirectories.Add(relativePath);
                SaveOperation(task.Name, operation);
                await WaitForExclusionAsync(projectPath, cancellationToken);
                if (Directory.Exists(projectPath))
                {
                    Report($"父级目录 {relativePath} 释放较慢，正在重试排除…");
                    var retryCleanup = await _svn.CleanupAsync(_configuration.ProjectRoot, cancellationToken);
                    if (retryCleanup.Succeeded)
                    {
                        await ExcludeWithCleanupRetryAsync(projectPath, cancellationToken);
                        await WaitForExclusionAsync(projectPath, cancellationToken);
                    }
                }
                if (Directory.Exists(projectPath))
                    throw new IOException($"排除后父级目录仍然存在，无法建立映射：{projectPath}。请关闭占用该目录的程序后重试。");
                Report($"正在建立映射 {relativePath} → {task.Name}…");
                _junctions.Create(projectPath, _guard.TaskPath(task.Name, relativePath));
                operation.LinkedDirectories.Add(relativePath);
                SaveOperation(task.Name, operation);
            }
            _store.SaveState(new WorkspaceState { Mode = WorkspaceMode.TaskMapping, ActiveTask = task.Name });
            Report($"已进入任务映射模式：{task.Name}");
        }
        catch (Exception exception)
        {
            operation.Phase = "Interrupted";
            operation.Error = exception.Message;
            SaveOperation(task.Name, operation);
            Report("进入映射失败，正在回滚父级目录…");
            try
            {
                await ResumeRollbackMappingAsync(cancellationToken);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException($"进入任务映射失败，且回滚未完成：{rollbackException.Message}。请处理占用或网络问题后点击“继续回滚进入操作”。", exception, rollbackException);
            }
            throw;
        }
    }

    public async Task ResumeRollbackMappingAsync(CancellationToken cancellationToken = default)
    {
        var state = _store.LoadState();
        if (state.RestoreOperation is not { Kind: WorkspaceOperationKinds.RollbackMapping } operation) return;
        try
        {
            var cleanup = await _svn.CleanupAsync(_configuration.ProjectRoot, cancellationToken);
            if (!cleanup.Succeeded) throw new InvalidOperationException($"清理 SVN 工作队列失败：{cleanup.StandardError}");
            foreach (var relativePath in _configuration.IsolatedDirectories.AsEnumerable().Reverse())
            {
                if (operation.CompletedDirectories.Contains(relativePath, StringComparer.OrdinalIgnoreCase)) continue;
                operation.CurrentDirectory = relativePath;
                operation.Phase = "RollingBack";
                SaveOperation(operation.TaskName, operation);
                var projectPath = _guard.ProjectPath(relativePath);
                var taskPath = _guard.TaskPath(operation.TaskName, relativePath);
                if (_junctions.IsJunction(projectPath))
                {
                    if (!_junctions.IsJunctionTo(projectPath, taskPath))
                        throw new InvalidOperationException($"目录 {relativePath} 的 junction 未指向当前任务，请人工确认。");
                    _junctions.RemoveLinkOnly(projectPath, _configuration.TaskRoot);
                }
                if (!operation.TargetRevisions.TryGetValue(relativePath, out var targetRevision))
                    targetRevision = await ReadDirectParentRevisionAsync(projectPath, cancellationToken);
                Report($"正在回滚 {relativePath} 到 r{targetRevision}…");
                await RestoreExcludedDirectoryAsync(relativePath, projectPath, taskPath, targetRevision, cancellationToken);
                operation.CompletedDirectories.Add(relativePath);
                SaveOperation(operation.TaskName, operation);
            }
            _store.SaveState(new WorkspaceState { Mode = WorkspaceMode.Normal });
        }
        catch (Exception exception)
        {
            operation.Phase = "Interrupted";
            operation.Error = exception.Message;
            SaveOperation(operation.TaskName, operation);
            throw;
        }
    }

    public async Task EnableTaskMappingWithUnversionedChangesAsync(string taskName, CancellationToken cancellationToken = default)
    {
        var state = _store.LoadState();
        if (state.Mode != WorkspaceMode.Normal)
            throw new InvalidOperationException("当前已处于任务映射模式。");

        var task = FindTask(taskName);
        var assessment = await GetTaskMappingAssessmentAsync(task.Name, cancellationToken);
        if (!assessment.IsReady)
            throw new InvalidOperationException($"不能进入任务映射：{assessment.Summary}{Environment.NewLine}{DescribeBlockingDirectories(assessment.Directories)}");

        var moves = new List<(string Source, string Target)>();
        foreach (var relativePath in _configuration.IsolatedDirectories)
        {
            var projectPath = _guard.ProjectPath(relativePath);
            var taskPath = _guard.TaskPath(task.Name, relativePath);
            var status = await _svn.StatusIncludingUnversionedAsync(projectPath, cancellationToken);
            if (!status.Succeeded)
                throw new InvalidOperationException($"无法读取任务映射目录 {relativePath} 的 SVN 状态：{status.StandardError}");

            var entries = SvnStatusParser.Parse(status.StandardOutput, projectPath);
            if (entries.Any(entry => entry.HasBlockingFlag))
                throw new InvalidOperationException($"任务映射目录 {relativePath} 存在冲突、缺失、目录受阻或锁定，不能迁移。");
            if (entries.Any(entry => entry.Code is not ('?' or 'I')))
                throw new InvalidOperationException($"任务映射目录 {relativePath} 存在已跟踪修改，请先提交或恢复，或使用“从当前修改进入映射”。");

            foreach (var entry in entries.Where(entry => (entry.Code is '?' or 'I') && IsUnder(projectPath, entry.FullPath)).OrderBy(entry => entry.FullPath.Length))
            {
                if (moves.Any(move => IsUnder(move.Source, entry.FullPath))) continue;
                var relativeItem = Path.GetRelativePath(projectPath, entry.FullPath);
                var target = Path.Combine(taskPath, relativeItem);
                if (File.Exists(target) || Directory.Exists(target))
                    throw new InvalidOperationException($"任务目录中已存在同名内容，无法迁移：{relativeItem}");
                moves.Add((entry.FullPath, target));
            }
        }

        if (moves.Count == 0)
        {
            await EnableTaskMappingAsync(task.Name, cancellationToken);
            return;
        }

        var completed = new List<(string Source, string Target)>();
        try
        {
            Report($"正在迁移 {moves.Count} 项未版本化或忽略内容到任务 {task.Name}…");
            foreach (var move in moves)
            {
                var relative = Path.GetRelativePath(_configuration.ProjectRoot, move.Source).Replace('\\', '/');
                Report($"正在迁移 {relative}…");
                var progress = new FileOperationProgress(Report, relative);
                await Task.Run(() =>
                {
                    CopyItem(move.Source, move.Target, progress.Copied, cancellationToken);
                    EnsureMirror(move.Source, move.Target, cancellationToken);
                    DeleteItem(move.Source, progress.Deleted, cancellationToken);
                }, cancellationToken);
                progress.Complete("迁移");
                completed.Add(move);
            }

            await EnableTaskMappingAsync(task.Name, cancellationToken);
        }
        catch
        {
            foreach (var move in completed.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(move.Source) || Directory.Exists(move.Source)) continue;
                    if (!File.Exists(move.Target) && !Directory.Exists(move.Target)) continue;
                    CopyItem(move.Target, move.Source);
                    DeleteItem(move.Target);
                }
                catch
                {
                    // Best-effort rollback; the original error remains the actionable failure.
                }
            }
            throw;
        }
    }

    private async Task EnsureIsolatedDirectoriesCleanAsync(CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        foreach (var relativePath in _configuration.IsolatedDirectories)
        {
            var projectPath = _guard.ProjectPath(relativePath);
            var result = await _svn.StatusIncludingUnversionedAsync(projectPath, cancellationToken);
            if (!result.Succeeded) throw new InvalidOperationException($"无法读取父级目录 {relativePath} 的 SVN 状态：{result.StandardError}");

            var entries = SvnStatusParser.Parse(result.StandardOutput, projectPath);
            if (entries.Count == 0) continue;

            var tracked = entries.Where(entry => entry.Code is not ('?' or 'I')).ToList();
            var unversioned = entries.Where(entry => entry.Code is '?' or 'I').ToList();
            var lines = new List<string> { $"任务映射目录 {relativePath} 存在未提交内容：" };
            if (tracked.Count > 0)
                lines.Add($"  已跟踪修改 {tracked.Count} 项：{FormatStatusEntries(tracked, 5)}");
            if (unversioned.Count > 0)
                lines.Add($"  未版本化或忽略 {unversioned.Count} 项：{FormatStatusEntries(unversioned, 5)}");
            lines.Add(tracked.Count > 0
                ? "请提交或恢复已跟踪修改；未版本化或忽略内容可移出任务映射目录，或使用“从当前修改进入映射”一并迁移。"
                : "当前没有已跟踪修改，只有未版本化或忽略内容。可使用“从当前修改进入映射”把它们一并迁移到任务。");
            problems.Add(string.Join(Environment.NewLine, lines));
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join($"{Environment.NewLine}{Environment.NewLine}", problems));
    }

    private string FormatStatusEntries(IReadOnlyList<SvnStatusEntry> entries, int limit)
    {
        var names = entries.Take(limit).Select(entry =>
        {
            var relative = Path.GetRelativePath(_configuration.ProjectRoot, entry.FullPath).Replace('\\', '/');
            return entry.Code is ' ' or '?' or 'I' ? relative : $"{entry.Code} {relative}";
        });
        var text = string.Join("、", names);
        return entries.Count > limit ? $"{text} 等 {entries.Count} 项" : text;
    }

    private async Task RestoreExcludedDirectoryAsync(string relativePath, string projectPath, string taskPath, int targetRevision, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(projectPath))
        {
            await EnsureSuccess(_svn.RestoreAtRevisionAsync(projectPath, targetRevision, cancellationToken), $"回滚父级目录 {relativePath} 到 r{targetRevision}");
            return;
        }

        // Excluding a directory that still contains ignored files leaves a schedule-delete
        // entry behind. Revert uses the local pristine store and does not need the SVN server.
        var revert = await _svn.RevertAsync(projectPath, cancellationToken);
        if (revert.Succeeded)
        {
            await MoveUnversionedToTaskAsync(projectPath, taskPath, cancellationToken);
            var status = await _svn.StatusIncludingUnversionedAsync(projectPath, cancellationToken);
            if (status.Succeeded)
            {
                var entries = SvnStatusParser.Parse(status.StandardOutput, projectPath);
                if (entries.Count == 0) return;
            }
        }

        await MoveUnversionedToTaskAsync(projectPath, taskPath, cancellationToken);
        if (Directory.Exists(projectPath) && !Directory.EnumerateFileSystemEntries(projectPath).Any())
            Directory.Delete(projectPath, recursive: false);

        await EnsureSuccess(_svn.RestoreAtRevisionAsync(projectPath, targetRevision, cancellationToken), $"回滚父级目录 {relativePath} 到 r{targetRevision}");
    }

    private async Task MoveUnversionedToTaskAsync(string projectPath, string taskPath, CancellationToken cancellationToken)
    {
        var status = await _svn.StatusIncludingUnversionedAsync(projectPath, cancellationToken);
        if (!status.Succeeded) throw new InvalidOperationException($"无法读取残留内容状态：{status.StandardError}");
        var entries = SvnStatusParser.Parse(status.StandardOutput, projectPath)
            .Where(entry => (entry.Code is '?' or 'I') && IsUnder(projectPath, entry.FullPath))
            .OrderBy(entry => entry.FullPath.Length)
            .ToList();
        var movedRoots = new List<string>();
        foreach (var entry in entries)
        {
            if (movedRoots.Any(root => IsUnder(root, entry.FullPath))) continue;
            var target = Path.Combine(taskPath, Path.GetRelativePath(projectPath, entry.FullPath));
            var relative = Path.GetRelativePath(projectPath, entry.FullPath);
            Report($"正在迁移 {relative}…");
            var progress = new FileOperationProgress(Report, relative);
            await Task.Run(() =>
            {
                CopyItem(entry.FullPath, target, progress.Copied, cancellationToken);
                EnsureMirror(entry.FullPath, target, cancellationToken);
                DeleteItem(entry.FullPath, progress.Deleted, cancellationToken);
            }, cancellationToken);
            progress.Complete("迁移");
            movedRoots.Add(entry.FullPath);
        }
    }

    private static async Task WaitForExclusionAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40 && Directory.Exists(path); attempt++)
            await Task.Delay(250, cancellationToken);
    }

    private async Task ExcludeWithCleanupRetryAsync(string projectPath, CancellationToken cancellationToken)
    {
        var result = await _svn.ExcludeAsync(projectPath, cancellationToken);
        if (result.Succeeded) return;

        var cleanup = await _svn.CleanupAsync(_configuration.ProjectRoot, cancellationToken);
        if (cleanup.Succeeded)
        {
            result = await _svn.ExcludeAsync(projectPath, cancellationToken);
            if (result.Succeeded) return;
        }

        var detail = string.IsNullOrWhiteSpace(cleanup.StandardError)
            ? result.StandardError
            : $"{result.StandardError}{Environment.NewLine}cleanup：{cleanup.StandardError}";
        throw new InvalidOperationException($"排除父级目录失败：{detail}");
    }
}
