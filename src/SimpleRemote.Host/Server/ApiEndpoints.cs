using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SimpleRemote.Config;

namespace SimpleRemote.Server;

public sealed class ApiPairRequest
{
    public string? PairingToken { get; set; }
    public string? Name { get; set; }

    /// <summary>
    /// The credential this phone already holds for this host, if any. When it still checks out,
    /// the scan refreshes that device's token instead of registering a second entry for the same
    /// phone.
    /// </summary>
    public string? ExistingDeviceId { get; set; }
    public string? ExistingToken { get; set; }
}

public sealed class ApiPairResponse
{
    public required string DeviceId { get; set; }
    public required string Token { get; set; }
    public string HostName { get; set; } = Environment.MachineName;
}

/// <summary>
/// Health probe payload.
///
/// A named type rather than an anonymous one specifically so it can be source-generated: under
/// Native AOT there is no reflection-based serializer to fall back on, and an anonymous type here
/// makes the endpoint return 500.
/// </summary>
public sealed class ApiHealthResponse
{
    public bool Ok { get; set; } = true;
}

/// <summary>HTTP surface: pairing, album art, health, and the WebSocket upgrade.</summary>
public static class ApiEndpoints
{
    public static void Map(WebApplication app, RemoteServer server, WebHost host)
    {
        app.MapGet("/healthz", () => Results.Json(new ApiHealthResponse(), AppJson.Default.ApiHealthResponse));

        app.MapPost("/api/pair", async (HttpContext ctx) =>
        {
            // Redeeming a pairing token is the one unauthenticated write, so it is throttled on
            // the same counter as failed logins.
            if (server.IsAuthThrottled(ctx.Connection.RemoteIpAddress))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            ApiPairRequest? request;
            try
            {
                request = await ctx.Request.ReadFromJsonAsync(AppJson.Default.ApiPairRequest);
            }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
            {
                return Results.BadRequest();
            }

            var redeemed = server.RedeemPairingToken(request?.PairingToken);

            if (redeemed == PairingTokenKind.None)
            {
                server.RecordAuthFailure(ctx.Connection.RemoteIpAddress);

                // Deliberately vague: an expired token and a wrong token are indistinguishable
                // to the caller, but the user-facing copy in the UI explains how to get a new QR.
                return Results.Unauthorized();
            }

            server.RecordAuthSuccess(ctx.Connection.RemoteIpAddress);

            // A handoff deliberately pairs the same phone again under its secure origin as a
            // second, labelled record (see below), so only a plain QR scan reuses an existing
            // device - a phone that already holds a working credential for this host rescanning
            // should refresh it in place, not add a duplicate row.
            var issued = redeemed == PairingTokenKind.Qr
                ? server.Devices.Reauthenticate(request?.ExistingDeviceId, request?.ExistingToken)
                : null;

            if (issued is null)
            {
                var name = string.IsNullOrWhiteSpace(request?.Name)
                    ? DescribeClient(ctx.Request.Headers.UserAgent.ToString())
                    : request.Name;

                // A handoff pairs the same phone again under its secure origin, so it gets its own
                // record. Labelled, so the device list does not look like a stranger paired.
                if (redeemed == PairingTokenKind.Handoff) name += " (secure)";

                issued = server.Devices.Register(name);
            }

            return Results.Json(
                new ApiPairResponse { DeviceId = issued.DeviceId, Token = issued.Token },
                AppJson.Default.ApiPairResponse);
        });

        app.MapGet("/api/albumart/{hash}", (string hash) =>
            server.Media.TryGetArt(hash, out var bytes, out var contentType)
                // Content-addressed, so this specific URL can never change meaning.
                ? Results.File(bytes, contentType)
                : Results.NotFound());

        // GET and CONNECT: over TLS the browser negotiates HTTP/2, and a WebSocket over HTTP/2 is an
        // extended CONNECT request (RFC 8441), not an upgraded GET. A GET-only route answered it with
        // 405, so the secure page loaded but the remote never connected.
        app.MapMethods("/ws", [HttpMethods.Get, HttpMethods.Connect], async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();

            // Credentials are sent as the first WebSocket message rather than in the query string,
            // so they never appear in a URL, a proxy log, or the browser history.
            var connection = new WsConnection(socket, server, ctx.Connection.RemoteIpAddress);
            await connection.RunAsync(ctx.RequestAborted);
        });

        _ = host;
    }

    /// <summary>Best-effort friendly name so the paired-device list is not a wall of identical rows.</summary>
    private static string DescribeClient(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "Device";

        if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) return "iPhone";
        if (userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "iPad";
        if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android phone";
        if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows browser";
        if (userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)) return "Mac browser";

        return "Device";
    }
}
