using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;

namespace Seamdesk.Transport.Tests;

public class PairingTests
{
    [Fact]
    public async Task Right_code_pins_both_sides_and_they_connect()
    {
        await using Node a = Kit.Start(Kit.Local("alpha.localhost"));
        await using Node b = Kit.Start(Kit.Local("bravo.localhost"));
        b.PeerPort = a.Port;

        Pin pinOfA = await b.PairAsync("127.0.0.1", a.ShowPairingCode().Code).WaitAsync(Kit.Patience);

        Assert.Equal(Kit.PinOf(a.Local), pinOfA);
        Assert.Equal([Kit.PinOf(a.Local)], b.Local.Pins.All);
        Assert.Equal([Kit.PinOf(b.Local)], a.Local.Pins.All);

        // The new pin starts B's supervisor, which dials A and passes the pinned TLS + Hello handshake.
        await Kit.Eventually(() => b[pinOfA].Current is not null && a[Kit.PinOf(b.Local)].Current is not null);
        Assert.Equal("bravo.localhost", a[Kit.PinOf(b.Local)].Current!.PeerHello.MachineId);
    }

    [Fact]
    public async Task Pairing_again_under_the_same_name_replaces_the_old_pin()
    {
        // B lost its identity (a reinstall, or "Start fresh" on an unreadable one) and pairs with A again under its name.
        // A must stop trusting the old key: kept, it stays pinned unseen, and removing B by name unpairs only one of the two.
        LocalNode oldB = Kit.Local("bravo.localhost");
        await using Node a = Kit.Start(Kit.Local("alpha.localhost"));
        a.Local.Pins.Add(Kit.PinOf(oldB));
        oldB.Pins.Add(Kit.PinOf(a.Local));
        await using Node newB = Kit.Start(Kit.Local("bravo.localhost"));
        newB.PeerPort = a.Port;

        await newB.PairAsync("127.0.0.1", a.ShowPairingCode().Code).WaitAsync(Kit.Patience);

        Assert.Equal([Kit.PinOf(newB.Local)], a.Local.Pins.All);
        Assert.Throws<KeyNotFoundException>(() => a[Kit.PinOf(oldB)]); // no longer dialled
        await using Node nodeOldB = Kit.Start(oldB);
        nodeOldB.PeerPort = a.Port;
        RefusedException e = await Assert.ThrowsAsync<RefusedException>(() => nodeOldB.DialAsync(Kit.PinOf(a.Local), CancellationToken.None));
        Assert.Equal("not paired with this machine (unknown certificate): pair again", e.Message);
    }

    [Fact]
    public void Saved_pins_keep_only_the_newest_for_a_name()
    {
        // Settings saved before the fix may hold both pins of a machine that paired again; the later one is the live one.
        Pin old = new("AA", "bravo.localhost", "127.0.0.1"), other = new("BB", "charlie.localhost", "127.0.0.1"), again = new("CC", "bravo.localhost", "127.0.0.1");

        Assert.Equal([other, again], new Pins([old, other, again]).All);
    }

    [Fact]
    public async Task Three_wrong_codes_kill_the_code()
    {
        await using Node a = Kit.Start(Kit.Local("alpha.localhost"));
        await using Node b = Kit.Start(Kit.Local("bravo.localhost"));
        b.PeerPort = a.Port;
        string code = a.ShowPairingCode().Code;
        string wrong = ((int.Parse(code) + 1) % 100_000_000).ToString("D8");

        for (int i = 0; i < 3; i++)
        {
            RefusedException e = await Assert.ThrowsAsync<RefusedException>(() => b.PairAsync("127.0.0.1", wrong));
            Assert.Equal("wrong pairing code", e.Message);
        }

        RefusedException dead = await Assert.ThrowsAsync<RefusedException>(() => b.PairAsync("127.0.0.1", code));
        Assert.Equal("no pairing code is active on alpha.localhost (expired, used or too many tries)", dead.Message);
        Assert.Empty(a.Local.Pins.All);
        Assert.Empty(b.Local.Pins.All);
    }

