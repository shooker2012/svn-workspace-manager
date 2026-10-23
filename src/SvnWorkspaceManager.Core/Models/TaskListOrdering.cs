namespace SvnWorkspaceManager.Core.Models;

public enum TaskListStatus
{
    // 任务本身已就绪的档位：任务内容已经跟隔离目录对齐，
    // 只差父目录里的收尾动作，因此排在“需要更新任务”之前。
    CurrentTask = 0,
    Ready = 1,
    ParentBlocked = 2,
    NeedsMigration = 3,

    // 任务本身尚未就绪：必须先更新任务版本，或任务目录不完整。
    NeedsUpdate = 4,
    Incomplete = 5
}

public enum ParentWorkspaceStatus
{
    Clean,
    UnversionedOnly,
    Blocked
}

public static class TaskListOrdering
{
    public static TaskListStatus GetStatus(
        string taskName,
        bool isValid,
        TaskMappingAssessment assessment,
        string? activeTask,
        ParentWorkspaceStatus parentStatus,
        WorkspaceMode mode)
    {
        if (!string.IsNullOrWhiteSpace(activeTask)
            && taskName.Equals(activeTask, StringComparison.OrdinalIgnoreCase))
            return TaskListStatus.CurrentTask;
        if (!isValid) return TaskListStatus.Incomplete;
        if (!assessment.IsReady) return TaskListStatus.NeedsUpdate;
        if (mode == WorkspaceMode.Normal)
        {
            // 这两种状态的差别只在父目录，任务内容已经就绪，因此都高于 NeedsUpdate。
            if (parentStatus == ParentWorkspaceStatus.Blocked) return TaskListStatus.ParentBlocked;
            if (parentStatus == ParentWorkspaceStatus.UnversionedOnly) return TaskListStatus.NeedsMigration;
        }
        return TaskListStatus.Ready;
    }

    public static IEnumerable<T> OrderForDisplay<T>(
        IEnumerable<T> items,
        Func<T, TaskListStatus> statusSelector,
        Func<T, string> nameSelector) =>
        items.OrderBy(statusSelector)
            .ThenBy(nameSelector, StringComparer.OrdinalIgnoreCase);
}
