using SvnWorkspaceManager.Core.FileSystem;
using SvnWorkspaceManager.Core.Models;
using SvnWorkspaceManager.Core.Svn;

namespace SvnWorkspaceManager.Core;

public sealed partial class WorkspaceService
{
    public async Task<RestoreAssessment> GetRestoreAssessmentAsync(CancellationToken cancellationToken = default)
    {
        var state = _store.LoadState();
        if (state.Mode != WorkspaceMode.TaskMapping || string.IsNullOrWhiteSpace(state.ActiveTask))
            return new RestoreAssessment(state.Mode, state.ActiveTask, [], "当前不是任务映射模式，不能恢复到原目录。");

        var directories = new List<RestoreDirectoryAssessment>();
        var exitInterrupted = state.RestoreOperation is { Kind: WorkspaceOperationKinds.Exit };
        var rollbackInterrupted = state.RestoreOperation is { Kind: WorkspaceOperationKinds.RollbackMapping };
        foreach (var relativePath in _configuration.IsolatedDirectories)
        {
            var projectPath = _guard.ProjectPath(relativePath);
            var taskPath = _guard.TaskPath(state.ActiveTask, relativePath);
            var mappingValid = _junctions.IsJunctionTo(projectPath, taskPath);
            var taskInfoResult = await _svn.WorkingCopyInfoAsync(taskPath, cancellationToken);
            var taskInfo = taskInfoResult.Info;
            var taskVersionResult = await _svn.VersionAsync(taskPath, cancellationToken);
            var taskVersion = taskVersionResult.Info;
            var taskStatus = await _svn.StatusAsync(taskPath, cancellationToken);
            var hasBlockingStatus = !taskStatus.Succeeded || SvnStatusParser.ContainsBlockingStatus(taskStatus.StandardOutput, Path.GetDirectoryName(taskPath)!);
            var expectedUrl = CombineSvnUrl(_configuration.SvnTrunkUrl, relativePath);
            var urlMatches = taskInfo is not null && taskInfo.Url.Equals(expectedUrl, StringComparison.OrdinalIgnoreCase);
            var versionIsUniform = taskVersion is not null && taskVersion.IsUniform && !taskVersion.HasSwitched && !taskVersion.IsSparse;
            var targetRevision = taskVersion?.MaxRevision;
            var reason = rollbackInterrupted ? "进入任务映射未完成，请继续回滚"
                : exitInterrupted ? "上次退出任务映射未完成，请继续退出"
                : !mappingValid ? "junction 未指向当前任务"
                : taskInfo is null ? "无法读取任务工作副本 revision"
                : !urlMatches ? "任务 SVN URL 与配置的 trunk 路径不一致"
                : taskVersion is null ? "无法读取任务 svnversion"
                : !versionIsUniform ? $"任务工作区不是统一 revision（svnversion：{taskVersion.MinRevision}:{taskVersion.MaxRevision}{taskVersion.Flags}）"
                : hasBlockingStatus ? "任务工作区存在冲突、缺失、目录受阻或锁定"
                : "可以将父目录对齐到任务 revision 后恢复";
            directories.Add(new RestoreDirectoryAssessment(relativePath, taskInfo?.Revision, expectedUrl, taskInfo?.Url, mappingValid, urlMatches, hasBlockingStatus, versionIsUniform, targetRevision, reason));
        }

        var summary = rollbackInterrupted ? "进入任务映射未完成，请关闭占用程序后继续回滚。"
            : exitInterrupted ? "上次退出任务映射未完成，请重试退出。"
            : directories.Count > 0 && directories.All(directory => directory.IsReady) ? "可以恢复到原目录。"
            : "当前不能安全恢复到原目录。";
        return new RestoreAssessment(state.Mode, state.ActiveTask, directories, summary);
    }

    public async Task RestoreCurrentTaskToProjectAsync(CancellationToken cancellationToken = default)
    {
        var state = _store.LoadState();
        if (state.Mode != WorkspaceMode.TaskMapping || string.IsNullOrWhiteSpace(state.ActiveTask))
            throw new InvalidOperationException("当前不是任务映射模式，不能恢复到原目录。");

        var task = FindTask(state.ActiveTask);
        if (state.RestoreOperation is { Kind: WorkspaceOperationKinds.Exit })
        {
            await ExitTaskMappingAsync(cancellationToken);
            return;
        }
        if (state.RestoreOperation is { Kind: WorkspaceOperationKinds.RollbackMapping })
        {
            await ResumeRollbackMappingAsync(cancellationToken);
            return;
        }
        if (state.RestoreOperation is { Kind: WorkspaceOperationKinds.Restore } interrupted)
        {
            await ResumeRestoreAsync(task, interrupted, cancellationToken);
            return;
        }

        var assessment = await GetRestoreAssessmentAsync(cancellationToken);
        if (!assessment.IsReady)
            throw new InvalidOperationException($"不能恢复到原目录：{assessment.Summary}{Environment.NewLine}{DescribeBlockingRestoreDirectories(assessment.Directories)}");

        var operation = new RestoreOperationState
        {
            TaskName = task.Name,
            Kind = WorkspaceOperationKinds.Restore,
            Phase = "Preparing",
            TargetRevisions = assessment.Directories.ToDictionary(directory => directory.RelativePath, directory => directory.TargetRevision!.Value, StringComparer.OrdinalIgnoreCase)
        };
        SaveOperation(task.Name, operation);
        await ResumeRestoreAsync(task, operation, cancellationToken);
    }

