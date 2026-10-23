using System.Diagnostics;
using SvnWorkspaceManager.Core.Logging;
using SvnWorkspaceManager.Core.Models;

namespace SvnWorkspaceManager.Core.Process;

public sealed class ProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);
    private readonly WorkspaceLogger _logger;

    public ProcessRunner(WorkspaceLogger logger) => _logger = logger;

    public async Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var argumentList = arguments.ToArray();
        var start = Stopwatch.StartNew();
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in argumentList) info.ArgumentList.Add(argument);

        var commandText = $"{executable} {string.Join(' ', argumentList)}";
        _logger.Info($"执行命令：{commandText}");
        using var process = new System.Diagnostics.Process { StartInfo = info };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var effectiveTimeout = timeout ?? DefaultTimeout;
        timeoutSource.CancelAfter(effectiveTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            _logger.Error($"命令超时，已终止（{effectiveTimeout.TotalSeconds:0} 秒）：{commandText}");
            throw new TimeoutException($"命令超过 {effectiveTimeout.TotalSeconds:0} 秒仍未完成，已终止：{commandText}。请检查 VPN、网络或目录占用后重试。");
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            _logger.Error($"命令已取消：{commandText}");
            throw;
        }

        var result = new CommandResult(process.ExitCode, await outputTask, await errorTask, start.Elapsed);
        _logger.Info($"命令完成：exit={result.ExitCode}, duration={result.Duration.TotalMilliseconds:0}ms");
        return result;
    }
}
