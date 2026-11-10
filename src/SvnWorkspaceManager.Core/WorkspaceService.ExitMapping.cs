using SvnWorkspaceManager.Core.Models;

namespace SvnWorkspaceManager.Core;

public sealed partial class WorkspaceService
{
    public async Task ExitTaskMappingAsync(CancellationToken cancellationToken = default)
    {
        var state = _store.LoadState();
        if (state.Mode == WorkspaceMode.Normal) return;
        if (string.IsNullOrWhiteSpace(state.ActiveTask))
            throw new InvalidOperationException("状态文件处于任务映射模式但没有活动任务，请人工检查状态文件。");
        if (state.RestoreOperation is { Kind: WorkspaceOperationKinds.RollbackMapping })
        {
            await ResumeRollbackMappingAsync(cancellationToken);
            return;
        }
        if (state.RestoreOperation is { Kind: WorkspaceOperationKinds.Restore, Phase: not "Completed" })
            throw new InvalidOperationException("上次恢复到原目录尚未完成，请先继续恢复，或人工处理后清理状态。");

        var task = FindTask(state.ActiveTask);
        var operation = state.RestoreOperation is { Kind: WorkspaceOperationKinds.Exit } existing && existing.TaskName.Equals(task.Name, StringComparison.OrdinalIgnoreCase)
            ? existing
            : new RestoreOperationState { TaskName = task.Name, Kind = WorkspaceOperationKinds.Exit, Phase = "Preparing" };

        try
        {
            foreach (var relativePath in _configuration.IsolatedDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (operation.CompletedDirectories.Contains(relativePath, StringComparer.OrdinalIgnoreCase)) continue;

                operation.CurrentDirectory = relativePath;
                operation.Phase = "RemovingMapping";
                var projectPath = _guard.ProjectPath(relativePath);
                var taskPath = _guard.TaskPath(task.Name, relativePath);
                if (_junctions.IsJunction(projectPath))
                {
                    if (!_junctions.IsJunctionTo(projectPath, taskPath))
                        throw new InvalidOperationException($"目录 {relativePath} 的 junction 未指向当前任务，请先人工确认。");
                    var parentRevision = await ReadDirectParentRevisionAsync(projectPath, cancellationToken);
                    operation.TargetRevisions[relativePath] = parentRevision;
                    SaveOperation(task.Name, operation);
                    _junctions.RemoveLinkOnly(projectPath, _configuration.TaskRoot);
                }
                else if (!operation.TargetRevisions.TryGetValue(relativePath, out _))
                {
                    operation.TargetRevisions[relativePath] = await ReadDirectParentRevisionAsync(projectPath, cancellationToken);
                    SaveOperation(task.Name, operation);
                }

                operation.Phase = "RestoringDirectory";
                SaveOperation(task.Name, operation);
                var targetRevision = operation.TargetRevisions[relativePath];
                await EnsureSuccess(_svn.RestoreAtRevisionAsync(projectPath, targetRevision, cancellationToken), $"将父级目录 {relativePath} 恢复到外层目录 r{targetRevision}");
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

    private async Task<int> ReadDirectParentRevisionAsync(string projectPath, CancellationToken cancellationToken)
    {
        var parent = Directory.GetParent(projectPath)?.FullName
            ?? throw new InvalidOperationException($"无法确定任务映射目录的直接父目录：{projectPath}");
        var info = await _svn.WorkingCopyInfoAsync(parent, cancellationToken);
        return info.Info?.Revision
            ?? throw new InvalidOperationException($"无法读取外层目录 {parent} 的 SVN revision：{info.Result.StandardError}");
    }

    private void SaveOperation(string taskName, RestoreOperationState operation) =>
        _store.SaveState(new WorkspaceState { Mode = WorkspaceMode.TaskMapping, ActiveTask = taskName, RestoreOperation = operation });
}
