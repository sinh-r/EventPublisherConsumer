using System.Security.Cryptography;

namespace EventScope.App.Connections;

/// <summary>
/// Protects a saved connection's SASL password at rest via Windows DPAPI, current-user
/// scope — enough to keep a plaintext credential out of <c>connections.json</c> without
/// standing up a real secret store for a single-user desktop tool. Never throws: a failure
/// either way means "don't persist/don't use this secret", not a crash, since a broken
/// credential must degrade to "reconnect and retype the password", never to a hang or an
/// unhandled exception on the ingest path.
/// </summary>
public static class ConnectionSecretProtector
{
    private static readonly byte[] Entropy = "EventScope.ConnectionProfile"u8.ToArray();

    /// <summary>Returns the DPAPI-protected, base64-encoded ciphertext for
    /// <paramref name="plaintext"/>, or <see langword="null"/> if protection failed (caller
    /// must then simply not persist the secret, rather than fall back to writing it
    /// plaintext).</summary>
    public static string? Protect(string plaintext)
    {
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
            // CA1416: ProtectedData is Windows-only. The catch below is exactly that guard —
            // this type already degrades to "don't persist the secret" on any other platform
            // rather than assuming Windows, so the warning doesn't apply here.
#pragma warning disable CA1416
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
            return Convert.ToBase64String(protectedBytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Attempts to recover the plaintext from a value previously returned by
    /// <see cref="Protect"/>. Returns <see langword="false"/> (never throws) on any failure —
    /// a corrupt value, one written by a different Windows user account, or a non-Windows
    /// host all just mean "no password available", not a crash.</summary>
    public static bool TryUnprotect(string? protectedBase64, out string plaintext)
    {
        plaintext = string.Empty;
        if (string.IsNullOrEmpty(protectedBase64)) return false;

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedBase64);
            // See Protect's matching pragma — the catch clauses here are the cross-platform
            // guard the analyzer wants, just expressed at runtime instead of compile time.
#pragma warning disable CA1416
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
            plaintext = System.Text.Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}
