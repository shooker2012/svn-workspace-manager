using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SvnWorkspaceManager.Core.Configuration;

namespace SvnWorkspaceManager.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ThemePalette.Initialize(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var projectIndex = Array.FindIndex(desktop.Args ?? [], argument => argument.Equals("--project", StringComparison.OrdinalIgnoreCase));
            var projectRoot = projectIndex >= 0 && projectIndex + 1 < (desktop.Args?.Length ?? 0) ? desktop.Args![projectIndex + 1] : null;
            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                desktop.MainWindow = new MainWindow(projectRoot);
            }
            else
            {
                var applicationDirectory = ProjectLocator.DefaultApplicationDirectory;
                var registry = new ProjectRegistry(applicationDirectory);
                var projects = registry.List().ToList();
                var legacyConfig = Path.Combine(applicationDirectory, "config.json");
                if (projects.Count == 0 && File.Exists(legacyConfig))
                {
                    var legacy = new WorkspaceStore(applicationDirectory).LoadConfiguration();
                    var legacyContext = ProjectLocator.FromConfiguration(legacy, applicationDirectory);
                    ProjectLocator.TryMigrateLegacy(applicationDirectory, legacyContext);
                    registry.Register(legacyContext);
                    projects = registry.List().ToList();
                }
                var lastProject = projects
                    .OrderByDescending(project => project.LastOpened)
                    .FirstOrDefault(project => Directory.Exists(project.ProjectRoot));
                if (lastProject is not null)
                {
                    try
                    {
                        desktop.MainWindow = new MainWindow(lastProject.ProjectRoot);
                    }
                    catch (InvalidOperationException)
                    {
                        desktop.MainWindow = new ProjectPickerWindow(desktop, projects);
                    }
                }
                else
                {
                    desktop.MainWindow = new ProjectPickerWindow(desktop, projects);
                }
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
