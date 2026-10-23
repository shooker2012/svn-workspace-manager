namespace SvnWorkspaceManager.Core.Svn;

public sealed record SvnStatusEntry(char Code, string FullPath, bool HasBlockingFlag);

public static class SvnStatusParser
{
    public static IReadOnlyList<SvnStatusEntry> Parse(string output, string workingCopyRoot)
    {
        var entries = new List<SvnStatusEntry>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 8) continue;
            var pathText = line[8..].Trim();
            if (string.IsNullOrWhiteSpace(pathText)) continue;
            var fullPath = Path.GetFullPath(Path.IsPathRooted(pathText)
                ? pathText
                : Path.Combine(workingCopyRoot, pathText));
            var blocking = line.Take(Math.Min(7, line.Length)).Any(character => character is 'C' or '!' or '~' or 'L');
            entries.Add(new SvnStatusEntry(line[0], fullPath, blocking));
        }

        return entries;
    }

    public static bool ContainsBlockingStatus(string output, string workingCopyRoot) =>
        Parse(output, workingCopyRoot).Any(entry => entry.HasBlockingFlag);
}
