using Seamdesk.Host;
using Microsoft.Extensions.Time.Testing;

namespace Seamdesk.Host.Tests;

public class StartupTests
{
    [Fact]
    public void The_watchdog_names_the_step_that_overran_but_never_while_the_user_answers_a_dialog_or_once_the_tray_is_up()
    {
        FakeTimeProvider time = new();
        Startup startup = new(time, TimeSpan.FromSeconds(20));

        startup.Begin("settings");
        time.Advance(TimeSpan.FromSeconds(19));
        Assert.Null(startup.Stuck());

        startup.Begin("identity: asking the user", waitsForUser: true);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(startup.Stuck());

        startup.Begin("input hooks");
        time.Advance(TimeSpan.FromSeconds(19));
        Assert.Null(startup.Stuck()); // the clock started again after the dialog
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("input hooks", startup.Stuck());

        startup.Up();
        Assert.Null(startup.Stuck());
    }
}
