using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace SvnWorkspaceManager.Tests;

[TestClass]
public sealed class CliTests
{
    [TestMethod]
    public async Task Cli_CreatesMapsAndExitsTask()
    {
        using var repo = SvnTestRepository.Create();

        StringAssert.Contains(repo.RunCli("status"), "普通模式");
        repo.RunCli("task", "create", "t1");
        StringAssert.Contains(repo.RunCli("task", "list"), "t1");
        repo.RunCli("mapping", "enable", "t1");
        StringAssert.Contains(repo.RunCli("status"), "任务映射模式");
        repo.RunCli("mapping", "disable");
        StringAssert.Contains(repo.RunCli("status"), "普通模式");

        Assert.IsFalse(repo.Junctions.IsJunction(repo.ProjectPath("Output/Lua")));
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Cli_CodexContext_ReturnsTaskDirectoriesAsJson()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");

        var output = repo.RunCli("codex", "context", "t1", "--json");
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        Assert.AreEqual("task", root.GetProperty("mode").GetString());
        Assert.AreEqual("t1", root.GetProperty("taskName").GetString());
        Assert.AreEqual(Path.GetFullPath(repo.TaskPath("t1", ".")), root.GetProperty("taskRoot").GetString());
        Assert.IsTrue(root.GetProperty("canEdit").GetBoolean());
        Assert.AreEqual(Path.GetFullPath(repo.ProjectRoot), root.GetProperty("workspaceRoot").GetString());
        Assert.AreEqual(2, root.GetProperty("pathMappings").GetArrayLength());
        StringAssert.Contains(root.GetProperty("pathMappings")[0].GetProperty("targetPath").GetString()!, "t1");
    }

    [TestMethod]
    public async Task Cli_CodexWorkspaceInfo_ReportsCurrentMappingWithoutChangingIt()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        var before = repo.Store.LoadState();

        var output = repo.RunCli("codex", "workspace-info", "--json");
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        Assert.AreEqual("Normal", root.GetProperty("mode").GetString());
        Assert.AreEqual(1, root.GetProperty("tasks").GetArrayLength());
        Assert.AreEqual("available", root.GetProperty("tasks")[0].GetProperty("status").GetString());
        Assert.AreEqual(before.Mode, repo.Store.LoadState().Mode);
        Assert.IsFalse(repo.Junctions.IsJunction(repo.ProjectPath("Output/Lua")));
    }

    [TestMethod]
    public async Task Cli_ReadOnlyCodexCommand_DoesNotWriteProjectRegistry()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        Directory.CreateDirectory(Path.Combine(repo.Home, "projects.json.tmp"));

        var output = repo.RunCli("codex", "workspace-info", "--json");
        using var document = JsonDocument.Parse(output);

        Assert.AreEqual(Path.GetFullPath(repo.ProjectRoot), document.RootElement.GetProperty("projectRoot").GetString());
    }

    [TestMethod]
    public async Task Cli_CodexNormalContext_UsesProjectDirectoriesWithoutChangingState()
    {
        using var repo = SvnTestRepository.Create();
        await repo.Service.CreateTaskAsync("t1");
        var before = repo.Store.LoadState();

        var output = repo.RunCli("codex", "context", "--normal", "--json");
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        Assert.AreEqual("normal", root.GetProperty("mode").GetString());
        Assert.IsFalse(root.GetProperty("taskName").ValueKind is JsonValueKind.String);
        Assert.AreEqual(Path.GetFullPath(repo.ProjectPath("Output/Lua")), root.GetProperty("pathMappings")[0].GetProperty("targetPath").GetString());
        Assert.AreEqual(before.Mode, repo.Store.LoadState().Mode);
        Assert.IsFalse(repo.Junctions.IsJunction(repo.ProjectPath("Output/Lua")));
    }
}
