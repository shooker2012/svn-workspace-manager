using Microsoft.VisualStudio.TestTools.UnitTesting;

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
}
