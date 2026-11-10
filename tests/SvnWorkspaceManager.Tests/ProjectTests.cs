using SvnWorkspaceManager.Core.Configuration;
using SvnWorkspaceManager.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SvnWorkspaceManager.Tests;

[TestClass]
public sealed class ProjectTests
{
    [TestMethod]
    public void ProjectIdentity_IsStableForPathSpelling()
    {
        var root = Path.Combine(Path.GetTempPath(), "svn-manager-project");
        Assert.AreEqual(ProjectIdentity.FromRoot(root), ProjectIdentity.FromRoot(root + Path.DirectorySeparatorChar));
        Assert.AreEqual(ProjectIdentity.FromRoot(root), ProjectIdentity.FromRoot(Path.Combine(root, ".")));
    }

    [TestMethod]
    public void ProjectConflictChecker_RejectsOverlappingRoots()
    {
        var first = new WorkspaceConfiguration { ProjectRoot = @"C:\work\app", TaskRoot = @"C:\work\tasks" };
        var second = new WorkspaceConfiguration { ProjectRoot = @"C:\work\app\tools", TaskRoot = @"C:\other\tasks" };
        Assert.ThrowsException<InvalidOperationException>(() => ProjectConflictChecker.EnsureNoOverlap(first, [second]));
    }

    [TestMethod]
    public void ProjectRegistry_RoundTripsAndUpdatesLastOpened()
    {
        var root = Path.Combine(Path.GetTempPath(), "svn-manager-registry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new ProjectRegistry(root);
            var context = ProjectLocator.CreateContext(Path.Combine(root, "project"), root);
            registry.Register(context);
            Assert.AreEqual(context.Id, registry.List().Single().Id);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
