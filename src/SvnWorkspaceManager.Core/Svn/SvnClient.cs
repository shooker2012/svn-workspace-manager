using SvnWorkspaceManager.Core.Models;
using SvnWorkspaceManager.Core.Process;
using System.Xml.Linq;

namespace SvnWorkspaceManager.Core.Svn;

public sealed class SvnClient
{
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan CheckoutTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RepositoryProbeTimeout = TimeSpan.FromSeconds(30);
    private readonly ProcessRunner _runner;
    private readonly string _executable;

    public SvnClient(ProcessRunner runner, string executable = "svn.exe")
    {
        _runner = runner;
        _executable = executable;
    }

    public Task<CommandResult> InfoAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["info", path], Path.GetDirectoryName(path), LocalTimeout, cancellationToken);
    public Task<CommandResult> InfoTextAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["info", path], Path.GetDirectoryName(path), LocalTimeout, cancellationToken);
    public async Task<(CommandResult Result, SvnWorkingCopyInfo? Info)> WorkingCopyInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["info", "--xml", path], Path.GetDirectoryName(path), LocalTimeout, cancellationToken);
        if (!result.Succeeded) return (result, null);
        try
        {
            var entry = XDocument.Parse(result.StandardOutput).Root?.Element("entry");
            var revision = int.TryParse(entry?.Attribute("revision")?.Value, out var parsedRevision) ? parsedRevision : (int?)null;
            var url = entry?.Element("url")?.Value.Trim();
            if (revision is null || string.IsNullOrWhiteSpace(url)) return (result, null);
            return (result, new SvnWorkingCopyInfo(revision.Value, url));
        }
        catch (System.Xml.XmlException)
        {
            return (result, null);
        }
    }
    public async Task<(CommandResult Result, int? Revision)> RepositoryRevisionAsync(string url, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["info", "--show-item", "revision", url], null, RepositoryProbeTimeout, cancellationToken);
        return int.TryParse(result.StandardOutput.Trim(), out var revision) ? (result, revision) : (result, null);
    }
    public async Task<(CommandResult Result, int? Revision)> RepositoryLastChangedRevisionAsync(string url, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["info", "--show-item", "last-changed-revision", url], null, RepositoryProbeTimeout, cancellationToken);
        return int.TryParse(result.StandardOutput.Trim(), out var revision) ? (result, revision) : (result, null);
    }
    public async Task<(CommandResult Result, SvnVersionInfo? Info)> VersionAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync("svnversion.exe", ["--no-newline", path], Path.GetDirectoryName(path), LocalTimeout, cancellationToken);
        if (!result.Succeeded) return (result, null);
        var text = result.StandardOutput.Trim();
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?<min>\d+)(?::(?<max>\d+))?(?<flags>[A-Z]*)$");
        if (!match.Success || !int.TryParse(match.Groups["min"].Value, out var min)) return (result, null);
        var max = match.Groups["max"].Success && int.TryParse(match.Groups["max"].Value, out var parsedMax) ? parsedMax : min;
        var flags = match.Groups["flags"].Value;
        return (result, new SvnVersionInfo(min, max, min == max, flags.Contains('S'), flags.Contains('P'), flags));
    }
    public Task<CommandResult> StatusAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["status", "--ignore-externals", path], Path.GetDirectoryName(path), LocalTimeout, cancellationToken);
    public Task<CommandResult> RemoteStatusAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["status", "--ignore-externals", "--show-updates", path], Path.GetDirectoryName(path), NetworkTimeout, cancellationToken);
    public Task<CommandResult> StatusIncludingUnversionedAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["status", "--ignore-externals", "--no-ignore", path], Path.GetDirectoryName(path), LocalTimeout, cancellationToken);
    public Task<CommandResult> UpdateAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["update", "--accept", "postpone", path], Path.GetDirectoryName(path), NetworkTimeout, cancellationToken);
    public Task<CommandResult> UpdateAtRevisionAsync(string path, int revision, CancellationToken cancellationToken = default) => RunAsync(["update", "--accept", "postpone", "--revision", revision.ToString(), path], Path.GetDirectoryName(path), NetworkTimeout, cancellationToken);
    public Task<CommandResult> ExcludeAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["update", "--set-depth", "exclude", path], Path.GetDirectoryName(path), NetworkTimeout, cancellationToken);
    public Task<CommandResult> RestoreAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["update", "--set-depth", "infinity", path], Path.GetDirectoryName(path), NetworkTimeout, cancellationToken);
    public Task<CommandResult> RestoreAtRevisionAsync(string path, int revision, CancellationToken cancellationToken = default) => RunAsync(["update", "--revision", revision.ToString(), "--set-depth", "infinity", path], Path.GetDirectoryName(path), NetworkTimeout, cancellationToken);
    public Task<CommandResult> RevertAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["revert", "--depth", "infinity", path], Path.GetDirectoryName(path), LocalTimeout, cancellationToken);
    public Task<CommandResult> CleanupAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["cleanup", path], Path.GetDirectoryName(path), LocalTimeout, cancellationToken);
    public Task<CommandResult> CheckoutAsync(string url, string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return RunAsync(["checkout", url, path], Path.GetDirectoryName(path), CheckoutTimeout, cancellationToken);
    }

    public Task<CommandResult> DiffAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["diff", "--internal-diff", "."], path, LocalTimeout, cancellationToken);
    public Task<CommandResult> PropertyDiffAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["diff", "--internal-diff", "--properties-only", "."], path, LocalTimeout, cancellationToken);
    public Task<CommandResult> PatchAsync(string patchPath, string targetPath, CancellationToken cancellationToken = default) => RunAsync(["patch", patchPath], targetPath, LocalTimeout, cancellationToken);
    public Task<CommandResult> AddAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["add", "--force", path], Path.GetDirectoryName(path), LocalTimeout, cancellationToken);
    public Task<CommandResult> AddEmptyDepthAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["add", "--force", "--depth", "empty", path], Path.GetDirectoryName(path), LocalTimeout, cancellationToken);
    public Task<CommandResult> DeleteAsync(string path, CancellationToken cancellationToken = default) => RunAsync(["delete", "--force", path], Path.GetDirectoryName(path), LocalTimeout, cancellationToken);

    private Task<CommandResult> RunAsync(IEnumerable<string> arguments, string? workingDirectory, TimeSpan timeout, CancellationToken cancellationToken) =>
        _runner.RunAsync(_executable, NonInteractive(arguments), workingDirectory, timeout, cancellationToken);

    private static IReadOnlyList<string> NonInteractive(IEnumerable<string> arguments)
    {
        var list = arguments.ToList();
        if (list.Count > 0 && !list.Contains("--non-interactive", StringComparer.OrdinalIgnoreCase))
            list.Insert(1, "--non-interactive");
        return list;
    }
}

public sealed record SvnVersionInfo(int MinRevision, int MaxRevision, bool IsUniform, bool HasSwitched, bool IsSparse, string Flags);
