using SvnWorkspaceManager.Core.Models;

namespace SvnWorkspaceManager.Core;

public sealed partial class WorkspaceService
{
    public CodexTaskContext GetCodexTaskContext(string taskName)
    {
        ValidateIsolationLayout();
        var task = FindTask(taskName);
        var taskRoot = _guard.TaskPath(task.Name, ".");
        return CreateCodexContext("task", task.Name, taskRoot);
    }

    public CodexTaskContext GetCodexNormalContext()
    {
        ValidateIsolationLayout();
        return CreateCodexContext("normal", null, null);
    }

    private CodexTaskContext CreateCodexContext(string mode, string? taskName, string? taskRoot)
    {
        var mappings = _configuration.IsolatedDirectories.Select(relativePath =>
        {
            var projectPath = _guard.ProjectPath(relativePath);
            var targetPath = taskRoot is null ? projectPath : _guard.TaskPath(taskName!, relativePath);
            return new CodexPathMapping(relativePath, projectPath, targetPath, Directory.Exists(targetPath));
        }).ToList();

        return new CodexTaskContext(
            mode,
            taskName,
            taskRoot,
            _guard.ProjectRoot,
            _guard.ProjectRoot,
            [.. _configuration.IsolatedDirectories],
            mappings,
            mappings.All(mapping => mapping.Exists));
    }

    public CodexWorkspaceInfo GetCodexWorkspaceInfo()
    {
        ValidateIsolationLayout();
        var state = _store.LoadState();
        var taskNames = ListTasks().ToList();
        var mappings = _configuration.IsolatedDirectories.Select(relativePath =>
        {
            var projectPath = _guard.ProjectPath(relativePath);
            var isJunction = _junctions.IsJunction(projectPath);
            var targetPath = isJunction ? _junctions.GetTarget(projectPath) : null;
            var targetTask = targetPath is null ? null : FindTaskForTarget(targetPath, taskNames);
            var isValid = state.Mode == WorkspaceMode.TaskMapping
                && !string.IsNullOrWhiteSpace(state.ActiveTask)
                && targetTask?.Equals(state.ActiveTask, StringComparison.OrdinalIgnoreCase) == true;
            return new CodexWorkspaceMapping(relativePath, projectPath, isJunction, targetPath, targetTask, isValid);
        }).ToList();

        var tasks = taskNames.Select(task => new CodexWorkspaceTask(
            task.Name,
            _guard.TaskPath(task.Name, "."),
            state.Mode == WorkspaceMode.TaskMapping && state.ActiveTask?.Equals(task.Name, StringComparison.OrdinalIgnoreCase) == true
                ? "active"
                : "available"))
            .ToList();

        return new CodexWorkspaceInfo(
            _guard.ProjectRoot,
            _guard.TaskRoot,
            state.Mode,
            state.ActiveTask,
            [.. _configuration.IsolatedDirectories],
            tasks,
            mappings,
            state.RestoreOperation,
            state.RestoreOperation is not null && state.RestoreOperation.Phase is not "Completed");
    }

    private string? FindTaskForTarget(string targetPath, IReadOnlyList<TaskWorkspaceMetadata> tasks)
    {
        var normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        foreach (var task in tasks)
        {
            var taskRoot = Path.TrimEndingDirectorySeparator(_guard.TaskPath(task.Name, "."));
            if (normalizedTarget.Equals(taskRoot, StringComparison.OrdinalIgnoreCase)
                || normalizedTarget.StartsWith(taskRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return task.Name;
        }

        return null;
    }
}
