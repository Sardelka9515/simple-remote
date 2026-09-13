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
}

public sealed class ApiPairResponse
{
    public required string DeviceId { get; set; }
    public required string Token { get; set; }
    public string HostName { get; set; } = Environment.MachineName;
}

/// <summary>HTTP surface: pairing, album art, health, and the WebSocket upgrade.</summary>
public static class ApiEndpoints
{
    public static void Map(WebApplication app, RemoteServer server, WebHost host)
    {
        app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

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

            if (!server.Pairing.TryRedeem(request?.PairingToken))
            {
                server.RecordAuthFailure(ctx.Connection.RemoteIpAddress);

                // Deliberately vague: an expired token and a wrong token are indistinguishable
                // to the caller, but the user-facing copy in the UI explains how to get a new QR.
                return Results.Unauthorized();
            }

            server.RecordAuthSuccess(ctx.Connection.RemoteIpAddress);

            var name = string.IsNullOrWhiteSpace(request?.Name)
                ? DescribeClient(ctx.Request.Headers.UserAgent.ToString())
                : request.Name;

            var issued = server.Devices.Register(name);

            return Results.Json(
                new ApiPairResponse { DeviceId = issued.DeviceId, Token = issued.Token },
                AppJson.Default.ApiPairResponse);
        });

        app.MapGet("/api/albumart/{hash}", (string hash) =>
            server.Media.TryGetArt(hash, out var bytes, out var contentType)
                // Content-addressed, so this specific URL can never change meaning.
                ? Results.File(bytes, contentType)
                : Results.NotFound());

        app.MapGet("/ws", async (HttpContext ctx) =>
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
