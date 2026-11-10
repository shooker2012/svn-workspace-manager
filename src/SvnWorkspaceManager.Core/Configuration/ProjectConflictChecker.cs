using SvnWorkspaceManager.Core.Models;

namespace SvnWorkspaceManager.Core.Configuration;

public static class ProjectConflictChecker
{
    public static void EnsureNoOverlap(WorkspaceConfiguration configuration, IEnumerable<WorkspaceConfiguration> others)
    {
        var mine = Scope(configuration);
        foreach (var other in others)
        {
            var theirs = Scope(other);
            var conflict = mine.FirstOrDefault(left => theirs.Any(right => IsSameOrNested(left, right)));
            if (conflict is not null)
                throw new InvalidOperationException($"项目影响范围重叠：{conflict}。");
        }
    }

    private static IReadOnlyList<string> Scope(WorkspaceConfiguration configuration)
    {
        var root = ProjectIdentity.NormalizeRoot(configuration.ProjectRoot);
        var paths = new List<string> { root, ProjectIdentity.NormalizeRoot(configuration.TaskRoot) };
        paths.AddRange(configuration.IsolatedDirectories.Select(relative => Path.GetFullPath(Path.Combine(root, relative))));
        return paths.Select(ProjectIdentity.NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsSameOrNested(string first, string second)
        => first.Equals(second, StringComparison.OrdinalIgnoreCase)
            || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
