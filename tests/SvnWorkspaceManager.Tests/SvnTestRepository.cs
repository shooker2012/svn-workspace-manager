using System.Diagnostics;
using SvnWorkspaceManager.Core;
using SvnWorkspaceManager.Core.Configuration;
using SvnWorkspaceManager.Core.FileSystem;
using SvnWorkspaceManager.Core.Logging;
using SvnWorkspaceManager.Core.Models;
using SvnWorkspaceManager.Core.Process;
using SvnWorkspaceManager.Core.Svn;

namespace SvnWorkspaceManager.Tests;

public sealed class SvnTestRepository : IDisposable
{
    private static readonly string ToolRoot = FindToolRoot();
    private static readonly string TestRunsRoot = Path.Combine(ToolRoot, ".test-runs");
    private int _counter;

    private SvnTestRepository(string root) => Root = root;

    public string Root { get; }
    public string RepoPath { get; private set; } = string.Empty;
    public string ProjectRoot { get; private set; } = string.Empty;
    public string TaskRoot { get; private set; } = string.Empty;
    public string Home { get; private set; } = string.Empty;
    public string TrunkUrl => new Uri(RepoPath).AbsoluteUri.TrimEnd('/') + "/trunk";
    public WorkspaceConfiguration Configuration { get; private set; } = new();
    public WorkspaceStore Store { get; private set; } = null!;
    public WorkspaceService Service { get; private set; } = null!;
    public JunctionManager Junctions { get; } = new();
    public RecordingRecycleBin RecycleBin { get; } = new();

    public WorkspaceService CreateServiceWithSvn(string executable)
    {
        var configuration = new WorkspaceConfiguration
        {
            ProjectRoot = Configuration.ProjectRoot,
            TaskRoot = Configuration.TaskRoot,
            SvnTrunkUrl = Configuration.SvnTrunkUrl,
            SvnExecutable = executable,
            IsolatedDirectories = [.. Configuration.IsolatedDirectories]
        };
        var logger = new WorkspaceLogger(Path.Combine(Home, "logs"));
        return new WorkspaceService(Store, configuration, new SvnClient(new ProcessRunner(logger), executable), new JunctionManager(), RecycleBin);
    }

