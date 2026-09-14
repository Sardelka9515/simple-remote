using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleRemote.Config;

namespace SimpleRemote.Server;

/// <summary>Builds and owns the Kestrel host.</summary>
public sealed class WebHost(RemoteServer server)
{
    private WebApplication? _app;

    public int Port { get; private set; }
    public bool IsHttps { get; private set; }

    public string Scheme => IsHttps ? "https" : "http";

    public async Task StartAsync()
    {
        var config = server.Config.Current;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "SimpleRemote",
        });

        // Per-message logging would dominate the cost of the input path, and there is no console
        // to read it on anyway.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        ConfigureKestrel(builder, config);

        _app = builder.Build();

        _app.UseWebSockets(new WebSocketOptions
        {
            // Frequent small frames also stop phone Wi-Fi power-save from parking the radio
            // between gestures, which is a real and easily-missed source of stutter.
            KeepAliveInterval = TimeSpan.FromSeconds(15),

            // Without a timeout the keep-alive only sends, it never notices silence. A phone that
            // slept or left the network leaves no FIN behind, so its connection would sit in the
            // hub - counted in the tray and broadcast to - until a send happened to fail. The
            // browser answers these pings itself, even for a backgrounded tab.
            KeepAliveTimeout = TimeSpan.FromSeconds(10),
        });

        MapStaticFiles(_app);
        ApiEndpoints.Map(_app, server, this);

        await _app.StartAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Single place that decides how we bind. The HTTPS branch is the seam that lets the whole
    /// stack move to a secure context later without touching anything else - the client already
    /// derives its WebSocket scheme from location.protocol.
    /// </summary>
    private void ConfigureKestrel(WebApplicationBuilder builder, AppConfig config)
    {
        Port = config.Port;
        IsHttps = config.UseHttps && !string.IsNullOrWhiteSpace(config.CertPath) && File.Exists(config.CertPath);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;

            // Binding to Any rather than a specific NIC means the QR can point at whichever
            // address the user picks without a restart, and needs no urlacl (unlike HTTP.sys).
            options.Listen(IPAddress.Any, Port, listen =>
            {
                if (!IsHttps) return;

                listen.UseHttps(System.Security.Cryptography.X509Certificates.X509CertificateLoader
                    .LoadPkcs12FromFile(config.CertPath!, config.CertPassword));
            });
        });
    }

    private static void MapStaticFiles(WebApplication app)
    {
        var provider = CreateWebRootProvider();

        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            ContentTypeProvider = contentTypes,
            OnPrepareResponse = ctx =>
            {
                // The UI is versioned with the executable, so let the phone cache it hard but
                // revalidate - a stale UI against a new host is a confusing failure mode.
                ctx.Context.Response.Headers.CacheControl = "no-cache";
            },
        });
    }

    /// <summary>
    /// Serves the UI from the embedded manifest, so a single-file publish is genuinely standalone.
    /// In a debug build it prefers the on-disk copy, so UI edits show up on refresh with no rebuild.
    /// </summary>
    private static IFileProvider CreateWebRootProvider()
    {
#if DEBUG
        var onDisk = FindSourceWebRoot();
        if (onDisk is not null) return new PhysicalFileProvider(onDisk);
#endif
        return new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
    }

    private static string? FindSourceWebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "wwwroot");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.html")))
                return candidate;
        }
        return null;
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        _app = null;
    }
}
