namespace SvnWorkspaceManager.Core.Models;

public sealed record CodexPathMapping(
    string RelativePath,
    string ProjectPath,
    string TargetPath,
    bool Exists);

public sealed record CodexTaskContext(
    string Mode,
    string? TaskName,
    string? TaskRoot,
    string WorkspaceRoot,
    string ProjectRoot,
    IReadOnlyList<string> IsolatedDirectories,
    IReadOnlyList<CodexPathMapping> PathMappings,
    bool CanEdit);

public sealed record CodexWorkspaceTask(
    string Name,
    string RootPath,
    string Status);

public sealed record CodexWorkspaceMapping(
    string RelativePath,
    string ProjectPath,
    bool IsJunction,
    string? TargetPath,
    string? TargetTask,
    bool IsValid);

public sealed record CodexWorkspaceInfo(
    string ProjectRoot,
    string TaskRoot,
    WorkspaceMode Mode,
    string? ActiveTask,
    IReadOnlyList<string> IsolatedDirectories,
    IReadOnlyList<CodexWorkspaceTask> Tasks,
    IReadOnlyList<CodexWorkspaceMapping> Mappings,
    RestoreOperationState? Operation,
    bool HasIncompleteOperation);
