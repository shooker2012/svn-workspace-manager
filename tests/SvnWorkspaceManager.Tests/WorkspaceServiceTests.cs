using Microsoft.VisualStudio.TestTools.UnitTesting;
using SvnWorkspaceManager.Core;
using SvnWorkspaceManager.Core.Logging;
using SvnWorkspaceManager.Core.Models;
using SvnWorkspaceManager.Core.Process;
using SvnWorkspaceManager.Core.Svn;

namespace SvnWorkspaceManager.Tests;

[TestClass]
public sealed class WorkspaceServiceTests
{
    [TestMethod]
    public async Task ProcessRunner_TerminatesCommandAfterTimeout()
    {
        using var repo = SvnTestRepository.Create();
        var runner = new ProcessRunner(new WorkspaceLogger(Path.Combine(repo.Home, "logs")));
        var started = DateTime.UtcNow;

        var exception = await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            runner.RunAsync("cmd.exe", ["/c", "ping", "-n", "20", "127.0.0.1"], repo.Root, TimeSpan.FromSeconds(1)));

        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        StringAssert.Contains(exception.Message, "已终止");
    }

    [TestMethod]
    public async Task CreateTask_ChecksOutEveryIsolatedDirectory()
    {
        using var repo = SvnTestRepository.Create();
        var task = await repo.Service.CreateTaskAsync("t1");

        Assert.IsTrue(File.Exists(Path.Combine(task.RootPath, "Output", "Lua", "init.lua")));
        Assert.IsTrue(File.Exists(Path.Combine(task.RootPath, "Source", "Azure", "main.cpp")));
        Assert.AreEqual(1, repo.Service.ListTasks().Count());
        var assessment = await repo.Service.GetTaskMappingAssessmentAsync("t1");
        Assert.IsTrue(assessment.IsReady, string.Join(Environment.NewLine, assessment.Directories.Select(directory => directory.Reason)));
    }

    [TestMethod]
    public async Task EnableMapping_RejectsUnversionedFilesAndCreatesJunctionWhenClean()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        var unversioned = Path.Combine(repo.ProjectPath("Output/Lua"), "local.lua");
        File.WriteAllText(unversioned, "return 2\n");

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => repo.Service.EnableTaskMappingAsync("t1"));
        StringAssert.Contains(exception.Message, "存在未提交内容");
        StringAssert.Contains(exception.Message, "未版本化");
        StringAssert.Contains(exception.Message, "local.lua");
        StringAssert.Contains(exception.Message, "没有已跟踪修改");
        Assert.IsFalse(repo.Junctions.IsJunction(repo.ProjectPath("Output/Lua")));

        File.Delete(unversioned);
        await repo.Service.EnableTaskMappingAsync("t1");
        Assert.AreEqual(WorkspaceMode.TaskMapping, repo.Store.LoadState().Mode);
        Assert.IsTrue(repo.Junctions.IsJunctionTo(repo.ProjectPath("Output/Lua"), repo.TaskPath("t1", "Output/Lua")));
        Assert.IsTrue(repo.Junctions.IsJunctionTo(repo.ProjectPath("Source/Azure"), repo.TaskPath("t1", "Source/Azure")));
    }

    [TestMethod]
    public async Task StaleTask_CannotEnterMapping()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        repo.CommitFileToTrunk("Output/Lua/init.lua", "return 9\n", "advance trunk");

        var assessment = await repo.Service.GetTaskMappingAssessmentAsync("t1");
        Assert.IsFalse(assessment.IsReady);
        StringAssert.Contains(assessment.Summary, "不能进入");
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => repo.Service.EnableTaskMappingAsync("t1"));
        StringAssert.Contains(exception.Message, "落后于 trunk");
    }

    [TestMethod]
    public async Task Assessment_IgnoresCommitsOutsideIsolatedDirectories()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");

        repo.CommitFileToTrunk("README.txt", "readme changed\n", "unrelated change");

        var assessment = await repo.Service.GetTaskMappingAssessmentAsync("t1");
        Assert.IsTrue(assessment.IsReady, string.Join(Environment.NewLine, assessment.Directories.Select(directory => directory.Reason)));
    }

    [TestMethod]
    public async Task EnableMappingWithUnversionedChanges_MovesFilesIntoSelectedTask()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        var local = Path.Combine(repo.ProjectPath("Output/Lua"), "local.lua");
        File.WriteAllText(local, "return 2\n");

        await repo.Service.EnableTaskMappingWithUnversionedChangesAsync("t1");

        Assert.AreEqual("return 2\n", File.ReadAllText(Path.Combine(repo.TaskPath("t1", "Output/Lua"), "local.lua")));
        Assert.IsTrue(repo.Junctions.IsJunctionTo(repo.ProjectPath("Output/Lua"), repo.TaskPath("t1", "Output/Lua")));
        Assert.AreEqual(WorkspaceMode.TaskMapping, repo.Store.LoadState().Mode);
        Assert.AreEqual("t1", repo.Store.LoadState().ActiveTask);
    }

    [TestMethod]
    public async Task EnableMappingWithUnversionedChanges_RejectsTrackedChanges()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        File.WriteAllText(Path.Combine(repo.ProjectPath("Output/Lua"), "init.lua"), "return 3\n");

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            repo.Service.EnableTaskMappingWithUnversionedChangesAsync("t1"));

        StringAssert.Contains(exception.Message, "已跟踪修改");
    }

    [TestMethod]
    public async Task SwitchTask_AllowsDirtyCurrentTaskAndPreservesWorkspaces()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        await repo.Service.CreateTaskAsync("t2");
        await repo.Service.EnableTaskMappingAsync("t1");
        var t1File = Path.Combine(repo.TaskPath("t1", "Output/Lua"), "init.lua");
        File.WriteAllText(t1File, "return 111\n");

        await repo.Service.SwitchTaskAsync("t2");

        Assert.AreEqual("return 111\n", File.ReadAllText(t1File));
        Assert.IsTrue(repo.Junctions.IsJunctionTo(repo.ProjectPath("Output/Lua"), repo.TaskPath("t2", "Output/Lua")));
        Assert.AreEqual("t2", repo.Store.LoadState().ActiveTask);
    }

    [TestMethod]
    public async Task ExitMapping_RestoresDirectParentRevisionInsteadOfHead()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        await repo.Service.EnableTaskMappingAsync("t1");

        repo.CommitFileToTrunk("Output/Other.txt", "public-2\n", "public change");
        repo.CommitFileToTrunk("Output/Lua/init.lua", "return 99\n", "lua change");
        repo.UpdateProject();
        var parentRevision = repo.WorkingCopyRevision(repo.ProjectPath("Output"));
        var expected = repo.RunSvn("cat", "--non-interactive", "-r", parentRevision.ToString(), repo.TrunkUrl + "/Output/Lua/init.lua");

        repo.CommitFileToTrunk("Output/Lua/init.lua", "return 100\n", "new head");
        await repo.Service.ExitTaskMappingAsync();

        Assert.AreEqual(WorkspaceMode.Normal, repo.Store.LoadState().Mode);
        Assert.IsFalse(repo.Junctions.IsJunction(repo.ProjectPath("Output/Lua")));
        Assert.AreEqual(expected, File.ReadAllText(Path.Combine(repo.ProjectPath("Output/Lua"), "init.lua")));
        Assert.AreNotEqual("return 100\n", File.ReadAllText(Path.Combine(repo.ProjectPath("Output/Lua"), "init.lua")));
        Assert.IsTrue(File.Exists(Path.Combine(repo.TaskPath("t1", "Output/Lua"), "init.lua")));
    }

    [TestMethod]
    public async Task CreateTaskFromCurrentChanges_MigratesCodeAndKeepsPublicChanges()
    {
        using var repo = SvnTestRepository.Create();
        File.WriteAllText(Path.Combine(repo.ProjectPath("Output/Lua"), "init.lua"), "return 7\n");
        File.WriteAllText(Path.Combine(repo.ProjectPath("Output/Lua"), "new.lua"), "return 8\n");
        File.WriteAllText(Path.Combine(repo.ProjectPath("Output/Lua"), "added.lua"), "return 9\n");
        repo.RunSvn("add", Path.Combine(repo.ProjectPath("Output/Lua"), "added.lua"));
        repo.RunSvn("delete", Path.Combine(repo.ProjectPath("Output/Lua"), "old.lua"));
        File.WriteAllText(Path.Combine(repo.ProjectPath("README.txt")), "readme changed\n");
        File.WriteAllText(Path.Combine(repo.ProjectRoot, "public-untracked.txt"), "keep me\n");

        await repo.Service.CreateTaskFromCurrentChangesAsync("migrated");

        Assert.AreEqual("return 7\n", File.ReadAllText(Path.Combine(repo.TaskPath("migrated", "Output/Lua"), "init.lua")));
        Assert.AreEqual("return 8\n", File.ReadAllText(Path.Combine(repo.TaskPath("migrated", "Output/Lua"), "new.lua")));
        Assert.AreEqual("return 9\n", File.ReadAllText(Path.Combine(repo.TaskPath("migrated", "Output/Lua"), "added.lua")));
        Assert.IsFalse(File.Exists(Path.Combine(repo.TaskPath("migrated", "Output/Lua"), "old.lua")));
        Assert.IsTrue(repo.Junctions.IsJunctionTo(repo.ProjectPath("Output/Lua"), repo.TaskPath("migrated", "Output/Lua")));
        Assert.AreEqual("readme changed\n", File.ReadAllText(Path.Combine(repo.ProjectRoot, "README.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(repo.ProjectRoot, "public-untracked.txt")));
        Assert.AreEqual(WorkspaceMode.TaskMapping, repo.Store.LoadState().Mode);
        Assert.AreEqual("migrated", repo.Store.LoadState().ActiveTask);
    }

    [TestMethod]
    public async Task CreateTaskFromCurrentChanges_MigratesIgnoredContentAndEntersMapping()
    {
        using var repo = SvnTestRepository.Create();
        var lua = repo.ProjectPath("Output/Lua");
        repo.RunSvn("propset", "svn:ignore", "generated", lua);
        repo.RunSvn("commit", "--non-interactive", "-m", "ignore generated", repo.ProjectRoot);
        var generated = Path.Combine(lua, "generated", "cache.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        File.WriteAllText(generated, "cache-value\n");

        await repo.Service.CreateTaskFromCurrentChangesAsync("ignored-migrated");

        Assert.IsTrue(repo.Junctions.IsJunctionTo(lua, repo.TaskPath("ignored-migrated", "Output/Lua")));
        Assert.AreEqual("cache-value\n", File.ReadAllText(Path.Combine(repo.TaskPath("ignored-migrated", "Output/Lua"), "generated", "cache.bin")));
        Assert.AreEqual(WorkspaceMode.TaskMapping, repo.Store.LoadState().Mode);
    }

    [TestMethod]
    public async Task CreateTaskFromCurrentChanges_ReportsProgressForIgnoredAndUnversionedContent()
    {
        using var repo = SvnTestRepository.Create();
        var lua = repo.ProjectPath("Output/Lua");
        repo.RunSvn("propset", "svn:ignore", "generated", lua);
        repo.RunSvn("commit", "--non-interactive", "-m", "ignore generated", repo.ProjectRoot);
        var generated = Path.Combine(lua, "generated", "cache.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        File.WriteAllText(generated, "cache-value\n");
        File.WriteAllText(Path.Combine(lua, "local.lua"), "return 2\n");

        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        repo.Service.Progress += messages.Enqueue;

        await repo.Service.CreateTaskFromCurrentChangesAsync("progress");

        var text = string.Join(Environment.NewLine, messages);
        StringAssert.Contains(text, "正在迁移 generated");
        StringAssert.Contains(text, "正在迁移 local.lua");
        StringAssert.Contains(text, "迁移完成");
        Assert.IsTrue(File.Exists(Path.Combine(repo.TaskPath("progress", "Output/Lua"), "local.lua")));
        Assert.AreEqual("cache-value\n", File.ReadAllText(Path.Combine(repo.TaskPath("progress", "Output/Lua"), "generated", "cache.bin")));
        Assert.AreEqual(WorkspaceMode.TaskMapping, repo.Store.LoadState().Mode);
    }

    [TestMethod]
    public async Task ResumeRollbackMapping_RestoresExcludedDirectoryWithIgnoredResidueOffline()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        var lua = repo.ProjectPath("Output/Lua");
        repo.RunSvn("propset", "svn:ignore", "generated", lua);
        repo.RunSvn("commit", "--non-interactive", "-m", "ignore generated", repo.ProjectRoot);
        var generated = Path.Combine(lua, "generated", "cache.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        File.WriteAllText(generated, "cache-value\n");
        var revision = repo.WorkingCopyRevision(lua);
        repo.RunSvn("update", "--non-interactive", "--set-depth", "exclude", lua);
        repo.RunSvn("update", "--non-interactive", "--set-depth", "infinity", lua);
        Assert.IsTrue(Directory.Exists(lua));
        Assert.IsTrue(repo.Status(lua).Contains('C'));

        repo.Store.SaveState(new WorkspaceState
        {
            Mode = WorkspaceMode.TaskMapping,
            ActiveTask = "t1",
            RestoreOperation = new RestoreOperationState
            {
                TaskName = "t1",
                Kind = WorkspaceOperationKinds.RollbackMapping,
                Phase = "Interrupted",
                TargetRevisions = new Dictionary<string, int> { ["Output/Lua"] = revision },
                ExcludedDirectories = ["Output/Lua"]
            }
        });

        await repo.Service.ResumeRollbackMappingAsync();

        Assert.AreEqual(WorkspaceMode.Normal, repo.Store.LoadState().Mode);
        Assert.AreEqual("return 1\n", File.ReadAllText(Path.Combine(lua, "init.lua")));
        Assert.AreEqual("cache-value\n", File.ReadAllText(Path.Combine(repo.TaskPath("t1", "Output/Lua"), "generated", "cache.bin")));
        Assert.IsFalse(repo.Status(lua).Contains('C'));
    }

    [TestMethod]
    public async Task RepairIsolatedDirectories_RestoresInterruptedExcludeAndKeepsIgnoredContent()
    {
        using var repo = SvnTestRepository.Create();
        var lua = repo.ProjectPath("Output/Lua");
        repo.RunSvn("propset", "svn:ignore", "generated", lua);
        repo.RunSvn("commit", "--non-interactive", "-m", "ignore generated", repo.ProjectRoot);
        var generated = Path.Combine(lua, "generated", "cache.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
        File.WriteAllText(generated, "cache-value\n");
        repo.RunSvn("update", "--non-interactive", "--set-depth", "exclude", lua);
        repo.RunSvn("update", "--non-interactive", "--set-depth", "infinity", lua);
        Assert.IsTrue(repo.Status(lua).Contains('C'));

        await repo.Service.RepairIsolatedDirectoriesAsync();

        Assert.AreEqual("return 1\n", File.ReadAllText(Path.Combine(lua, "init.lua")));
        Assert.AreEqual("cache-value\n", File.ReadAllText(generated));
        var entries = SvnStatusParser.Parse(repo.Status(lua), lua);
        Assert.IsFalse(entries.Any(entry => entry.HasBlockingFlag || entry.Code == 'D'));
        Assert.AreEqual(WorkspaceMode.Normal, repo.Store.LoadState().Mode);
    }

    [TestMethod]
    public async Task RestoreCurrentTask_SyncsAddModifyDeleteUnversionedAndProperties()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        await repo.Service.EnableTaskMappingAsync("t1");
        var taskLua = repo.TaskPath("t1", "Output/Lua");
        File.WriteAllText(Path.Combine(taskLua, "init.lua"), "return 42\n");
        File.WriteAllText(Path.Combine(taskLua, "new.lua"), "return 43\n");
        repo.RunSvn("add", Path.Combine(taskLua, "new.lua"));
        repo.RunSvn("delete", Path.Combine(taskLua, "old.lua"));
        File.WriteAllText(Path.Combine(taskLua, "scratch.txt"), "scratch\n");
        repo.RunSvn("propset", "svn:keywords", "Id", Path.Combine(taskLua, "init.lua"));

        await repo.Service.RestoreCurrentTaskToProjectAsync();

        var projectLua = repo.ProjectPath("Output/Lua");
        Assert.AreEqual(WorkspaceMode.Normal, repo.Store.LoadState().Mode);
        Assert.IsFalse(repo.Junctions.IsJunction(projectLua));
        Assert.AreEqual("return 42\n", File.ReadAllText(Path.Combine(projectLua, "init.lua")));
        Assert.IsTrue(File.Exists(Path.Combine(projectLua, "new.lua")));
        Assert.IsFalse(File.Exists(Path.Combine(projectLua, "old.lua")));
        Assert.IsTrue(File.Exists(Path.Combine(projectLua, "scratch.txt")));
        Assert.AreEqual("Id", repo.RunSvn("propget", "svn:keywords", Path.Combine(projectLua, "init.lua")).Trim());

        var status = repo.Status(projectLua);
        StringAssert.Contains(status, "new.lua");
        StringAssert.Contains(status, "old.lua");
        StringAssert.Contains(status, "scratch.txt");
        Assert.IsTrue(File.Exists(Path.Combine(repo.TaskPath("t1", "Output/Lua"), "scratch.txt")));
    }

    [TestMethod]
    public async Task RestoreCurrentTask_UsesTaskRevisionWhenTrunkIsAhead()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        await repo.Service.EnableTaskMappingAsync("t1");
        var taskLua = repo.TaskPath("t1", "Output/Lua");
        var taskRevision = repo.WorkingCopyRevision(taskLua);
        repo.CommitFileToTrunk("Output/Lua/init.lua", "return 88\n", "trunk ahead");
        File.WriteAllText(Path.Combine(taskLua, "init.lua"), "return 55\n");

        await repo.Service.RestoreCurrentTaskToProjectAsync();

        var projectLua = repo.ProjectPath("Output/Lua");
        Assert.AreEqual(WorkspaceMode.Normal, repo.Store.LoadState().Mode);
        Assert.AreEqual("return 55\n", File.ReadAllText(Path.Combine(projectLua, "init.lua")));
        Assert.AreEqual(taskRevision, repo.WorkingCopyRevision(projectLua));
    }

    [TestMethod]
    public async Task InterruptedRestore_CanBeResumed()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        await repo.Service.EnableTaskMappingAsync("t1");
        var taskFile = Path.Combine(repo.TaskPath("t1", "Output/Lua"), "init.lua");
        File.WriteAllText(taskFile, "return 77\n");

        using (var locked = new FileStream(taskFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsExceptionAsync<IOException>(() => repo.Service.RestoreCurrentTaskToProjectAsync());
        }

        var interrupted = repo.Store.LoadState();
        Assert.AreEqual(WorkspaceMode.TaskMapping, interrupted.Mode);
        Assert.IsNotNull(interrupted.RestoreOperation);
        Assert.AreEqual("Interrupted", interrupted.RestoreOperation.Phase);

        await repo.Service.ResumeInterruptedOperationAsync();

        Assert.AreEqual(WorkspaceMode.Normal, repo.Store.LoadState().Mode);
        Assert.AreEqual("return 77\n", File.ReadAllText(Path.Combine(repo.ProjectPath("Output/Lua"), "init.lua")));
    }

    [TestMethod]
    public async Task RemoveTaskRecord_KeepsTaskFiles()
    {
        using var repo = SvnTestRepository.Create();
        var task = await repo.Service.CreateTaskAsync("t1");
        await repo.Service.RemoveTaskRecordAsync("t1");

        Assert.IsFalse(repo.Service.ListTasks().Any());
        Assert.IsTrue(File.Exists(Path.Combine(task.RootPath, "Output", "Lua", "init.lua")));
    }

    [TestMethod]
    public async Task DeleteTaskToRecycleBin_RequestsTaskDirectoryAndRemovesRecord()
    {
        using var repo = SvnTestRepository.Create();
        var task = await repo.Service.CreateTaskAsync("t1");

        await repo.Service.DeleteTaskToRecycleBinAsync("t1");

        CollectionAssert.Contains(
            repo.RecycleBin.DeletedDirectories,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(task.RootPath)));
        Assert.IsFalse(repo.Service.ListTasks().Any());
        Assert.IsTrue(File.Exists(Path.Combine(task.RootPath, "Output", "Lua", "init.lua")));
    }

    [TestMethod]
    public async Task DeleteTaskToRecycleBin_RejectsActiveTask()
    {
        using var repo = SvnTestRepository.Create();
        var task = await repo.Service.CreateTaskAsync("t1");
        await repo.Service.EnableTaskMappingAsync("t1");

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => repo.Service.DeleteTaskToRecycleBinAsync("t1"));

        StringAssert.Contains(exception.Message, "退出任务映射");
        Assert.AreEqual(0, repo.RecycleBin.DeletedDirectories.Count);
        Assert.IsTrue(Directory.Exists(task.RootPath));
        Assert.IsTrue(repo.Service.ListTasks().Any(item => item.Name == "t1"));
    }

    [TestMethod]
    public async Task EnableMapping_RollsBackWhenSecondDirectoryCannotBeExcluded()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        var lockedPath = Path.Combine(repo.ProjectPath("Source/Azure"), "main.cpp");
        Exception? caught;
        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            caught = null;
            try { await repo.Service.EnableTaskMappingAsync("t1"); }
            catch (Exception exception) { caught = exception; }
        }
        Assert.IsNotNull(caught);
        var interrupted = repo.Store.LoadState();
        Assert.AreEqual(WorkspaceMode.TaskMapping, interrupted.Mode);
        Assert.AreEqual(WorkspaceOperationKinds.RollbackMapping, interrupted.RestoreOperation?.Kind);

        await repo.Service.ResumeInterruptedOperationAsync();

        Assert.AreEqual(WorkspaceMode.Normal, repo.Store.LoadState().Mode);
        Assert.IsFalse(repo.Junctions.IsJunction(repo.ProjectPath("Output/Lua")));
        Assert.IsTrue(File.Exists(Path.Combine(repo.ProjectPath("Output/Lua"), "init.lua")), caught?.ToString());
        Assert.IsTrue(File.Exists(Path.Combine(repo.ProjectPath("Source/Azure"), "main.cpp")), caught?.ToString());
    }

    [TestMethod]
    public async Task SvnUnavailable_IsReportedAsOperationFailure()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        var broken = repo.CreateServiceWithSvn("svn-not-installed.exe");

        Exception? caught = null;
        try { await broken.GetTaskMappingAssessmentAsync("t1"); }
        catch (Exception exception) { caught = exception; }
        Assert.IsNotNull(caught);
    }

    [TestMethod]
    public async Task Status_ReportsIsolationDirectoryState()
    {
        using var repo = SvnTestRepository.Create();
        var snapshot = await repo.Service.GetStatusAsync();
        Assert.AreEqual(2, snapshot.IsolationDirectories.Count);
        Assert.IsTrue(snapshot.IsolationDirectories.All(directory => directory.IsClean));
        Assert.IsTrue(snapshot.IsolationDirectories.All(directory => !directory.IsJunction));
        Assert.IsTrue(snapshot.IsolationDirectories.All(directory => directory.Revision is not null));

        var unversioned = Path.Combine(repo.ProjectPath("Output/Lua"), "local.lua");
        File.WriteAllText(unversioned, "return 3\n");
        snapshot = await repo.Service.GetStatusAsync();
        var lua = snapshot.IsolationDirectories.Single(directory => directory.RelativePath == "Output/Lua");
        Assert.AreEqual(0, lua.TrackedChanges);
        Assert.AreEqual(1, lua.UnversionedEntries);
        Assert.IsFalse(lua.IsClean);
        Assert.IsTrue(snapshot.IsolationDirectories.Single(directory => directory.RelativePath == "Source/Azure").IsClean);

        File.Delete(unversioned);
        await repo.Service.CreateTaskAsync("t1");
        await repo.Service.EnableTaskMappingAsync("t1");
        snapshot = await repo.Service.GetStatusAsync();
        Assert.IsTrue(snapshot.IsolationDirectories.All(directory => directory.IsJunction));
        Assert.IsTrue(snapshot.IsolationDirectories.All(directory => directory.LinkTarget!.EndsWith(Path.Combine("t1", directory.RelativePath.Replace('/', Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task OverlappingIsolatedDirectories_AreRejected()
    {
        using var repo = SvnTestRepository.Create("Output", "Output/Lua");
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => repo.Service.CreateTaskAsync("t1"));
        StringAssert.Contains(exception.Message, "互相包含");
    }
}
