using System.Text.Json;
using System.Text.Json.Serialization;
using SvnWorkspaceManager.Core.Models;

namespace SvnWorkspaceManager.Core.Configuration;

public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new WorkspaceModeJsonConverter() }
    };

    public WorkspaceStore(string applicationDirectory)
    {
        ApplicationDirectory = Path.GetFullPath(applicationDirectory);
        Directory.CreateDirectory(ApplicationDirectory);
    }

    public string ApplicationDirectory { get; }
    public string ConfigurationPath => Path.Combine(ApplicationDirectory, "config.json");
    public string StatePath => Path.Combine(ApplicationDirectory, "state.json");
    public string TasksDirectory => Path.Combine(ApplicationDirectory, "tasks");

    public WorkspaceConfiguration LoadConfiguration()
    {
        if (!File.Exists(ConfigurationPath))
        {
            return new WorkspaceConfiguration
            {
                ProjectRoot = Directory.GetCurrentDirectory(),
                TaskRoot = Path.Combine(Directory.GetCurrentDirectory(), "..", "AzureTasks")
            };
        }

        var configuration = JsonSerializer.Deserialize<WorkspaceConfiguration>(File.ReadAllText(ConfigurationPath), JsonOptions)
            ?? throw new InvalidDataException("配置文件为空或格式无效。");
        ValidateVersion(configuration.Version);
        return configuration;
    }

    public WorkspaceState LoadState()
    {
        if (!File.Exists(StatePath))
        {
            return new WorkspaceState();
        }

        var state = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(StatePath), JsonOptions)
            ?? throw new InvalidDataException("状态文件为空或格式无效。");
        ValidateVersion(state.Version);
        return state;
    }

    public void SaveConfiguration(WorkspaceConfiguration configuration)
    {
        ValidateVersion(configuration.Version);
        WriteAtomically(ConfigurationPath, configuration);
    }

    public void SaveState(WorkspaceState state)
    {
        state.UpdatedAt = DateTimeOffset.UtcNow;
        ValidateVersion(state.Version);
        WriteAtomically(StatePath, state);
    }

    public IEnumerable<TaskWorkspaceMetadata> ListTasks()
    {
        if (!Directory.Exists(TasksDirectory)) yield break;
        foreach (var path in Directory.EnumerateFiles(TasksDirectory, "task.json", SearchOption.AllDirectories))
        {
            TaskWorkspaceMetadata? metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<TaskWorkspaceMetadata>(File.ReadAllText(path), JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (metadata is not null) yield return metadata;
        }
    }

    public void SaveTask(TaskWorkspaceMetadata metadata)
    {
        ValidateVersion(metadata.Version);
        var taskDirectory = Path.Combine(TasksDirectory, metadata.Name);
        Directory.CreateDirectory(taskDirectory);
        WriteAtomically(Path.Combine(taskDirectory, "task.json"), metadata);
    }

    public void RemoveTaskRecord(TaskWorkspaceMetadata metadata)
    {
        var taskDirectory = Path.Combine(TasksDirectory, metadata.Name);
        var fullTasksRoot = Path.GetFullPath(TasksDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullTaskDirectory = Path.GetFullPath(taskDirectory);
        if (!fullTaskDirectory.StartsWith(fullTasksRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝操作任务根目录之外的路径。");
        var taskFile = Path.Combine(fullTaskDirectory, "task.json");
        if (File.Exists(taskFile)) File.Delete(taskFile);
    }

    private static void ValidateVersion(int version)
    {
        if (version != 1) throw new InvalidDataException($"不支持的配置版本：{version}。");
    }

    private static void WriteAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporaryPath, path, true);
    }
}