    public async Task ResumeInterruptedOperationAsync(CancellationToken cancellationToken = default)
    {
        var state = _store.LoadState();
        if (state.Mode != WorkspaceMode.TaskMapping || string.IsNullOrWhiteSpace(state.ActiveTask) || state.RestoreOperation is null) return;
        if (state.RestoreOperation.Kind == WorkspaceOperationKinds.Exit)
        {
            await ExitTaskMappingAsync(cancellationToken);
            return;
        }
        if (state.RestoreOperation.Kind == WorkspaceOperationKinds.RollbackMapping)
        {
            await ResumeRollbackMappingAsync(cancellationToken);
            return;
        }
        var task = FindTask(state.ActiveTask);
        await ResumeRestoreAsync(task, state.RestoreOperation, cancellationToken);
    }

    private async Task ResumeRestoreAsync(TaskWorkspaceMetadata task, RestoreOperationState operation, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var relativePath in _configuration.IsolatedDirectories)
            {
                if (operation.CompletedDirectories.Contains(relativePath, StringComparer.OrdinalIgnoreCase)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                operation.CurrentDirectory = relativePath;
                if (!operation.TargetRevisions.TryGetValue(relativePath, out var targetRevision))
                {
                    var version = await _svn.VersionAsync(_guard.TaskPath(task.Name, relativePath), cancellationToken);
                    if (version.Info is null || !version.Info.IsUniform)
                        throw new InvalidOperationException($"任务目录 {relativePath} 不是统一 revision，无法恢复。");
                    targetRevision = version.Info.MaxRevision;
                    operation.TargetRevisions[relativePath] = targetRevision;
                }

                var projectPath = _guard.ProjectPath(relativePath);
                var taskPath = _guard.TaskPath(task.Name, relativePath);
                var taskStatus = await _svn.StatusAsync(taskPath, cancellationToken);
                if (!taskStatus.Succeeded || SvnStatusParser.ContainsBlockingStatus(taskStatus.StandardOutput, Path.GetDirectoryName(taskPath)!))
                    throw new InvalidOperationException($"任务目录 {relativePath} 存在冲突、缺失、目录受阻或锁定，不能恢复。");
                operation.Phase = "AligningTask";
                SaveOperation(task.Name, operation);
                var taskInfo = await _svn.WorkingCopyInfoAsync(taskPath, cancellationToken);
                if (taskInfo.Info is null) throw new InvalidOperationException($"无法读取任务目录 {relativePath}：{taskInfo.Result.StandardError}");
                if (taskInfo.Info.Revision < targetRevision)
                    await EnsureSuccess(_svn.UpdateAtRevisionAsync(taskPath, targetRevision, cancellationToken), $"将任务目录 {relativePath} 更新到 r{targetRevision}");

                operation.Phase = "RemovingMapping";
                SaveOperation(task.Name, operation);
                if (_junctions.IsJunction(projectPath))
                {
                    if (!_junctions.IsJunctionTo(projectPath, taskPath))
                        throw new InvalidOperationException($"目录 {relativePath} 的 junction 未指向当前任务，请先人工确认。");
                    _junctions.RemoveLinkOnly(projectPath, _configuration.TaskRoot);
                }

                operation.Phase = "AligningParent";
                SaveOperation(task.Name, operation);
                await EnsureSuccess(_svn.RestoreAtRevisionAsync(projectPath, targetRevision, cancellationToken), $"将父级目录 {relativePath} 对齐到 r{targetRevision}");

                operation.Phase = "Copying";
                SaveOperation(task.Name, operation);
                await SyncTaskContentAsync(taskPath, projectPath, cancellationToken);
                operation.CompletedDirectories.Add(relativePath);
                SaveOperation(task.Name, operation);
            }

            _store.SaveState(new WorkspaceState { Mode = WorkspaceMode.Normal });
        }
        catch (Exception exception)
        {
            operation.Phase = "Interrupted";
            operation.Error = exception.Message;
            SaveOperation(task.Name, operation);
            throw;
        }
    }

    private static void MirrorDirectory(string source, string target, Action<long>? onFileCopied = null, Action<long>? onFileDeleted = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"任务目录不存在：{source}");
        Directory.CreateDirectory(target);
        var sourceDirectories = Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).Equals(".svn", StringComparison.OrdinalIgnoreCase)).ToList();
        var sourceFiles = Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly).ToList();
        var sourceDirectoryNames = sourceDirectories.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceFileNames = sourceFiles.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(target, "*", SearchOption.TopDirectoryOnly).Where(path => !Path.GetFileName(path).Equals(".svn", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourceDirectoryNames.Contains(Path.GetFileName(directory)))
            {
                Directory.Delete(directory, recursive: true);
                onFileDeleted?.Invoke(0);
            }
        }
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.TopDirectoryOnly).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourceFileNames.Contains(Path.GetFileName(file)))
            {
                var length = new FileInfo(file).Length;
                File.Delete(file);
                onFileDeleted?.Invoke(length);
            }
        }
        foreach (var directory in sourceDirectories)
            MirrorDirectory(directory, Path.Combine(target, Path.GetFileName(directory)), onFileCopied, onFileDeleted, cancellationToken);
        foreach (var file in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyFilePreservingAttributes(file, Path.Combine(target, Path.GetFileName(file)));
            onFileCopied?.Invoke(new FileInfo(file).Length);
        }
    }

    private async Task SyncTaskContentAsync(string taskPath, string projectPath, CancellationToken cancellationToken)
    {
        var status = await _svn.StatusAsync(taskPath, cancellationToken);
        if (!status.Succeeded) throw new InvalidOperationException($"无法读取任务状态：{status.StandardError}");
        var entries = SvnStatusParser.Parse(status.StandardOutput, Path.GetDirectoryName(taskPath)!);

        foreach (var entry in entries.Where(entry => entry.Code is 'D' or 'R').OrderBy(entry => entry.FullPath.Length))
        {
            if (!IsUnder(taskPath, entry.FullPath)) continue;
            var target = Path.Combine(projectPath, Path.GetRelativePath(taskPath, entry.FullPath));
            if (!File.Exists(target) && !Directory.Exists(target)) continue;
            await EnsureSuccess(_svn.DeleteAsync(target, cancellationToken), $"同步删除 {target}");
        }

        Report($"正在同步 {Path.GetFileName(projectPath)} 到原目录…");
        var progress = new FileOperationProgress(Report, Path.GetFileName(projectPath));
        await Task.Run(() => MirrorDirectory(taskPath, projectPath, progress.Copied, progress.Deleted, cancellationToken), cancellationToken);
        progress.Complete("同步");

        foreach (var entry in entries.Where(entry => entry.Code is 'A' or 'R').OrderBy(entry => entry.FullPath.Length))
        {
            if (!IsUnder(taskPath, entry.FullPath)) continue;
            var target = Path.Combine(projectPath, Path.GetRelativePath(taskPath, entry.FullPath));
            if (!File.Exists(target) && !Directory.Exists(target)) continue;
            var result = Directory.Exists(target)
                ? await _svn.AddEmptyDepthAsync(target, cancellationToken)
                : await _svn.AddAsync(target, cancellationToken);
            if (!result.Succeeded) throw new InvalidOperationException($"同步新增 {target} 失败：{result.StandardError}");
        }

        var propertyDiff = await _svn.PropertyDiffAsync(taskPath, cancellationToken);
        if (!propertyDiff.Succeeded)
            throw new InvalidOperationException($"读取任务 SVN 属性变化失败：{propertyDiff.StandardError}");
        if (!string.IsNullOrWhiteSpace(propertyDiff.StandardOutput))
        {
            var patchPath = Path.Combine(_store.ApplicationDirectory, $"restore-{Guid.NewGuid():N}.patch");
            try
            {
                await File.WriteAllTextAsync(patchPath, propertyDiff.StandardOutput, cancellationToken);
                await EnsureSuccess(_svn.PatchAsync(patchPath, projectPath, cancellationToken), "同步 SVN 属性");
            }
            finally { try { File.Delete(patchPath); } catch { } }
        }
    }

    private static void CopyFilePreservingAttributes(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var attributes = File.Exists(target) ? File.GetAttributes(target) : FileAttributes.Normal;
        if (File.Exists(target) && attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
        try { File.Copy(source, target, true); }
        finally { if (File.Exists(target)) File.SetAttributes(target, File.GetAttributes(source)); }
    }
}
