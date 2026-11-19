using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Controls.Templates;
using Avalonia.Platform.Storage;
using SvnWorkspaceManager.Core.Configuration;
using SvnWorkspaceManager.Core.Models;
using SvnWorkspaceManager.Core;

namespace SvnWorkspaceManager.Gui;

public sealed class ProjectPickerWindow : Window
{
    private readonly ListBox _projects = new();
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;

    public ProjectPickerWindow(IClassicDesktopStyleApplicationLifetime desktop, IReadOnlyList<RegisteredProject> projects)
    {
        _desktop = desktop;
        Title = "选择项目 - SVN 工作区管理器";
        Width = 720;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _projects.ItemsSource = projects;
        _projects.SelectedIndex = projects.Count > 0 ? 0 : -1;
        _projects.ItemTemplate = new FuncDataTemplate<RegisteredProject>((project, _) => new StackPanel
        {
            Spacing = 3,
            Children = { new TextBlock { Text = project.Name, FontSize = 16 }, new TextBlock { Text = project.ProjectRoot, Foreground = ThemePalette.Muted } }
        });
        var open = new Button { Content = "打开项目", HorizontalAlignment = HorizontalAlignment.Right };
        open.Click += (_, _) => OpenSelected();
        var import = new Button { Content = "导入 SVN 工作副本" };
        import.Click += async (_, _) => await ImportProject();
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children = { new TextBlock { Text = "选择已导入的 SVN 项目", FontSize = 22 }, _projects, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { import, open } } }
        };
    }

    private async Task ImportProject()
    {
        var folders = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = "选择已有 SVN 工作副本根目录" });
        var path = folders.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!Directory.Exists(Path.Combine(path, ".svn"))) throw new InvalidOperationException("所选目录不是 SVN 工作副本根目录。");
            var context = ProjectLocator.CreateContext(path, ProjectLocator.DefaultApplicationDirectory);
            var configPath = context.ConfigurationPath;
            if (!File.Exists(configPath))
            {
                var configuration = await ReadImportConfiguration(context);
                if (configuration is null) return;
                new WorkspaceStore(context.ApplicationDirectory).SaveConfiguration(configuration);
            }
            new ProjectRegistry(ProjectLocator.DefaultApplicationDirectory).Register(context);
            await MessageBox("项目已导入，请重新打开项目选择器。", "导入完成");
        }
        catch (Exception exception) { await MessageBox(exception.Message, "导入失败"); }
    }

    private async Task<WorkspaceConfiguration?> ReadImportConfiguration(ProjectContext context)
    {
        var svnUrl = new TextBox { Watermark = "SVN trunk URL" };
        var taskRoot = new TextBox { Text = Path.Combine(Path.GetDirectoryName(context.ProjectRoot) ?? context.ProjectRoot, Path.GetFileName(context.ProjectRoot) + "Tasks") };
        var isolated = new TextBox { Text = "Output/Lua\nSource/Azure" };
        var save = new Button { Content = "导入项目" };
        var cancel = new Button { Content = "取消" };
        var dialog = new Window { Title = "导入 SVN 项目", Width = 620, Height = 430, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var result = new TaskCompletionSource<WorkspaceConfiguration?>();
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(svnUrl.Text) || string.IsNullOrWhiteSpace(taskRoot.Text) || string.IsNullOrWhiteSpace(isolated.Text)) return;
            result.TrySetResult(new WorkspaceConfiguration { ProjectRoot = context.ProjectRoot, TaskRoot = taskRoot.Text.Trim(), SvnTrunkUrl = svnUrl.Text.Trim(), IsolatedDirectories = isolated.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() });
            dialog.Close();
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 10, Children = { new TextBlock { Text = "导入已有 SVN 工作副本", FontSize = 22 }, new TextBlock { Text = "项目根目录：" + context.ProjectRoot }, new TextBlock { Text = "SVN trunk URL" }, svnUrl, new TextBlock { Text = "任务根目录" }, taskRoot, new TextBlock { Text = "任务映射目录（每行一个相对路径）" }, isolated, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } } } };
        await dialog.ShowDialog(this);
        return await result.Task;
    }

    private void OpenSelected()
    {
        if (_projects.SelectedItem is not RegisteredProject project) return;
        if (!Directory.Exists(project.ProjectRoot))
        {
            _ = MessageBox("项目路径不存在，请先恢复目录或使用 --project 指定有效的已导入项目。", "项目不可用");
            return;
        }
        try
        {
            var window = new MainWindow(project.ProjectRoot);
            _desktop.MainWindow = window;
            window.Show();
            Close();
        }
        catch (Exception exception) { _ = MessageBox(exception.Message, "无法打开项目"); }
    }

    private async Task MessageBox(string text, string title)
    {
        var dialog = new Window { Title = title, Width = 520, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new Button { Content = "确定", HorizontalAlignment = HorizontalAlignment.Right } } } };
        if (dialog.Content is StackPanel panel && panel.Children[1] is Button button) button.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }
}
