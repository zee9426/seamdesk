using System.Runtime.InteropServices;
using Seamdesk.App;
using Seamdesk.Host;
using Seamdesk.Transport;

namespace Seamdesk.App.Tests;

public class InjectorTests
{
    /// <summary>
    /// SendInput rejects a wrongly laid-out INPUT with ERROR_INVALID_PARAMETER (87). On a desktop that takes input it
    /// succeeds; in a non-interactive session (ssh, session 0) it fails with ERROR_ACCESS_DENIED (5) instead. A wheel
    /// turn of 0 changes nothing either way.
    /// </summary>
    [Fact]
    public void SendInput_accepts_the_INPUT_layout()
    {
        Injector injector = new(() => new Screens([new Display(0, 0, 1024, 768, true)]));

        bool sent = injector.Inject(new Wheel(0, Horizontal: false));
        int error = Marshal.GetLastPInvokeError();

        Assert.True(sent || error == 5, $"SendInput failed with error {error}");
    }
}