    public static SvnTestRepository Create(params string[] isolatedDirectories)
    {
        Directory.CreateDirectory(TestRunsRoot);
        var root = Path.Combine(TestRunsRoot, "t-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var repository = new SvnTestRepository(root);
        repository.Initialize(isolatedDirectories.Length == 0 ? ["Output/Lua", "Source/Azure"] : isolatedDirectories);
        return repository;
    }

    public string ProjectPath(string relativePath) => Path.Combine(ProjectRoot, relativePath);
    public string TaskPath(string task, string relativePath) => Path.Combine(TaskRoot, task, relativePath);

    public string RunSvn(params string[] arguments) => RunTool("svn.exe", Root, arguments);

    public string RunSvnIn(string workingDirectory, params string[] arguments) => RunTool("svn.exe", workingDirectory, arguments);

    public int RepositoryRevision() => int.Parse(RunSvn("info", "--non-interactive", "--show-item", "revision", TrunkUrl).Trim());

    public int WorkingCopyRevision(string path) => int.Parse(RunSvn("info", "--non-interactive", "--show-item", "revision", path).Trim());

    public string Status(string path) => RunSvn("status", "--ignore-externals", path);

    public void CommitFileToTrunk(string relativePath, string content, string message)
    {
        var workingCopy = Path.Combine(Root, $"trunk-wc-{++_counter}");
        RunSvn("checkout", "--non-interactive", TrunkUrl, workingCopy);
        var file = Path.Combine(workingCopy, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        try { RunSvnIn(workingCopy, "add", "--force", "--parents", relativePath); } catch { }
        RunSvnIn(workingCopy, "commit", "--non-interactive", "-m", message, workingCopy);
        DeleteDirectoryRobust(workingCopy);
    }

    public void UpdateProject() => RunSvn("update", "--non-interactive", ProjectRoot);

    private void Initialize(string[] isolatedDirectories)
    {
        RepoPath = Path.Combine(Root, "repo");
        RunTool("svnadmin.exe", Root, "create", RepoPath);
        RunSvn("mkdir", "--non-interactive", "-m", "init", TrunkUrl);

        ProjectRoot = Path.Combine(Root, "project");
        RunSvn("checkout", "--non-interactive", TrunkUrl, ProjectRoot);
        Directory.CreateDirectory(Path.Combine(ProjectRoot, "Output", "Lua"));
        Directory.CreateDirectory(Path.Combine(ProjectRoot, "Source", "Azure"));
        File.WriteAllText(Path.Combine(ProjectRoot, "Output", "Lua", "init.lua"), "return 1\n");
        File.WriteAllText(Path.Combine(ProjectRoot, "Output", "Lua", "old.lua"), "return 0\n");
        File.WriteAllText(Path.Combine(ProjectRoot, "Output", "Other.txt"), "public\n");
        File.WriteAllText(Path.Combine(ProjectRoot, "Source", "Azure", "main.cpp"), "int main() { return 0; }\n");
        File.WriteAllText(Path.Combine(ProjectRoot, "README.txt"), "readme\n");
        RunSvnIn(ProjectRoot, "add", "Output", "Source", "README.txt");
        RunSvnIn(ProjectRoot, "commit", "--non-interactive", "-m", "initial", ProjectRoot);

        TaskRoot = Path.Combine(Root, "tasks");
        Home = Path.Combine(Root, "home");
        Configuration = new WorkspaceConfiguration
        {
            ProjectRoot = ProjectRoot,
            TaskRoot = TaskRoot,
            SvnTrunkUrl = TrunkUrl,
            IsolatedDirectories = [.. isolatedDirectories]
        };
        Store = new WorkspaceStore(Home);
        Store.SaveConfiguration(Configuration);
        var logger = new WorkspaceLogger(Path.Combine(Home, "logs"));
        Service = new WorkspaceService(Store, Configuration, new SvnClient(new ProcessRunner(logger), Configuration.SvnExecutable), Junctions, RecycleBin);
    }

    public string RunCli(params string[] arguments)
    {
        var executable = FindCliExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = ToolRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["SVN_WORKSPACE_MANAGER_HOME"] = Home;
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 CLI。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"CLI {string.Join(' ', arguments)} 退出码 {process.ExitCode}：{output}{error}");
        return output;
    }

    private static string FindCliExecutable()
    {
        var bin = Path.Combine(ToolRoot, "src", "SvnWorkspaceManager.Cli", "bin");
        var executable = Directory.EnumerateFiles(bin, "SvnWorkspaceManager.Cli.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return executable ?? throw new FileNotFoundException("找不到 CLI 可执行文件，请先构建解决方案。", bin);
    }

    private static string RunTool(string fileName, string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动 {fileName}。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} {string.Join(' ', arguments)} 退出码 {process.ExitCode}{Environment.NewLine}{output}{error}");
        return output;
    }

    private static string FindToolRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SvnWorkspaceManager.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("无法定位 SvnWorkspaceManager.slnx。");
    }

    public void Dispose()
    {
        try
        {
            foreach (var relativePath in Configuration.IsolatedDirectories)
            {
                var projectPath = Path.Combine(ProjectRoot, relativePath);
                if (Junctions.IsJunction(projectPath)) Junctions.RemoveLinkOnly(projectPath, TaskRoot);
            }
        }
        catch { }

        try
        {
            var fullRoot = Path.GetFullPath(Root);
            var allowedPrefix = Path.GetFullPath(TestRunsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullRoot.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullRoot))
                DeleteDirectoryRobust(fullRoot);
        }
        catch { }
    }

    private static void DeleteDirectoryRobust(string path)
    {
        if (!Directory.Exists(path)) return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", options))
        {
            try
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
            catch { }
        }
        foreach (var directory in Directory.EnumerateDirectories(path, "*", options).OrderByDescending(item => item.Length))
        {
            try
            {
                var info = new DirectoryInfo(directory);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) info.Delete(false);
            }
            catch { }
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch when (attempt < 2)
            {
                System.Threading.Thread.Sleep(200);
            }
        }
    }
}

public sealed class RecordingRecycleBin : IRecycleBin
{
    public List<string> DeletedDirectories { get; } = [];

    public void DeleteDirectory(string path) =>
        DeletedDirectories.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
}
