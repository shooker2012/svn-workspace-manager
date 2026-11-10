using SvnWorkspaceManager.Core.Models;
using SvnWorkspaceManager.Core.Svn;

namespace SvnWorkspaceManager.Core;

public sealed partial class WorkspaceService
{
    public async Task RepairIsolatedDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        var state = _store.LoadState();
        if (state.Mode != WorkspaceMode.Normal)
            throw new InvalidOperationException("只有普通模式可以修复父级任务映射目录；任务映射中断请使用“继续回滚”。");

        var repaired = new List<string>();
        foreach (var relativePath in _configuration.IsolatedDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectPath = _guard.ProjectPath(relativePath);
            if (_junctions.IsJunction(projectPath))
                throw new InvalidOperationException($"任务映射目录 {relativePath} 仍是 junction，请先退出任务映射。");

            if (!Directory.Exists(projectPath))
            {
                var parentPath = Path.GetDirectoryName(projectPath)!;
                var parentInfo = await _svn.WorkingCopyInfoAsync(parentPath, cancellationToken);
                if (parentInfo.Info is null)
                    throw new InvalidOperationException($"无法读取 {relativePath} 的父目录 revision：{parentInfo.Result.StandardError}");
                Report($"正在恢复缺失的父级目录 {relativePath} 到 r{parentInfo.Info.Revision}…");
                await EnsureSuccess(_svn.RestoreAtRevisionAsync(projectPath, parentInfo.Info.Revision, cancellationToken), $"恢复父级目录 {relativePath}");
                repaired.Add(relativePath);
                continue;
            }

            var infoText = await _svn.InfoTextAsync(projectPath, cancellationToken);
            var scheduledForDeletion = infoText.Succeeded
                && infoText.StandardOutput.Contains("Schedule: delete", StringComparison.OrdinalIgnoreCase);
            var status = await _svn.StatusIncludingUnversionedAsync(projectPath, cancellationToken);
            if (!status.Succeeded)
                throw new InvalidOperationException($"无法读取父级目录 {relativePath} 的 SVN 状态：{status.StandardError}");

            var entries = SvnStatusParser.Parse(status.StandardOutput, projectPath);
            var blocking = entries.Where(entry => entry.HasBlockingFlag).ToList();
            if (!scheduledForDeletion && blocking.Count == 0) continue;

            var trackedChanges = entries.Where(entry => entry.Code is 'M' or 'A' or 'R').ToList();
            if (trackedChanges.Count > 0)
                throw new InvalidOperationException(
                    $"任务映射目录 {relativePath} 包含已跟踪修改，拒绝自动修复：{FormatStatusEntries(trackedChanges, 5)}。请先提交或恢复这些修改。");

            if (!scheduledForDeletion)
                throw new InvalidOperationException(
                    $"任务映射目录 {relativePath} 存在 SVN 冲突，但目录没有处于中断排除状态。请先人工检查：{FormatStatusEntries(blocking, 5)}");

            Report($"正在清理并恢复 {relativePath} 的中断排除状态…");
            var cleanup = await _svn.CleanupAsync(_configuration.ProjectRoot, cancellationToken);
            if (!cleanup.Succeeded)
                throw new InvalidOperationException($"清理 SVN 工作队列失败：{cleanup.StandardError}");

            var revert = await _svn.RevertAsync(projectPath, cancellationToken);
            if (!revert.Succeeded)
                throw new InvalidOperationException($"恢复父级目录 {relativePath} 失败：{revert.StandardError}");

            var verify = await _svn.StatusIncludingUnversionedAsync(projectPath, cancellationToken);
            if (!verify.Succeeded)
                throw new InvalidOperationException($"修复后无法读取 {relativePath} 的状态：{verify.StandardError}");
            var remaining = SvnStatusParser.Parse(verify.StandardOutput, projectPath);
            var invalid = remaining.Where(entry => entry.HasBlockingFlag || entry.Code is 'M' or 'A' or 'R' or 'D' or '!' or '~').ToList();
            if (invalid.Count > 0)
                throw new InvalidOperationException($"修复后 {relativePath} 仍存在异常状态：{FormatStatusEntries(invalid, 5)}");

            repaired.Add(relativePath);
        }

        Report(repaired.Count == 0
            ? "未发现需要修复的父级任务映射目录。"
            : $"已修复父级任务映射目录：{string.Join('、', repaired)}。");
    }
}
