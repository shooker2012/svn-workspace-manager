namespace SvnWorkspaceManager.Core.FileSystem;

using System.Diagnostics;

public sealed class JunctionManager
{
    public bool IsJunction(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    public string? GetTarget(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || !info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
        return info.ResolveLinkTarget(true)?.FullName;
    }

    public bool IsJunctionTo(string linkPath, string expectedTargetPath)
    {
        var info = new DirectoryInfo(linkPath);
        if (!info.Exists || !info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        var target = info.ResolveLinkTarget(true)?.FullName;
        return target is not null && Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar).Equals(
            Path.GetFullPath(expectedTargetPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    public void Create(string linkPath, string targetPath)
    {
        if (Directory.Exists(linkPath) || File.Exists(linkPath)) throw new IOException($"映射路径已存在：{linkPath}");
        if (!Directory.Exists(targetPath)) throw new DirectoryNotFoundException(targetPath);
        using var process = System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("无法启动 mklink。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException($"创建 junction 超过 15 秒仍未完成，已终止：{linkPath}");
        }
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException($"创建 junction 失败：{error.GetAwaiter().GetResult()}{output.GetAwaiter().GetResult()}");
    }

    public void RemoveLinkOnly(string linkPath, string expectedTargetRoot)
    {
        var info = new DirectoryInfo(linkPath);
        if (!info.Exists || !info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"目标不是 junction：{linkPath}");
        var target = info.ResolveLinkTarget(true)?.FullName;
        var normalizedRoot = Path.GetFullPath(expectedTargetRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = target is null ? null : Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var isUnderRoot = normalizedTarget is not null && (normalizedTarget.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (!isUnderRoot)
            throw new IOException("拒绝删除指向任务根目录之外的 junction。");
        info.Delete(false);
    }
}