    [Fact]
    public async Task Normal_dials_from_a_peer_A_forgot_do_not_use_up_the_code()
    {
        // B still has A pinned and keeps dialling; A dropped B and shows a code to pair again.
        LocalNode b = Kit.Local("bravo.localhost");
        await using Node a = Kit.Start(Kit.Local("alpha.localhost"));
        b.Pins.Add(Kit.PinOf(a.Local));
        await using Node nodeB = Kit.Start(b);
        nodeB.PeerPort = a.Port;
        string code = a.ShowPairingCode().Code;

        for (int i = 0; i < 3; i++)
        {
            RefusedException e = await Assert.ThrowsAsync<RefusedException>(() => nodeB.DialAsync(Kit.PinOf(a.Local), CancellationToken.None));
            Assert.Equal("not paired with this machine (unknown certificate): pair again", e.Message);
        }

        Assert.Equal(Kit.PinOf(a.Local), await nodeB.PairAsync("127.0.0.1", code).WaitAsync(Kit.Patience));
        Assert.Equal([Kit.PinOf(b)], a.Local.Pins.All);
    }

    [Fact]
    public async Task Code_expires_after_two_minutes()
    {
        FakeTimeProvider time = new();
        await using Node a = Kit.Start(Kit.Local("alpha.localhost", time));
        await using Node b = Kit.Start(Kit.Local("bravo.localhost"));
        b.PeerPort = a.Port;
        string code = a.ShowPairingCode().Code;
        string wrong = ((int.Parse(code) + 1) % 100_000_000).ToString("D8");

        // One second before expiry the code still runs J-PAKE (a wrong guess is judged on its merits).
        time.Advance(TimeSpan.FromSeconds(119));
        RefusedException live = await Assert.ThrowsAsync<RefusedException>(() => b.PairAsync("127.0.0.1", wrong));
        Assert.Equal("wrong pairing code", live.Message);

        time.Advance(TimeSpan.FromSeconds(1));
        RefusedException expired = await Assert.ThrowsAsync<RefusedException>(() => b.PairAsync("127.0.0.1", code));
        Assert.Equal("no pairing code is active on alpha.localhost (expired, used or too many tries)", expired.Message);
        Assert.Empty(a.Local.Pins.All);
    }

    /// <summary>
    /// Mallory terminates TLS on both sides with her own certificates and relays every frame unchanged, including the
    /// J-PAKE messages, while B types the right code. The fingerprint binding in the key confirmation must catch it.
    /// </summary>
    [Fact]
    public async Task Relaying_man_in_the_middle_with_the_right_code_fails_on_both_sides()
    {
        await using Node a = Kit.Start(Kit.Local("alpha.localhost"));
        await using Node b = Kit.Start(Kit.Local("bravo.localhost"));
        LocalNode mallory = Kit.Local("mallory");
        TcpListener relay = new(IPAddress.Loopback, 0);
        relay.Start();
        b.PeerPort = ((IPEndPoint)relay.LocalEndpoint).Port;
        Task relaying = RelayAsync(relay, mallory, a.Port);

        await Assert.ThrowsAsync<RefusedException>(() => b.PairAsync("127.0.0.1", a.ShowPairingCode().Code).WaitAsync(Kit.Patience));

        await relaying.WaitAsync(Kit.Patience);
        Assert.Empty(a.Local.Pins.All);
        Assert.Empty(b.Local.Pins.All);
    }

    private static async Task RelayAsync(TcpListener relay, LocalNode mallory, int portOfA)
    {
        using Socket fromB = await relay.AcceptSocketAsync();
        relay.Stop();
        await using SslStream toB = await Tls.AcceptAsync(new NetworkStream(fromB), mallory.Identity, CancellationToken.None);
        await using SslStream toA = await Kit.RawDialAsync(Kit.Local("mallory"), portOfA);
        await Task.WhenAny(toB.CopyToAsync(toA), toA.CopyToAsync(toB));
    }
}
