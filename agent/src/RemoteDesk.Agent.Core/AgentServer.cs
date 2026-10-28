using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RemoteDesk.Agent.Core;

/// <summary>WebSocket-сервер агента поверх TLS: wss://ip:port/ws.</summary>
public sealed class AgentServer(AgentIdentity identity, PairingStore devices, PairingSecrets secrets, Func<AgentInfo> info)
    : IAsyncDisposable
{
    WebApplication? app;
    readonly ConcurrentDictionary<Session, byte> sessions = new();

    public int Port { get; private set; }

    /// <summary>Телефон только что сопрягся (id, имя).</summary>
    public event Action<PairedDevice>? Paired;

    /// <summary>Изменился список подключённых сейчас телефонов.</summary>
    public event Action? ConnectionsChanged;

    internal AgentIdentity Identity => identity;
    internal PairingStore Devices => devices;
    internal PairingSecrets Secrets => secrets;
    internal AgentInfo Info() => info();

    public IReadOnlyCollection<string> ConnectedDeviceIds =>
        sessions.Keys.Where(s => s.DeviceId is not null).Select(s => s.DeviceId!).Distinct().ToList();

    public async Task StartAsync(int port, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
            k.Listen(IPAddress.Any, port, listen => listen.UseHttps(identity.Certificate)));

        app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Map("/ws", HandleAsync);
        await app.StartAsync(ct);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address.Replace("0.0.0.0", "localhost")).Port;
    }

    async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var session = new Session(this, ws);
        sessions[session] = 0;
        try
        {
            await session.RunAsync(ctx.RequestAborted);
        }
        finally
        {
            sessions.TryRemove(session, out _);
            if (session.DeviceId is not null)
                ConnectionsChanged?.Invoke();
        }
    }

    internal void OnAuthenticated(Session session, PairedDevice device, bool justPaired)
    {
        if (justPaired)
            Paired?.Invoke(device);
        ConnectionsChanged?.Invoke();
    }

    /// <summary>Отключает телефон (например, после отзыва доступа).</summary>
    public async Task DisconnectAsync(string deviceId, string code, string msg)
    {
        foreach (var session in sessions.Keys.Where(s => s.DeviceId == deviceId).ToList())
            await session.CloseWithErrorAsync(code, msg);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in sessions.Keys.ToList())
            await session.SendAsync(Message.Build("bye", ("reason", "agent_stopped")));
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

/// <summary>Одно соединение с телефоном.</summary>
internal sealed class Session(AgentServer server, WebSocket ws)
{
    const int MaxMessageBytes = 1 << 20;
    readonly SemaphoreSlim sendLock = new(1, 1);

    public string? DeviceId { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var text = await ReceiveAsync(ct);
                if (text is null)
                    break;
                await HandleAsync(text);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            // Телефон пропал из сети — это нормально.
        }
    }

    async Task HandleAsync(string text)
    {
        var msg = Message.Parse(text);
        if (msg is null)
        {
            await SendAsync(Message.Error(Protocol.Errors.BadMessage, "Сообщение не разобрано"));
            return;
        }

        var type = msg.Str("t");
        if (DeviceId is null && type is not ("pair" or "hello"))
        {
            await CloseWithErrorAsync(Protocol.Errors.NotAuthenticated, "Сначала сопряжение или вход");
            return;
        }

        switch (type)
        {
            case "pair":
                await PairAsync(msg);
                break;
            case "hello":
                await HelloAsync(msg);
                break;
            case "ping":
                await SendAsync(Message.Build("pong", ("n", msg.Num("n"))));
                break;
            case "bye":
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                break;
            default:
                await SendAsync(Message.Error(Protocol.Errors.BadMessage, $"Неизвестный тип «{type}»"));
                break;
        }
    }

    async Task PairAsync(System.Text.Json.Nodes.JsonObject msg)
    {
        var deviceId = msg.Str("deviceId");
        var secret = msg.Str("secret");
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrEmpty(secret))
        {
            await SendAsync(Message.Error(Protocol.Errors.BadMessage, "Нет deviceId или secret"));
            return;
        }

        switch (server.Secrets.TryConsume(secret))
        {
            case PairResult.Bad:
                await CloseWithErrorAsync(Protocol.Errors.BadSecret, "Неверный ключ сопряжения. Отсканируйте QR-код заново");
                return;
            case PairResult.Expired:
                await CloseWithErrorAsync(Protocol.Errors.SecretExpired, "QR-код устарел. Откройте его на компьютере заново");
                return;
        }

        var name = Limit(msg.Str("deviceName"), 64) ?? "Телефон";
        var platform = Limit(msg.Str("platform"), 16) ?? "unknown";
        var token = server.Devices.Add(deviceId, name, platform);
        var device = server.Devices.All.First(d => d.DeviceId == deviceId);
        DeviceId = deviceId;
        await SendAsync(Message.Build("paired", ("token", token), ("agent", server.Info())));
        server.OnAuthenticated(this, device, justPaired: true);
    }

    async Task HelloAsync(System.Text.Json.Nodes.JsonObject msg)
    {
        var deviceId = msg.Str("deviceId");
        var token = msg.Str("token");
        var device = deviceId is null || token is null ? null : server.Devices.Validate(deviceId, token);
        if (device is null)
        {
            await CloseWithErrorAsync(Protocol.Errors.Revoked, "Этот телефон не сопряжён с компьютером. Подключите его заново");
            return;
        }
        DeviceId = device.DeviceId;
        await SendAsync(Message.Build("welcome", ("agent", server.Info()), ("permissions", Protocol.DefaultPermissions)));
        server.OnAuthenticated(this, device, justPaired: false);
    }

    static string? Limit(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s.Trim() : s[..max].Trim();

    async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > MaxMessageBytes)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "too big", ct);
                return null;
            }
            if (result.EndOfMessage)
                break;
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    public async Task SendAsync(string text)
    {
        if (ws.State != WebSocketState.Open)
            return;
        await sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async Task CloseWithErrorAsync(string code, string msg)
    {
        await SendAsync(Message.Error(code, msg));
        try
        {
            // CloseOutputAsync не ждёт ответного кадра закрытия: зависший телефон не задержит агента.
            if (ws.State == WebSocketState.Open)
                await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, code, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
        }
    }
}
