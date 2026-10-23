using Microsoft.VisualBasic.FileIO;

namespace SvnWorkspaceManager.Core.FileSystem;

public interface IRecycleBin
{
    void DeleteDirectory(string path);
}

public sealed class WindowsRecycleBin : IRecycleBin
{
    public void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
            path,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin,
            UICancelOption.ThrowException);
    }
}
