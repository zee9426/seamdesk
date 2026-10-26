namespace Seamdesk.Host.Tests;

public class LogFileTests
{
    [Fact]
    public void The_log_rolls_at_its_size_keeping_the_newest_lines_and_the_ones_before_and_dropping_older()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("seamdesk-log-").FullName, "seamdesk.log");
        string Line(int i) => $"line {i:D4} " + new string('x', 90); // 100 bytes with the newline on Linux

        for (int i = 0; i < 35; i++)
        {
            LogFile.Append(path, Line(i), most: 1000);
        }

        // 0-9 rolled away, 10-19, 20-29 in seamdesk.old.log, 30-34 in seamdesk.log.
        Assert.Equal([.. Enumerable.Range(20, 10).Select(Line)], File.ReadAllLines(Path.Combine(Path.GetDirectoryName(path)!, "seamdesk.old.log")));
        Assert.Equal([.. Enumerable.Range(30, 5).Select(Line)], File.ReadAllLines(path));
    }
}
