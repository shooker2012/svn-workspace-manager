using SvnWorkspaceManager.Core.Models;

namespace SvnWorkspaceManager.Core.Configuration;

public static class ProjectLocator
{
    public static string DefaultApplicationDirectory => Environment.GetEnvironmentVariable("SVN_WORKSPACE_MANAGER_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Azure", "SvnWorkspaceManager");

    public static ProjectContext CreateContext(string projectRoot, string? applicationDirectory = null)
    {
        var normalized = ProjectIdentity.NormalizeRoot(projectRoot);
        var id = ProjectIdentity.FromRoot(normalized);
        var app = applicationDirectory ?? DefaultApplicationDirectory;
        var projectDirectory = Path.Combine(Path.GetFullPath(app), "projects", id);
        return new ProjectContext(id, new DirectoryInfo(normalized).Name, normalized, projectDirectory);
    }

    public static ProjectContext FromConfiguration(WorkspaceConfiguration configuration, string? applicationDirectory = null)
        => CreateContext(configuration.ProjectRoot, applicationDirectory);

    public static bool TryMigrateLegacy(string applicationDirectory, ProjectContext context)
    {
        var legacyConfigurationPath = Path.Combine(applicationDirectory, "config.json");
        if (!File.Exists(legacyConfigurationPath) || File.Exists(context.ConfigurationPath)) return false;
        var legacyStore = new WorkspaceStore(applicationDirectory);
        var configuration = legacyStore.LoadConfiguration();
        if (!ProjectIdentity.NormalizeRoot(configuration.ProjectRoot).Equals(context.ProjectRoot, StringComparison.OrdinalIgnoreCase)) return false;
        var projectStore = new WorkspaceStore(context.ApplicationDirectory);
        projectStore.SaveConfiguration(configuration);
        if (File.Exists(legacyStore.StatePath)) File.Copy(legacyStore.StatePath, projectStore.StatePath, overwrite: false);
        if (Directory.Exists(legacyStore.TasksDirectory))
            CopyDirectory(legacyStore.TasksDirectory, projectStore.TasksDirectory);
        return true;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
