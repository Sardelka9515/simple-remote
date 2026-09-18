using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SimpleRemote.Config;

namespace SimpleRemote.Server;

/// <summary>
/// The self-signed certificate behind the secure port.
///
/// HTTPS exists here for one reason: browsers only hand motion-sensor data (and clipboard read,
/// and screen wake lock) to secure pages. A LAN address can never get a publicly trusted
/// certificate, so the phone shows a warning once and the user accepts it. What this class
/// controls is making that the *only* friction: the certificate is generated on first run,
/// reused afterwards so an accepted exception keeps working, and replaced only when it would stop
/// working anyway - expired, or no longer naming the addresses the phone connects to.
///
/// Shaped for mobile browsers' rules for server certificates: an ECDSA P-256 key, a subject
/// alternative name for every address (iOS ignores the common name), the server-auth extended
/// key usage, and a validity well under Apple's 825-day ceiling.
///
/// The private key sits unencrypted in the user's profile, next to devices.json, which already
/// holds everything an attacker with that access would want.
/// </summary>
public sealed class CertificateStore(string? directory = null, TimeProvider? time = null)
{
    public const string FileName = "https.pfx";

    private static readonly TimeSpan Validity = TimeSpan.FromDays(365);

    /// <summary>Replace a certificate this close to expiry, rather than let it lapse mid-session.</summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(14);

    private readonly string _directory = directory ?? ConfigStore.DataDirectory;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string FilePath => Path.Combine(_directory, FileName);

    /// <summary>
    /// The stored certificate if it still covers <paramref name="addresses"/> and is not about to
    /// expire; otherwise a newly generated one, which is saved for next time.
    /// </summary>
    public X509Certificate2 LoadOrCreate(IEnumerable<string> addresses)
    {
        var wanted = addresses
            .Select(a => IPAddress.TryParse(a, out var ip) ? ip : null)
            .OfType<IPAddress>()
            .Append(IPAddress.Loopback)
            .Distinct()
            .ToArray();

        var existing = TryLoad();
        if (existing is not null)
        {
            if (IsUsable(existing, wanted)) return existing;
            existing.Dispose();
        }

        return Create(wanted);
    }

    private X509Certificate2? TryLoad()
    {
        if (!File.Exists(FilePath)) return null;

        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(FilePath, password: null);
        }
        catch (CryptographicException)
        {
            // Corrupt or from an incompatible build: regenerating is the whole recovery.
            return null;
        }
    }

    private bool IsUsable(X509Certificate2 certificate, IReadOnlyCollection<IPAddress> wanted)
    {
        if (!certificate.HasPrivateKey) return false;

        var now = _time.GetUtcNow();
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime() - RenewBefore)
            return false;

        var covered = certificate.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateIPAddresses())
            .ToHashSet();

        // A new address (the PC moved networks) means a certificate that names the wrong host.
        // Browsers already distrust it, but a name mismatch is a different, scarier warning.
        return wanted.All(covered.Contains);
    }

    private X509Certificate2 Create(IReadOnlyCollection<IPAddress> addresses)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(
            $"CN=Simple Remote ({Environment.MachineName})", key, HashAlgorithmName.SHA256);

        var names = new SubjectAlternativeNameBuilder();
        foreach (var address in addresses) names.AddIpAddress(address);
        names.AddDnsName("localhost");
        if (Uri.CheckHostName(Environment.MachineName) == UriHostNameType.Dns) names.AddDnsName(Environment.MachineName);

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")], false));

        var now = _time.GetUtcNow();

        // Round-tripped through PFX bytes on purpose: a certificate straight out of CreateSelfSigned
        // has an ephemeral key, which Windows' TLS stack (SChannel) refuses to use.
        using var generated = request.CreateSelfSigned(now.AddDays(-1), now + Validity);
        var pfx = generated.Export(X509ContentType.Pfx);

        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(FilePath, pfx);

        return X509CertificateLoader.LoadPkcs12(pfx, password: null);
    }
}
