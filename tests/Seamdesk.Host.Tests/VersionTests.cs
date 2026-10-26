using Seamdesk.Host;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Seamdesk.Host.Tests;

public class VersionTests
{
    // Seamdesk logs and shows its assembly version; it must be the release's, the top CHANGELOG.md entry (tools/release.ps1).
    [Fact]
    public void Assembly_version_is_the_top_changelog_entry()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "CHANGELOG.md"))) dir = Path.GetDirectoryName(dir)!;
        string top = Regex.Match(File.ReadAllText(Path.Combine(dir, "CHANGELOG.md")), @"^## \[([\d.]+)\]", RegexOptions.Multiline).Groups[1].Value;

        string? shown = typeof(Cursor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];

        Assert.Equal(top, shown);
    }
}
