using SvnWorkspaceManager.Core.FileSystem;
using SvnWorkspaceManager.Core.Models;
using SvnWorkspaceManager.Core.Security;
using SvnWorkspaceManager.Core.Svn;

namespace SvnWorkspaceManager.Core;

public sealed partial class WorkspaceService
{
    public async Task<TaskWorkspaceMetadata> CreateTaskFromCurrentChangesAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateIsolationLayout();
        PathGuard.ValidateTaskName(name);
        if (_store.ListTasks().Any(task => task.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"任务已存在：{name}");

        var patches = new List<(string RelativePath, string FilePath)>();
        var unversioned = new List<(string RelativePath, string SourcePath, string RelativeItemPath)>();
        try
        {
            foreach (var relativePath in _configuration.IsolatedDirectories)
            {
                var source = _guard.ProjectPath(relativePath);
                if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"任务映射目录不存在：{source}");
                if (_junctions.IsJunction(source)) throw new InvalidOperationException($"任务映射目录 {relativePath} 已经处于任务映射状态。");

                var status = await _svn.StatusIncludingUnversionedAsync(source, cancellationToken);
                if (!status.Succeeded) throw new InvalidOperationException($"无法读取 {relativePath} 的 SVN 状态：{status.StandardError}");
                var entries = SvnStatusParser.Parse(status.StandardOutput, Path.GetDirectoryName(source)!);
                if (entries.Any(entry => entry.HasBlockingFlag))
                    throw new InvalidOperationException($"任务映射目录 {relativePath} 存在冲突、缺失、目录受阻或锁定，不能迁移。");

                if (entries.Any(entry => entry.Code is not ('?' or 'I')))
                {
                    Report($"正在导出 {relativePath} 的修改…");
                    var diff = await _svn.DiffAsync(source, cancellationToken);
                    if (!diff.Succeeded) throw new InvalidOperationException($"无法导出 {relativePath} 的修改：{diff.StandardError}");
                    if (!string.IsNullOrWhiteSpace(diff.StandardOutput))
                    {
                        var patchPath = Path.Combine(_store.ApplicationDirectory, $"migration-{Guid.NewGuid():N}.patch");
                        await File.WriteAllTextAsync(patchPath, diff.StandardOutput, cancellationToken);
                        patches.Add((relativePath, patchPath));
                    }
                }

                foreach (var entry in entries.Where(entry => entry.Code is '?' or 'I').OrderBy(entry => entry.FullPath.Length))
                {
                    if (!IsUnder(source, entry.FullPath)) continue;
                    if (unversioned.Any(item => IsUnder(item.SourcePath, entry.FullPath))) continue;
                    var relativeItem = Path.GetRelativePath(source, entry.FullPath);
                    unversioned.Add((relativePath, entry.FullPath, relativeItem));
                }
            }

            Report("正在创建任务工作区…");
            var metadata = await CreateTaskAsync(name, cancellationToken);
            foreach (var patch in patches)
            {
                Report($"正在把修改应用到任务目录 {patch.RelativePath}…");
                var result = await _svn.PatchAsync(patch.FilePath, _guard.TaskPath(name, patch.RelativePath), cancellationToken);
                if (!result.Succeeded) throw new InvalidOperationException($"无法将 {patch.RelativePath} 的修改应用到新任务：{result.StandardError}");
            }
            if (unversioned.Count > 0) Report($"正在迁移 {unversioned.Count} 项未版本化或忽略内容…");
            foreach (var item in unversioned)
            {
                var target = Path.Combine(_guard.TaskPath(name, item.RelativePath), item.RelativeItemPath);
                Report($"正在迁移 {item.RelativeItemPath}…");
                var progress = new FileOperationProgress(Report, item.RelativeItemPath);
                await Task.Run(() =>
                {
                    CopyItem(item.SourcePath, target, progress.Copied, cancellationToken);
                    EnsureMirror(item.SourcePath, target, cancellationToken);
                    DeleteItem(item.SourcePath, progress.Deleted, cancellationToken);
                }, cancellationToken);
                progress.Complete("迁移");
            }
            foreach (var relativePath in _configuration.IsolatedDirectories)
            {
                var projectPath = _guard.ProjectPath(relativePath);
                Report($"正在恢复父级目录 {relativePath}…");
                await EnsureSuccess(_svn.RevertAsync(projectPath, cancellationToken), $"恢复父级目录 {relativePath}");
                await RemoveUnversionedAfterRevertAsync(projectPath, cancellationToken);
            }

            await EnableTaskMappingAsync(name, cancellationToken: cancellationToken);
            return metadata;
        }
        finally
        {
            foreach (var patch in patches)
            {
                try { File.Delete(patch.FilePath); } catch { }
            }
        }
    }

    private static bool IsUnder(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyItem(string source, string target, Action<long>? onFileCopied = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly).Where(path => !Path.GetFileName(path).Equals(".svn", StringComparison.OrdinalIgnoreCase)))
                CopyItem(directory, Path.Combine(target, Path.GetFileName(directory)), onFileCopied, cancellationToken);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
                CopyItem(file, Path.Combine(target, Path.GetFileName(file)), onFileCopied, cancellationToken);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            var attributes = File.GetAttributes(target);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
        }
        File.Copy(source, target, true);
        onFileCopied?.Invoke(new FileInfo(target).Length);
    }

    private static void EnsureMirror(string source, string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(source))
        {
            if (!Directory.Exists(target)) throw new IOException($"迁移目录不完整：{target}");
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly).Where(path => !Path.GetFileName(path).Equals(".svn", StringComparison.OrdinalIgnoreCase)))
                EnsureMirror(directory, Path.Combine(target, Path.GetFileName(directory)), cancellationToken);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
                EnsureMirror(file, Path.Combine(target, Path.GetFileName(file)), cancellationToken);
            return;
        }
        if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length)
            throw new IOException($"迁移文件校验失败：{target}");
    }

    private static void DeleteItem(string path, Action<long>? onFileDeleted = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                info.Delete(recursive: false);
                return;
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                if (File.Exists(entry)) onFileDeleted?.Invoke(new FileInfo(entry).Length);
            }
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            var length = new FileInfo(path).Length;
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
            onFileDeleted?.Invoke(length);
        }
    }

    private async Task RemoveUnversionedAfterRevertAsync(string projectPath, CancellationToken cancellationToken)
    {
        var status = await _svn.StatusIncludingUnversionedAsync(projectPath, cancellationToken);
        if (!status.Succeeded) throw new InvalidOperationException($"无法读取父级目录状态：{status.StandardError}");
        foreach (var entry in SvnStatusParser.Parse(status.StandardOutput, Path.GetDirectoryName(projectPath)!).Where(entry => entry.Code is '?' or 'I'))
        {
            if (IsUnder(projectPath, entry.FullPath)) DeleteItem(entry.FullPath, cancellationToken: cancellationToken);
        }
    }
}
