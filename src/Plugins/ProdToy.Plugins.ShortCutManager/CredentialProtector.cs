using System.Security.Cryptography;
using System.Text;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Thin wrapper over <see cref="ProtectedData"/> for storing shortcut
/// credentials. Uses <see cref="DataProtectionScope.CurrentUser"/> so the
/// encrypted blob can only be decrypted by the same Windows user on the
/// same machine — which lines up with the per-envId data folders, since
/// the shortcuts.json that holds the blob is already machine-local.
///
/// All inputs/outputs are strings to keep <c>shortcuts.json</c> readable:
/// plaintext password in / out, base64 blob in storage.
/// </summary>
static class CredentialProtector
{
    public static string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            // DPAPI can fail (e.g. roaming profile not loaded). Returning
            // empty rather than throwing keeps shortcut save UX usable; the
            // user just won't have credentials stored.
            return "";
        }
    }

    public static string Decrypt(string encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64)) return "";
        try
        {
            var protectedBytes = Convert.FromBase64String(encryptedBase64);
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Most common cause: the blob was encrypted on a different
            // machine/user and synced over. We can't recover; return empty
            // so the caller surfaces "no password" rather than crashing.
            return "";
        }
    }
}
