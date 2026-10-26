using System.Security.Cryptography;
using Seamdesk.Transport;

namespace Seamdesk.App;

/// <summary>The identity's PKCS#12 bytes in a file, encrypted with DPAPI for the current user only.</summary>
internal sealed class DpapiIdentityStore(string path) : IIdentityStore
{
    // The project's first name, Glide: identities saved before the rename stay readable (install.ps1 moves them over).
    private static readonly byte[] Entropy = "glide identity v1"u8.ToArray();

    public byte[]? Load() =>
        File.Exists(path) ? ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser) : null;

    public void Save(byte[] pkcs12)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProtectedData.Protect(pkcs12, Entropy, DataProtectionScope.CurrentUser));
    }
}
