using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Seamdesk.App;
using Seamdesk.Transport;

namespace Seamdesk.App.Tests;

public class DpapiIdentityStoreTests
{
    [Fact]
    public async Task An_identity_kept_with_DPAPI_survives_a_restart_and_Schannel_uses_it_for_mutual_TLS_1_3()
    {
        string dir = Directory.CreateTempSubdirectory("seamdesk-dpapi-").FullName;
        try
        {
            string path = Path.Combine(dir, "identity.dpapi");
            Identity first = await Identity.LoadOrCreateAsync(new DpapiIdentityStore(path), "alpha", TimeSpan.FromSeconds(20), TimeProvider.System);
            Identity again = await Identity.LoadOrCreateAsync(new DpapiIdentityStore(path), "alpha", TimeSpan.FromSeconds(20), TimeProvider.System);
            Identity beta = await Identity.LoadOrCreateAsync(new DpapiIdentityStore(Path.Combine(dir, "beta.dpapi")), "beta", TimeSpan.FromSeconds(20), TimeProvider.System);

            Assert.Equal(first.Spki, again.Spki); // a new key per launch would break every pin
            // The file is not a usable PKCS#12: only DPAPI, as this user, turns it back into the key.
            Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(path), null));

            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            Task<SslStream> accepted = Task.Run(async () =>
                await Tls.AcceptAsync((await listener.AcceptTcpClientAsync()).GetStream(), beta, CancellationToken.None));
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            await using SslStream dialer = await Tls.DialAsync(client.GetStream(), again, CancellationToken.None);
            await using SslStream server = await accepted.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(SslProtocols.Tls13, dialer.SslProtocol);
            Assert.Equal(beta.Spki, Identity.SpkiOf(dialer.RemoteCertificate!));
            Assert.Equal(first.Spki, Identity.SpkiOf(server.RemoteCertificate!));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
