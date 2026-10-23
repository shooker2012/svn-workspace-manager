using System.Text.Json.Serialization;

namespace SvnWorkspaceManager.Core.Models;

public sealed class WorkspaceConfiguration
{
    public int Version { get; set; } = 1;
    public string ProjectRoot { get; set; } = string.Empty;
    public string TaskRoot { get; set; } = string.Empty;
    public string SvnTrunkUrl { get; set; } = string.Empty;
    public string SvnExecutable { get; set; } = "svn.exe";
    public List<string> IsolatedDirectories { get; set; } = ["Output/Lua", "Source/Azure"];
    public List<string> MonitoredProcesses { get; set; } = [];
}

public sealed class WorkspaceState
{
    public int Version { get; set; } = 1;
    public WorkspaceMode Mode { get; set; } = WorkspaceMode.Normal;
    public string? ActiveTask { get; set; }
    public RestoreOperationState? RestoreOperation { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RestoreOperationState
{
    public string TaskName { get; set; } = string.Empty;
    public string Kind { get; set; } = WorkspaceOperationKinds.Restore;
    public string Phase { get; set; } = string.Empty;
    public string? CurrentDirectory { get; set; }
    public Dictionary<string, int> TargetRevisions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ExcludedDirectories { get; set; } = [];
    public List<string> LinkedDirectories { get; set; } = [];
    public List<string> CompletedDirectories { get; set; } = [];
    public string? Error { get; set; }
}

public static class WorkspaceOperationKinds
{
    public const string Restore = "RestoreToProject";
    public const string Exit = "ExitTaskMapping";
    public const string RollbackMapping = "RollbackMapping";
}

public enum WorkspaceMode
{
    Normal,
    TaskMapping
}

public sealed class TaskWorkspaceMetadata
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string SvnUrl { get; set; } = string.Empty;
    public List<string> IsolatedDirectories { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record DirectoryMapping(string RelativePath, string LinkPath, string TargetPath, bool IsJunction);

public sealed record IsolationDirectorySnapshot(
    string RelativePath,
    bool Exists,
    bool IsJunction,
    string? LinkTarget,
    int? Revision,
    string? Url,
    int TrackedChanges,
    int UnversionedEntries,
    string? Error)
{
    public int TotalChanges => TrackedChanges + UnversionedEntries;
    public bool IsClean => Exists && Error is null && TotalChanges == 0;
}

public sealed record RestoreDirectoryAssessment(
    string RelativePath,
    int? TaskRevision,
    string? ExpectedUrl,
    string? TaskUrl,
    bool MappingValid,
    bool UrlMatches,
    bool HasBlockingStatus,
    bool TaskVersionIsUniform,
    int? TargetRevision,
    string Reason)
{
    public bool IsReady => MappingValid && UrlMatches && TaskVersionIsUniform && !HasBlockingStatus && TargetRevision is not null;
}

public sealed record RestoreAssessment(
    WorkspaceMode Mode,
    string? ActiveTask,
    IReadOnlyList<RestoreDirectoryAssessment> Directories,
    string Summary)
{
    public bool IsReady => Mode == WorkspaceMode.TaskMapping && !string.IsNullOrWhiteSpace(ActiveTask) && Directories.Count > 0 && Directories.All(directory => directory.IsReady);
}

public sealed record SvnWorkingCopyInfo(int Revision, string Url);

public sealed record TaskMappingDirectoryAssessment(
    string RelativePath,
    int? TaskRevision,
    int? TargetRevision,
    string? ExpectedUrl,
    string? TaskUrl,
    bool RevisionMatches,
    bool UrlMatches,
    bool HasBlockingStatus,
    string Reason)
{
    public bool IsReady => RevisionMatches && UrlMatches && !HasBlockingStatus;
}

public sealed record TaskMappingAssessment(
    string TaskName,
    IReadOnlyList<TaskMappingDirectoryAssessment> Directories,
    string Summary)
{
    public bool IsReady => Directories.Count > 0 && Directories.All(directory => directory.IsReady);
}

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration)
{
    [JsonIgnore]
    public bool Succeeded => ExitCode == 0;
}
