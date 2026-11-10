namespace SvnWorkspaceManager.Core.Security;

public sealed class PathGuard
{
    public string ProjectRoot { get; }
    public string TaskRoot { get; }

    public PathGuard(string projectRoot, string taskRoot)
    {
        ProjectRoot = Normalize(projectRoot);
        TaskRoot = Normalize(taskRoot);
    }

    public string ProjectPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidOperationException("任务映射目录必须是相对路径。");
        var path = Normalize(Path.Combine(ProjectRoot, relativePath));
        EnsureUnder(ProjectRoot, path);
        return path;
    }

    public string TaskPath(string taskName, string relativePath)
    {
        ValidateTaskName(taskName);
        if (Path.IsPathRooted(relativePath)) throw new InvalidOperationException("任务映射目录必须是相对路径。");
        var path = Normalize(Path.Combine(TaskRoot, taskName, relativePath));
        EnsureUnder(TaskRoot, path);
        return path;
    }

    public static void ValidateTaskName(string taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName)
            || taskName.Any(c => Path.GetInvalidFileNameChars().Contains(c))
            || taskName is "." or ".."
            || taskName.EndsWith(' ') || taskName.EndsWith('.'))
            throw new InvalidOperationException("任务名称为空、包含非法字符，或以空格、句点结尾。");

        var deviceName = taskName.Split('.')[0];
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (deviceName.Length == 4 && (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(deviceName[3])))
            throw new InvalidOperationException("任务名称不能使用 Windows 保留设备名。");
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static void EnsureUnder(string root, string path)
    {
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"路径超出允许范围：{path}");
    }
}
