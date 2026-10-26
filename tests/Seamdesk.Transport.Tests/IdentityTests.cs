using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;

namespace Seamdesk.Transport.Tests;

public class IdentityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Identity_survives_a_restart_in_a_user_only_file()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("seamdesk-id-").FullName, "identity.p12");

        Identity first = await Identity.LoadOrCreateAsync(new FileIdentityStore(path), "alpha", Timeout, TimeProvider.System);
        Identity again = await Identity.LoadOrCreateAsync(new FileIdentityStore(path), "alpha", Timeout, TimeProvider.System);

        Assert.Equal(first.Spki, again.Spki);
        Assert.True(again.Certificate.HasPrivateKey);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    /// <summary>
    /// A stored identity that DPAPI refuses (another logon's key, a damaged file) or that never comes back must surface as
    /// unreadable, in time, and must not be replaced: Seamdesk asks the user before it starts fresh.
    /// </summary>
    [Theory]
    [InlineData("refuses")]
    [InlineData("never answers")]
    public async Task A_stored_identity_that_cant_be_read_is_reported_within_the_timeout_and_never_overwritten(string store)
    {
        FakeTimeProvider time = new();
        using ManualResetEventSlim never = new();
        Stub stub = new(store == "refuses"
            ? () => throw new CryptographicException("Key not valid for use in specified state.")
            : () =>
            {
                never.Wait(TimeSpan.FromSeconds(5)); // the cap only keeps a regression from hanging the run
                return null;
            });

        Task<Identity> load = Identity.LoadOrCreateAsync(stub, "alpha", Timeout, time);
        time.Advance(Timeout);

        await Assert.ThrowsAsync<UnreadableIdentityException>(() => load);
        Assert.False(stub.Saved);
        never.Set();
    }

    private sealed class Stub(Func<byte[]?> load) : IIdentityStore
    {
        public bool Saved { get; private set; }

        public byte[]? Load() => load();

        public void Save(byte[] pkcs12) => Saved = true;
    }
}
