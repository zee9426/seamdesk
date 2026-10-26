using System.Security.Cryptography;
using System.Text;
using Seamdesk.Mwb;

namespace Seamdesk.App;

/// <summary>Each MWB peer's security key: typed in the settings window and kept with DPAPI, for this Windows account.</summary>
internal static class MwbKeys
{
    // The project's first name, Glide: keys saved before the rename stay readable (install.ps1 moves them over).
    private static readonly byte[] Entropy = "glide mwb key v1"u8.ToArray();

    public static bool Has(string peer) => File.Exists(KeyPath(peer));

    /// <summary>Keeps the key the user typed, encrypted for this Windows account. Returns why it was refused, or null.</summary>
    public static string? Set(string peer, string typed)
    {
        try
        {
            _ = new MwbKey(typed);
        }
        catch (ArgumentException)
        {
            return "Mouse Without Borders keys are at least 16 characters long (spaces don't count).";
        }

        Directory.CreateDirectory(Log.Dir);
        File.WriteAllBytes(KeyPath(peer), ProtectedData.Protect(Encoding.UTF8.GetBytes(typed), Entropy, DataProtectionScope.CurrentUser));
        return null;
    }

    public static void Forget(string peer) => File.Delete(KeyPath(peer));

    /// <summary>The peer's key, or null if none is kept.</summary>
    public static MwbKey? Key(string peer) => Has(peer)
        ? new MwbKey(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyPath(peer)), Entropy, DataProtectionScope.CurrentUser)))
        : null;

    private static string KeyPath(string peer) => Path.Combine(Log.Dir, $"mwb-{peer}.key");
}
