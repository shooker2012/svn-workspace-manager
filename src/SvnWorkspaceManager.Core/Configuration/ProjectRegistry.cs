using System.Text.Json;
using SvnWorkspaceManager.Core.Models;

namespace SvnWorkspaceManager.Core.Configuration;

public sealed record RegisteredProject(string Id, string Name, string ProjectRoot, DateTimeOffset LastOpened);

public sealed class ProjectRegistry
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ProjectRegistry(string applicationDirectory) => _path = Path.Combine(Path.GetFullPath(applicationDirectory), "projects.json");

    public IReadOnlyList<RegisteredProject> List()
    {
        if (!File.Exists(_path)) return [];
        try { return JsonSerializer.Deserialize<List<RegisteredProject>>(File.ReadAllText(_path), Options) ?? []; }
        catch (JsonException) { return []; }
    }

    public void Register(ProjectContext context)
    {
        using var gate = AcquireGate();
        var items = List().Where(item => !item.Id.Equals(context.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        items.Add(new RegisteredProject(context.Id, context.Name, context.ProjectRoot, DateTimeOffset.UtcNow));
        Write(items.OrderByDescending(item => item.LastOpened).ToList());
    }

    public void EnsureNoConflict(WorkspaceConfiguration configuration)
    {
        var others = List()
            .Where(item => !item.Id.Equals(ProjectIdentity.FromRoot(configuration.ProjectRoot), StringComparison.OrdinalIgnoreCase))
            .Select(item => Path.Combine(Path.GetDirectoryName(_path)!, "projects", item.Id, "config.json"))
            .Where(File.Exists)
            .Select(path => new WorkspaceStore(Path.GetDirectoryName(path)!).LoadConfiguration())
            .ToList();
        ProjectConflictChecker.EnsureNoOverlap(configuration, others);
    }

    public void Remove(string id)
    {
        using var gate = AcquireGate();
        Write(List().Where(item => !item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    private IDisposable AcquireGate()
    {
        var mutex = new Mutex(false, $"Local\\SvnWorkspaceManager.ProjectIndex.{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_path)))[..16]}");
        if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) { mutex.Dispose(); throw new TimeoutException("项目索引正在被其他进程修改，请稍后重试。"); }
        return new MutexReleaser(mutex);
    }

    private sealed class MutexReleaser(Mutex mutex) : IDisposable
    {
        public void Dispose() { try { mutex.ReleaseMutex(); } catch { } mutex.Dispose(); }
    }

    private void Write(IReadOnlyList<RegisteredProject> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(items, Options));
        File.Move(temp, _path, true);
    }
}
