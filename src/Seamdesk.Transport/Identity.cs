using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Seamdesk.Transport;

/// <summary>Where an install keeps its long-term certificate and private key (PKCS#12 bytes).</summary>
public interface IIdentityStore
{
    byte[]? Load();

    void Save(byte[] pkcs12);
}

/// <summary>Plain file, readable by the current user only (mode 0600). For tests and Linux; Windows uses DPAPI (Seamdesk.App).</summary>
public sealed class FileIdentityStore(string path) : IIdentityStore
{
    public byte[]? Load() => File.Exists(path) ? File.ReadAllBytes(path) : null;

    public void Save(byte[] pkcs12)
    {
        using FileStream file = OperatingSystem.IsWindows()
            ? new FileStream(path, FileMode.Create)
            : new FileStream(path, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        file.Write(pkcs12);
    }
}

/// <summary>This install's long-term key pair and self-signed certificate.</summary>
public sealed class Identity
{
    private Identity(X509Certificate2 certificate) => Certificate = certificate;

    public X509Certificate2 Certificate { get; }

    public string Spki => SpkiOf(Certificate);

    /// <summary>
    /// The stored identity, or a new one (saved) if there is none. Reading the store runs off the caller's thread and gets
    /// <paramref name="timeout"/>: DPAPI has no timeout of its own (CryptUnprotectData docs). A store that refuses or
    /// doesn't answer throws <see cref="UnreadableIdentityException"/>, and nothing is saved over it.
    /// </summary>
    public static async Task<Identity> LoadOrCreateAsync(IIdentityStore store, string machineName, TimeSpan timeout, TimeProvider time)
    {
        byte[]? pkcs12;
        try
        {
            pkcs12 = await Task.Run(store.Load).WaitAsync(timeout, time).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new UnreadableIdentityException($"reading it took over {timeout.TotalSeconds:0} s", e);
        }
        catch (CryptographicException e)
        {
            throw new UnreadableIdentityException(e.Message, e);
        }

        if (pkcs12 is null)
        {
            pkcs12 = CreatePkcs12(machineName);
            store.Save(pkcs12);
        }

        return new Identity(X509CertificateLoader.LoadPkcs12(pkcs12, null));
    }

    /// <summary>SHA-256 of the certificate's SubjectPublicKeyInfo, hex. This is what a peer pins.</summary>
    public static string SpkiOf(X509Certificate certificate)
    {
        using X509Certificate2 cert = new(certificate);
        return Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    private static byte[] CreatePkcs12(string machineName)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new(new X500DistinguishedName($"CN=Seamdesk {machineName}"), key, HashAlgorithmName.SHA256);
        using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(100));
        return cert.Export(X509ContentType.Pkcs12);
    }
}

/// <summary>The identity store holds an identity this logon can't read (another logon's DPAPI key, a damaged file) or didn't answer.</summary>
public sealed class UnreadableIdentityException(string message, Exception inner) : Exception(message, inner);
