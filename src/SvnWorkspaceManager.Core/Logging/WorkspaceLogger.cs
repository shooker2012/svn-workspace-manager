namespace SvnWorkspaceManager.Core.Logging;

public sealed class WorkspaceLogger
{
    private readonly string _logPath;
    private readonly string _fallbackLogPath;
    private readonly object _gate = new();

    public WorkspaceLogger(string directory)
    {
        Directory.CreateDirectory(directory);
        _logPath = Path.Combine(directory, "workspace-manager.log");
        var fallbackDirectory = Path.Combine(Path.GetTempPath(), "SvnWorkspaceManager", "logs");
        Directory.CreateDirectory(fallbackDirectory);
        _fallbackLogPath = Path.Combine(fallbackDirectory, "workspace-manager.log");
    }

    public string LogPath => _logPath;

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    public void Session(string message) => Write("SESSION", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                File.AppendAllText(_logPath, line);
                return;
            }
            catch
            {
                // Logging must never be able to leave the GUI stuck in a busy state.
                // Fall through to the temp directory and, if that also fails, give up quietly.
            }

            try
            {
                File.AppendAllText(_fallbackLogPath, line);
            }
            catch
            {
                // Intentionally ignored: diagnostics are best-effort only.
            }
        }
    }
}
