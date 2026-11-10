using System.Security.Cryptography;
using System.Text;

namespace SvnWorkspaceManager.Core.Models;

public sealed record ProjectContext(string Id, string Name, string ProjectRoot, string ApplicationDirectory)
{
    public string ConfigurationPath => Path.Combine(ApplicationDirectory, "config.json");
    public string StatePath => Path.Combine(ApplicationDirectory, "state.json");
}

public static class ProjectIdentity
{
    public static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("项目根目录不能为空。", nameof(path));
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            try { full = new DirectoryInfo(full).FullName; } catch { }
        }
        return Path.TrimEndingDirectorySeparator(full);
    }

    public static string FromRoot(string path)
    {
        var normalized = NormalizeRoot(path);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToUpperInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }
}
