using System.Diagnostics;

namespace SvnWorkspaceManager.Core.FileSystem;

/// <summary>
/// Reports throttled progress for long-running file copy or delete operations.
/// The callback may run on a worker thread; callers are responsible for marshaling.
/// </summary>
internal sealed class FileOperationProgress
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(500);

    private readonly Action<string>? _report;
    private readonly string _label;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private int _files;
    private long _bytes;
    private long _lastReportTicks;

    public FileOperationProgress(Action<string>? report, string label)
    {
        _report = report;
        _label = label;
    }

    public void Copied(long bytes) => Count(bytes, "已复制", "个文件");

    public void Deleted(long bytes = 0) => Count(bytes, "已删除", "项");

    public void Complete(string verb)
    {
        int files;
        long bytes;
        lock (_gate)
        {
            files = _files;
            bytes = _bytes;
        }

        _report?.Invoke($"{_label}：{verb}完成，共处理 {files:N0} 项（{FormatBytes(bytes)}）。");
    }

    private void Count(long bytes, string verb, string unit)
    {
        int files;
        long total;
        bool shouldReport;
        lock (_gate)
        {
            _files++;
            _bytes += bytes;
            files = _files;
            total = _bytes;
            var now = _clock.ElapsedTicks;
            shouldReport = now - _lastReportTicks >= ReportInterval.Ticks;
            if (shouldReport) _lastReportTicks = now;
        }

        if (shouldReport)
            _report?.Invoke($"{_label}：{verb} {files:N0} {unit}（{FormatBytes(total)}）…");
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024L => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B"
    };
}
