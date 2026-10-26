namespace Seamdesk.Host;

/// <summary>
/// seamdesk.log, kept to a size: a line that finds the file at <c>most</c> bytes or more first moves it to
/// <see cref="Old"/>, replacing the one before. So at most twice <c>most</c> is on disk, and the latest lines always are.
/// </summary>
public static class LogFile
{
    private static readonly Lock Gate = new();

    /// <summary>Where the lines before the last roll are: seamdesk.log's are in seamdesk.old.log.</summary>
    public static string Old(string path) => Path.ChangeExtension(path, ".old.log");

    public static void Append(string path, string line, long most)
    {
        lock (Gate)
        {
            if (new FileInfo(path) is { Exists: true, Length: var length } && length >= most)
            {
                File.Move(path, Old(path), overwrite: true);
            }

            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}
