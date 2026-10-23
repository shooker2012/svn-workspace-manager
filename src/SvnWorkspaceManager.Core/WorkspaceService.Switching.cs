using SvnWorkspaceManager.Core.Models;

namespace SvnWorkspaceManager.Core;

public sealed partial class WorkspaceService
{
    public async Task SwitchTaskAsync(string taskName, CancellationToken cancellationToken = default)
    {
        var state = _store.LoadState();
        if (state.Mode != WorkspaceMode.TaskMapping || state.ActiveTask is null)
            throw new InvalidOperationException("当前不在任务映射模式。");
        var target = FindTask(taskName);
        if (target.Name.Equals(state.ActiveTask, StringComparison.OrdinalIgnoreCase)) return;

        var assessment = await GetTaskMappingAssessmentAsync(target.Name, cancellationToken);
        if (!assessment.IsReady)
            throw new InvalidOperationException($"不能切换到任务：{assessment.Summary}{Environment.NewLine}{DescribeBlockingDirectories(assessment.Directories)}");

        var previousTask = FindTask(state.ActiveTask);
        foreach (var relativePath in _configuration.IsolatedDirectories)
        {
            var projectPath = _guard.ProjectPath(relativePath);
            if (!_junctions.IsJunctionTo(projectPath, _guard.TaskPath(previousTask.Name, relativePath)))
                throw new InvalidOperationException($"目录 {relativePath} 的映射未指向当前任务，不能切换。");
        }

        var switched = new List<string>();
        try
        {
            foreach (var relativePath in _configuration.IsolatedDirectories)
            {
                var projectPath = _guard.ProjectPath(relativePath);
                _junctions.RemoveLinkOnly(projectPath, _configuration.TaskRoot);
                _junctions.Create(projectPath, _guard.TaskPath(target.Name, relativePath));
                switched.Add(relativePath);
            }
            _store.SaveState(new WorkspaceState { Mode = WorkspaceMode.TaskMapping, ActiveTask = target.Name });
        }
        catch (Exception exception)
        {
            foreach (var relativePath in switched.AsEnumerable().Reverse())
            {
                var projectPath = _guard.ProjectPath(relativePath);
                try
                {
                    if (_junctions.IsJunction(projectPath))
                        _junctions.RemoveLinkOnly(projectPath, _configuration.TaskRoot);
                    _junctions.Create(projectPath, _guard.TaskPath(previousTask.Name, relativePath));
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException($"切换任务失败，且 {relativePath} 回滚失败：{rollbackException.Message}", exception);
                }
            }
            throw;
        }
    }

    private static string DescribeBlockingDirectories(IEnumerable<TaskMappingDirectoryAssessment> directories) =>
        string.Join(Environment.NewLine, directories.Where(directory => !directory.IsReady).Select(directory => $"{directory.RelativePath}：{directory.Reason}"));

    private static string DescribeBlockingRestoreDirectories(IEnumerable<RestoreDirectoryAssessment> directories) =>
        string.Join(Environment.NewLine, directories.Where(directory => !directory.IsReady).Select(directory => $"{directory.RelativePath}：{directory.Reason}"));
}
