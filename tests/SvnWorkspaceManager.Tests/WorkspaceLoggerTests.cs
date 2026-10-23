using Microsoft.VisualStudio.TestTools.UnitTesting;
using SvnWorkspaceManager.Core.Logging;

namespace SvnWorkspaceManager.Tests;

[TestClass]
public sealed class WorkspaceLoggerTests
{
    [TestMethod]
    public void Info_DoesNotThrow_WhenPrimaryLogPathIsBlocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "svnwm-logger-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(Path.Combine(logs, "workspace-manager.log"));

        try
        {
            var logger = new WorkspaceLogger(logs);

            logger.Session("logger fallback test");
            logger.Info("logger fallback test");

            Assert.IsTrue(Directory.Exists(Path.Combine(logs, "workspace-manager.log")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
