using Seamdesk.Host;

namespace Seamdesk.App;

internal static class Log
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Seamdesk");

    public static readonly string FilePath = Path.Combine(Dir, "seamdesk.log");

    /// <summary>About four hours of driving an MWB peer (its timing lines add 0.9 MB an hour); the 4 MB before are in seamdesk.old.log.</summary>
    private const long Most = 4 * 1024 * 1024;

    public static void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            LogFile.Append(FilePath, $"{DateTime.Now:O} {line}", Most);
        }
        catch (IOException)
        {
        }
    }
}
