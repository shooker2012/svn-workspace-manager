using Avalonia;
using System.Runtime.InteropServices;
using System.Threading;

namespace SvnWorkspaceManager.Gui;

internal static class Program
{
    private const string InstanceMutexName = @"Local\SvnWorkspaceManager.Gui";

    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = new Mutex(true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox(IntPtr.Zero, "SVN 工作区管理器已经在运行。请切换到已打开的窗口，避免两个实例同时修改任务状态。", "SVN 工作区管理器", 0x00000040);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
