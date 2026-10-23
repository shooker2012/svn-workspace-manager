using Microsoft.VisualStudio.TestTools.UnitTesting;
using SvnWorkspaceManager.Core.Models;

namespace SvnWorkspaceManager.Tests;

[TestClass]
public sealed class TaskListOrderingTests
{
    [TestMethod]
    public void OrderForDisplay_SortsByStatusThenName()
    {
        TaskListEntry[] entries =
        [
            new("a-incomplete", TaskListStatus.Incomplete),
            new("a-parent", TaskListStatus.ParentBlocked),
            new("b-needs-update", TaskListStatus.NeedsUpdate),
            new("a-needs-update", TaskListStatus.NeedsUpdate),
            new("b-needs-migration", TaskListStatus.NeedsMigration),
            new("b-ready", TaskListStatus.Ready),
            new("a-ready", TaskListStatus.Ready),
            new("current", TaskListStatus.CurrentTask)
        ];

        var ordered = TaskListOrdering.OrderForDisplay(
                entries,
                entry => entry.Status,
                entry => entry.Name)
            .Select(entry => entry.Name)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "current", "a-ready", "b-ready", "a-parent", "b-needs-migration", "a-needs-update", "b-needs-update", "a-incomplete" },
            ordered);
    }

    [TestMethod]
    public void GetStatus_UsesDisplayPriorityAndParentState()
    {
        var ready = ReadyAssessment();
        var needsUpdate = new TaskMappingAssessment("needs-update", [], "任务落后于 trunk");

        Assert.AreEqual(
            TaskListStatus.CurrentTask,
            TaskListOrdering.GetStatus("current", false, needsUpdate, "current", ParentWorkspaceStatus.Blocked, WorkspaceMode.Normal));
        Assert.AreEqual(
            TaskListStatus.ParentBlocked,
            TaskListOrdering.GetStatus("ready", true, ready, null, ParentWorkspaceStatus.Blocked, WorkspaceMode.Normal));
        Assert.AreEqual(
            TaskListStatus.NeedsMigration,
            TaskListOrdering.GetStatus("ready", true, ready, null, ParentWorkspaceStatus.UnversionedOnly, WorkspaceMode.Normal));
        Assert.AreEqual(
            TaskListStatus.Ready,
            TaskListOrdering.GetStatus("ready", true, ready, null, ParentWorkspaceStatus.Clean, WorkspaceMode.Normal));
        Assert.AreEqual(
            TaskListStatus.NeedsUpdate,
            TaskListOrdering.GetStatus("needs-update", true, needsUpdate, null, ParentWorkspaceStatus.Clean, WorkspaceMode.Normal));
        Assert.AreEqual(
            TaskListStatus.Incomplete,
            TaskListOrdering.GetStatus("incomplete", false, ready, null, ParentWorkspaceStatus.Clean, WorkspaceMode.Normal));
    }

    [TestMethod]
    public void TaskListStatus_RanksParentBlockedInTaskReadyTier()
    {
        // “任务已就绪、只差父目录清理”与任务版本无关，必须排在需要更新任务之前。
        Assert.IsTrue(TaskListStatus.CurrentTask < TaskListStatus.Ready);
        Assert.IsTrue(TaskListStatus.Ready < TaskListStatus.ParentBlocked);
        Assert.IsTrue(TaskListStatus.ParentBlocked < TaskListStatus.NeedsMigration);
        Assert.IsTrue(TaskListStatus.NeedsMigration < TaskListStatus.NeedsUpdate);
        Assert.IsTrue(TaskListStatus.NeedsUpdate < TaskListStatus.Incomplete);
    }

    private static TaskMappingAssessment ReadyAssessment() => new(
        "ready",
        [new TaskMappingDirectoryAssessment("Output/Lua", 10, 10, "expected", "actual", true, true, false, "已对齐")],
        "可以进入映射");

    private sealed record TaskListEntry(string Name, TaskListStatus Status);
}
