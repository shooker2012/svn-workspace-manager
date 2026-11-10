using System.Diagnostics;

namespace SvnWorkspaceManager.Core.Configuration;

public sealed class ProjectGuiSession : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private ProjectGuiSession(Mutex mutex) => _mutex = mutex;

    public static bool TryAcquire(string projectId, out ProjectGuiSession? session)
    {
        var mutex = new Mutex(false, $"Local\\SvnWorkspaceManager.Gui.{projectId}");
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException)
        {
            // The previous GUI process exited unexpectedly; ownership is transferred.
            acquired = true;
        }
        if (!acquired) { mutex.Dispose(); session = null; return false; }
        session = new ProjectGuiSession(mutex);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mutex.ReleaseMutex(); } catch (AbandonedMutexException) { }
        _mutex.Dispose();
    }
}

public sealed class ProjectOperationLock : IDisposable
{
    private readonly Semaphore _semaphore;
    private bool _disposed;

    private ProjectOperationLock(Semaphore semaphore) => _semaphore = semaphore;

    public static ProjectOperationLock Acquire(string projectId, TimeSpan timeout)
    {
        var semaphore = new Semaphore(1, 1, $"Local\\SvnWorkspaceManager.Operation.{projectId}");
        try
        {
            if (!semaphore.WaitOne(timeout))
                throw new TimeoutException($"项目正在执行其他写操作（pid={GetOwnerHint()}），请稍后重试。");
            return new ProjectOperationLock(semaphore);
        }
        catch { semaphore.Dispose(); throw; }
    }

    private static int GetOwnerHint() => System.Diagnostics.Process.GetCurrentProcess().Id;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _semaphore.Release(); } catch (SemaphoreFullException) { }
        _semaphore.Dispose();
    }
}
