using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace SteamPhantom.Services;

/// <summary>
/// Persists SteamKit2 refresh tokens to disk encrypted with the current
/// Windows user's DPAPI key. The blob can only be read back by the same
/// user on the same machine — no cleartext token on disk.
/// </summary>
public class SteamAuthStore
{
    private static readonly byte[] Entropy = "SteamPhantom.SteamAuth"u8.ToArray();

    private string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SteamPhantom", "auth.bin");

    public bool HasStoredAuth => File.Exists(FilePath);

    public void Save(string accountName, string refreshToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var clear = JsonSerializer.SerializeToUtf8Bytes(new StoredAuth(accountName, refreshToken));
        var encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, encrypted);
    }

    public (string AccountName, string RefreshToken)? TryLoad()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(FilePath);
            var clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize<StoredAuth>(clear);
            if (stored is null || string.IsNullOrEmpty(stored.RefreshToken)) return null;
            return (stored.AccountName, stored.RefreshToken);
        }
        catch
        {
            return null;
        }
    }

    public void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    private record StoredAuth(string AccountName, string RefreshToken);
}
