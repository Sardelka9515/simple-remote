using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using SimpleRemote.Clipboard;
using SimpleRemote.Config;
using SimpleRemote.Input;
using SimpleRemote.Media;
using SimpleRemote.Pairing;
using SimpleRemote.Server;
using SimpleRemote.Server.Protocol;
using SimpleRemote.Shortcuts;
using Xunit;

namespace SimpleRemote.Tests;

public sealed class SecurePortTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sr-cert-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// What a phone browser needs from the certificate: every address it may connect to named in
    /// the SAN (iOS ignores the common name), server-auth usage, and a private key Kestrel can use.
    /// </summary>
    [Fact]
    public void GeneratedCertificateNamesTheLanAddressesAndCanServeTls()
    {
        using var certificate = new CertificateStore(_dir).LoadOrCreate(["192.168.1.20", "10.0.0.5"]);

        Assert.True(certificate.HasPrivateKey);

        var addresses = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateIPAddresses()).ToList();
        Assert.Contains(IPAddress.Parse("192.168.1.20"), addresses);
        Assert.Contains(IPAddress.Parse("10.0.0.5"), addresses);
        Assert.Contains(IPAddress.Loopback, addresses);

        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(usage.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");

        // Under Apple's 825-day ceiling for TLS server certificates.
        Assert.True(certificate.NotAfter - certificate.NotBefore < TimeSpan.FromDays(825));

        // The key must be usable, not just present: this is what SChannel needs to sign a handshake.
        using var key = certificate.GetECDsaPrivateKey();
        Assert.NotNull(key);
        Assert.NotEmpty(key.SignData([1, 2, 3], System.Security.Cryptography.HashAlgorithmName.SHA256));
    }

    /// <summary>
    /// Reuse is the point: the phone's certificate exception is tied to this certificate, so a new
    /// one on every start would bring the browser warning back every time.
    /// </summary>
    /// <summary>
    /// The real proof: Kestrel completes a TLS handshake with the generated certificate. A certificate
    /// with an ephemeral key passes every property check above and still fails here, because
    /// Windows' TLS stack refuses to use it.
    /// </summary>
    [Fact]
    public async Task KestrelServesTlsWithTheGeneratedCertificate()
    {
        using var certificate = new CertificateStore(_dir).LoadOrCreate(["127.0.0.1"]);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));

        await using var app = builder.Build();
        app.MapGet("/", () => "secure");
        await app.StartAsync();

        try
        {
            var address = app.Urls.Single();
            string? servedThumbprint = null;

            using var handler = new HttpClientHandler
            {
                // Self-signed by design; what matters is which certificate was presented.
                ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                {
                    servedThumbprint = cert?.Thumbprint;
                    return true;
                },
            };
            using var client = new HttpClient(handler);

            Assert.Equal("secure", await client.GetStringAsync(address));
            Assert.Equal(certificate.Thumbprint, servedThumbprint);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>
    /// The reported bug: over HTTPS the page loaded but the remote never connected. TLS lets the
    /// browser negotiate HTTP/2, and Chrome then opens WebSockets over HTTP/2 - as a CONNECT request,
    /// which a GET-only route never matches. Plain HTTP is always HTTP/1.1 GET, so it hid this.
    /// </summary>
    [Theory]
    [InlineData("2.0")]
    [InlineData("1.1")]
    public async Task WebSocketAuthenticatesOverTls(string httpVersion)
    {
        using var certificate = new CertificateStore(_dir).LoadOrCreate(["127.0.0.1"]);
        using var server = BuildServer();
        var device = server.Devices.Register("test phone");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));

        await using var app = builder.Build();
        app.UseWebSockets();
        ApiEndpoints.Map(app, server, new WebHost(server));
        await app.StartAsync();

        try
        {
            var uri = new Uri(app.Urls.Single().Replace("https://", "wss://") + "/ws");

            using var handler = new SocketsHttpHandler
            {
                SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            };
            using var invoker = new HttpMessageInvoker(handler);
            using var socket = new System.Net.WebSockets.ClientWebSocket();
            socket.Options.HttpVersion = Version.Parse(httpVersion);
            socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(uri, invoker, timeout.Token);

            var auth = $$"""{"t":"auth","deviceId":"{{device.DeviceId}}","token":"{{device.Token}}"}""";
            await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(auth),
                System.Net.WebSockets.WebSocketMessageType.Text, true, timeout.Token);

            var buffer = new byte[4096];
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            var reply = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);

            Assert.Contains("\"ok\":true", reply.Replace(" ", ""));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public void CertificateIsReusedAcrossStarts()
    {
        using var first = new CertificateStore(_dir).LoadOrCreate(["192.168.1.20"]);
        using var second = new CertificateStore(_dir).LoadOrCreate(["192.168.1.20"]);

        Assert.Equal(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public void NewNetworkAddressGetsANewCertificate()
    {
        using var first = new CertificateStore(_dir).LoadOrCreate(["192.168.1.20"]);
        using var moved = new CertificateStore(_dir).LoadOrCreate(["192.168.50.7"]);

        Assert.NotEqual(first.Thumbprint, moved.Thumbprint);
    }

    [Fact]
    public void CertificateNearExpiryIsReplaced()
    {
        var time = new FakeTime(DateTimeOffset.UtcNow);
        using var first = new CertificateStore(_dir, time).LoadOrCreate(["192.168.1.20"]);

        time.Now = time.Now.AddDays(360);
        using var renewed = new CertificateStore(_dir, time).LoadOrCreate(["192.168.1.20"]);

        Assert.NotEqual(first.Thumbprint, renewed.Thumbprint);
    }

    [Fact]
    public void CorruptCertificateFileIsRegenerated()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, CertificateStore.FileName), [1, 2, 3, 4]);

        using var certificate = new CertificateStore(_dir).LoadOrCreate(["192.168.1.20"]);

        Assert.True(certificate.HasPrivateKey);
    }

    // ---- handoff -------------------------------------------------------------------

    private static RemoteServer BuildServer()
    {
        var config = new ConfigStore();
        var injector = new InputInjector();
        var media = new MediaController(injector);
        var shortcuts = new ShortcutService(config, injector, media);

        return new RemoteServer(
            config,
            new DeviceStore(Path.Combine(Path.GetTempPath(), $"sr-handoff-{Guid.NewGuid():N}.json")),
            new PairingTokenSource(),
            injector,
            media,
            new VolumeController(),
            new ClipboardService(),
            shortcuts);
    }

    [Fact]
    public void HandoffTokenPairsOnceAndIsReportedAsHandoff()
    {
        using var server = BuildServer();
        var token = server.Handoff.Issue();

        Assert.Equal(PairingTokenKind.Handoff, server.RedeemPairingToken(token));
        Assert.Equal(PairingTokenKind.None, server.RedeemPairingToken(token));
    }

    /// <summary>
    /// The pairing source holds one token at a time. A handoff sharing it would silently kill the QR
    /// code someone is about to scan, or a scan would kill a handoff in flight.
    /// </summary>
    [Fact]
    public void HandoffAndQrTokensDoNotInvalidateEachOther()
    {
        using var server = BuildServer();

        var qr = server.Pairing.Issue();
        var handoff = server.Handoff.Issue();

        Assert.Equal(PairingTokenKind.Qr, server.RedeemPairingToken(qr));
        Assert.Equal(PairingTokenKind.Handoff, server.RedeemPairingToken(handoff));
    }

    [Fact]
    public void ConfigAdvertisesTheSecurePortOnlyOnceListening()
    {
        using var server = BuildServer();
        Assert.Equal(0, server.DescribeConfig().SecurePort);

        server.SecurePort = 8788;
        Assert.Equal(8788, server.DescribeConfig().SecurePort);
    }

    [Fact]
    public void HandoffMessageSerialisesWithItsDiscriminator()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new SecureHandoffMessage { Token = "abc" }, AppJson.Default.SecureHandoffMessage);

        Assert.Contains("\"t\":\"secureHandoff\"", json.Replace(" ", "").Replace("\n", "").Replace("\r", ""));
        Assert.Contains("\"token\":\"abc\"", json.Replace(" ", "").Replace("\n", "").Replace("\r", ""));
    }
}
