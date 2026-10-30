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

/// <summary>Что агент умеет делать по просьбе телефона. В тестах всё подменяется записью.</summary>
/// <param name="Recycle">Удалить в корзину (в тестах — просто удалить).</param>
public sealed record AgentServices(
    IInputSink Input,
    PowerScheduler Power,
    IWakeOnLanSetup Wol,
    FileService Files,
    IClipboard Clipboard,
    Action<string> Recycle);

/// <summary>Сервер агента поверх TLS: wss://ip:port/ws для управления и https://ip:port/api/… для файлов.</summary>
public sealed class AgentServer(
    AgentIdentity identity,
    PairingStore devices,
    PairingSecrets secrets,
    Func<AgentInfo> info,
    AgentServices services)
    : IAsyncDisposable
{
    readonly IInputSink input = services.Input;
    readonly PowerScheduler power = services.Power;
    readonly IWakeOnLanSetup wol = services.Wol;
    WebApplication? app;
    readonly ConcurrentDictionary<Session, byte> sessions = new();
    bool subscribed;

    public int Port { get; private set; }

    /// <summary>Телефон только что сопрягся (id, имя).</summary>
    public event Action<PairedDevice>? Paired;

    /// <summary>Изменился список подключённых сейчас телефонов.</summary>
    public event Action? ConnectionsChanged;

    internal AgentIdentity Identity => identity;
    internal PairingStore Devices => devices;
    internal PairingSecrets Secrets => secrets;
    internal AgentInfo Info() => info();
    internal IInputSink Input => input;
    internal PowerScheduler Power => power;
    internal IWakeOnLanSetup Wol => wol;
    internal AgentServices Services => services;

    /// <summary>Телефон попросил открыть на ПК мастер включения по сети.</summary>
    public event Action? WolSetupRequested;

    internal void RequestWolSetup() => WolSetupRequested?.Invoke();

    internal static string WolMessage(WolReport report) => Message.Build(
        "wol_status",
        ("ready", report.Ready),
        ("canFix", report.CanFix),
        ("checks", report.Checks.Select(c => new
        {
            id = c.Id,
            state = c.State.ToString().ToLowerInvariant(),
            title = c.Title,
            hint = c.Hint,
        }).ToList()));

    /// <summary>Сообщение о таймере питания: {"t":"power_timer"} без полей — таймера нет.</summary>
    internal static string TimerMessage(PowerTimer? timer) => timer is null
        ? Message.Build("power_timer")
        : Message.Build("power_timer", ("a", timer.Action.Name()), ("at", timer.At.ToUnixTimeMilliseconds()),
            ("now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("force", timer.Force));

    async void BroadcastTimer(PowerTimer? timer)
    {
        var text = TimerMessage(timer);
        foreach (var session in sessions.Keys.Where(s => s.DeviceId is not null).ToList())
            await session.SendAsync(text);
    }

    /// <summary>Сообщение об ошибке всем подключённым телефонам (телефон покажет его внизу экрана).</summary>
    public async Task NotifyAsync(string code, string msg)
    {
        var text = Message.Error(code, msg);
        foreach (var session in sessions.Keys.Where(s => s.DeviceId is not null).ToList())
            await session.SendAsync(text);
    }

    public IReadOnlyCollection<string> ConnectedDeviceIds =>
        sessions.Keys.Where(s => s.DeviceId is not null).Select(s => s.DeviceId!).Distinct().ToList();

    public async Task StartAsync(int port, bool loopback = false, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
            k.Listen(loopback ? IPAddress.Loopback : IPAddress.Any, port, listen => listen.UseHttps(identity.Certificate)));

        if (!subscribed)
        {
            power.Changed += BroadcastTimer;
            subscribed = true;
        }

        app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Map("/ws", HandleAsync);
        app.Map("/api/files/{**op}", ctx => FileApi.HandleAsync(this, ctx));
        app.Map("/api/clip/image", ctx => FileApi.HandleClipImageAsync(this, ctx));
        await app.StartAsync(ct);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost")).Port;
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
        power.Changed -= BroadcastTimer;
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
    readonly InputHandler input = new(server.Input);
    string[] permissions = [];

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
        finally
        {
            // Чтобы на ПК не «залип» Ctrl или зажатая кнопка мыши.
            input.ReleaseAll();
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
            case "power":
            case "power_cancel":
                if (!permissions.Contains("power"))
                {
                    await SendAsync(Message.Error(Protocol.Errors.Forbidden, "Управление питанием выключено на компьютере"));
                    break;
                }
                await PowerAsync(msg);
                break;
            case "clip_get":
            case "clip_set":
                if (!permissions.Contains("clipboard"))
                {
                    await SendAsync(Message.Error(Protocol.Errors.Forbidden, "Буфер обмена выключен на компьютере"));
                    break;
                }
                if (type == "clip_get")
                {
                    // Текст важнее: при копировании из Word или Excel в буфере и текст, и картинка.
                    // Саму картинку телефон заберёт по HTTPS (/api/clip/image) — в WebSocket она может не влезть.
                    var clipText = await server.Services.Clipboard.GetTextAsync() ?? "";
                    var hasImage = clipText.Length == 0 && await server.Services.Clipboard.GetImageAsync() is not null;
                    await SendAsync(Message.Build("clip", ("text", clipText), ("image", hasImage)));
                }
                else
                {
                    await server.Services.Clipboard.SetTextAsync(msg.Str("text") ?? "");
                    await SendAsync(Message.Build("clip_done"));
                }
                break;
            case "wol_check":
            case "wol_setup":
                if (type == "wol_setup")
                    server.RequestWolSetup();
                // Проверка запускает powercfg — не держим поток приёма.
                await SendAsync(AgentServer.WolMessage(await Task.Run(server.Wol.Check)));
                break;
            case var _ when InputHandler.Handles(type):
                if (!permissions.Contains("input"))
                {
                    await SendAsync(Message.Error(Protocol.Errors.Forbidden, "Управление мышью и клавиатурой выключено на компьютере"));
                    break;
                }
                if (input.Handle(msg) is { } problem)
                    await SendAsync(Message.Error(Protocol.Errors.BadMessage, problem));
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
        permissions = Protocol.DefaultPermissions;
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
        permissions = Protocol.DefaultPermissions;
        await SendAsync(Message.Build("welcome", ("agent", server.Info()), ("permissions", Protocol.DefaultPermissions)));
        if (server.Power.Current is { } timer)
            await SendAsync(AgentServer.TimerMessage(timer));
        server.OnAuthenticated(this, device, justPaired: false);
    }

    async Task PowerAsync(System.Text.Json.Nodes.JsonObject msg)
    {
        if (msg.Str("t") == "power_cancel")
        {
            // Если таймера уже нет, всё равно сообщаем текущее состояние: телефон мог его пропустить.
            if (!server.Power.Cancel())
                await SendAsync(AgentServer.TimerMessage(null));
            return;
        }

        if (PowerActions.Parse(msg.Str("a")) is not { } action)
        {
            await SendAsync(Message.Error(Protocol.Errors.BadMessage, $"Неизвестное действие «{msg.Str("a")}»"));
            return;
        }
        var force = msg.Bool("force") ?? false;
        var keep = msg.Bool("keep") ?? false;
        var delay = msg.Num("in") ?? 0;
        try
        {
            if (delay > 0)
            {
                if (TimeSpan.FromSeconds(delay) > PowerScheduler.MaxDelay)
                {
                    await SendAsync(Message.Error(Protocol.Errors.BadMessage, "Таймер — не больше суток"));
                    return;
                }
                server.Power.Schedule(action, TimeSpan.FromSeconds(delay), force, keep);
            }
            else
            {
                server.Power.Run(action, force, keep);
            }
        }
        catch (Exception e)
        {
            await SendAsync(Message.Error(Protocol.Errors.PowerFailed, e.Message));
        }
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
