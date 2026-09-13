using System.Security.Cryptography;

namespace SimpleRemote.Pairing;

/// <summary>
/// Mints the short-lived, single-use tokens carried in the pairing QR code.
///
/// The token is deliberately weak-lifetime rather than weak-entropy: it is worthless 5 minutes
/// after the QR window opens, and worthless immediately after one phone redeems it, so a photo
/// of the screen does not grant lasting access.
/// </summary>
public sealed class PairingTokenSource(TimeProvider? time = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();

    private string? _token;
    private DateTimeOffset _expires;

    /// <summary>Invalidates any previous token and returns a fresh one.</summary>
    public string Issue()
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            _token = token;
            _expires = _time.GetUtcNow() + Lifetime;
        }
        return token;
    }

    /// <summary>Consumes the token if it matches and has not expired. Single use.</summary>
    public bool TryRedeem(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;

        lock (_gate)
        {
            if (_token is null) return false;
            if (_time.GetUtcNow() > _expires) { _token = null; return false; }

            // Fixed-time compare: this is reachable by an unauthenticated caller.
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(_token),
                    System.Text.Encoding.UTF8.GetBytes(candidate)))
                return false;

            _token = null; // single use
            return true;
        }
    }

    public void Revoke()
    {
        lock (_gate) _token = null;
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
