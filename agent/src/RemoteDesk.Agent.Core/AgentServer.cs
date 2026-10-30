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
/// <param name="SignIn">Агент помощника службы на экране входа: без файлов и буфера обмена, без сопряжения.</param>
public sealed record AgentServices(
    IInputSink Input,
    PowerScheduler Power,
    IWakeOnLanSetup Wol,
    FileService Files,
    IClipboard Clipboard,
    Action<string> Recycle,
    Outbox Outbox,
    IScreenSource Screen,
    IReadOnlyList<string>? Permissions = null,
    bool SignIn = false,
    IAudioSource? Audio = null);

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

    /// <summary>Что разрешено телефонам на этом агенте.</summary>
    internal IReadOnlyList<string> Permissions => services.Permissions ?? Protocol.DefaultPermissions;

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
        var session = new Session(this, ws, ctx.Connection.RemoteIpAddress);
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

    /// <summary>Телефон начал (true) или перестал (false) смотреть экран: имя телефона. Человек за ПК должен об этом знать.</summary>
    public event Action<string, bool>? ScreenWatching;

    internal void OnScreenWatching(Session session, bool watching)
    {
        var name = Devices.All.FirstOrDefault(d => d.DeviceId == session.DeviceId)?.Name ?? "Телефон";
        ScreenWatching?.Invoke(name, watching);
    }

    long nextPhoneRequest;
    readonly ConcurrentDictionary<long, TaskCompletionSource<System.Text.Json.Nodes.JsonObject>> phoneRequests = new();

    /// <summary>Запрос к папкам телефона ({"t":"pf"}); ответ — поля pf_r. Ошибку телефона бросает как <see cref="PhoneFilesException"/>.</summary>
    internal async Task<System.Text.Json.Nodes.JsonObject> AskPhoneAsync(string deviceId, string op, params (string Key, object? Value)[] fields)
    {
        var session = sessions.Keys.FirstOrDefault(s => s.DeviceId == deviceId)
            ?? throw new PhoneFilesException("Телефон отключился. Откройте этот компьютер в Couchpad на телефоне");
        var id = Interlocked.Increment(ref nextPhoneRequest);
        var reply = new TaskCompletionSource<System.Text.Json.Nodes.JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        phoneRequests[id] = reply;
        try
        {
            await session.SendAsync(Message.Build("pf", [("id", id), ("op", op), .. fields]));
            var msg = await reply.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (msg.Str("error") is { } error)
                throw new PhoneFilesException(error);
            return msg;
        }
        catch (TimeoutException)
        {
            throw new PhoneFilesException("Телефон не ответил. Проверьте, что Couchpad на нём открыт и подключён к этому компьютеру");
        }
        finally
        {
            phoneRequests.TryRemove(id, out _);
        }
    }

    internal void OnPhoneReply(System.Text.Json.Nodes.JsonObject msg)
    {
        if (msg.Num("id") is { } id && phoneRequests.TryGetValue(id, out var reply))
            reply.TrySetResult(msg);
    }

    readonly List<OutboxItem> undelivered = [];

    internal static string OfferMessage(IEnumerable<OutboxItem> items) =>
        Message.Build("offer", ("files", items.Select(i => new { id = i.Id, name = i.Name, size = i.Size }).ToList()));

    /// <summary>
    /// Предложить файлы подключённым телефонам — они сразу начнут скачивать.
    /// Если ни один не подключён, файлы уйдут первому, кто подключится (в течение часа).
    /// </summary>
    /// <returns>Сколько телефонов получило предложение сейчас.</returns>
    public async Task<int> OfferAsync(IReadOnlyList<OutboxItem> items)
    {
        var phones = sessions.Keys.Where(s => s.DeviceId is not null).ToList();
        if (phones.Count == 0)
        {
            lock (undelivered)
                undelivered.AddRange(items);
            return 0;
        }
        var text = OfferMessage(items);
        foreach (var session in phones)
            await session.SendAsync(text);
        return phones.Count;
    }

    internal void OnAuthenticated(Session session, PairedDevice device, bool justPaired)
    {
        List<OutboxItem> waiting;
        lock (undelivered)
        {
            waiting = [.. undelivered];
            undelivered.Clear();
        }
        if (waiting.Count > 0)
            _ = session.SendAsync(OfferMessage(waiting));
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
internal sealed class Session(AgentServer server, WebSocket ws, IPAddress? remote)
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
            StopScreen();
            // Колонки ПК, заглушённые на время звука, включаются обратно.
            StopAudio();
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
            case "pf_r":
                server.OnPhoneReply(msg);
                break;
            case "screen_start":
                if (!permissions.Contains("screen"))
                {
                    await SendAsync(Message.Error(Protocol.Errors.Forbidden, "Показ экрана выключен на компьютере"));
                    break;
                }
                await StartScreenAsync(msg);
                break;
            case "screen_stop":
                StopScreen();
                break;
            case "screen_ack":
                screen?.Ack();
                break;
            case "screen_key":
                screen?.RequestKeyFrame();
                break;
            case "audio_start":
                if (!permissions.Contains("screen"))
                {
                    await SendAsync(Message.Error(Protocol.Errors.Forbidden, "Показ экрана и звук выключены на компьютере"));
                    break;
                }
                await StartAudioAsync(msg);
                break;
            case "audio_stop":
                StopAudio();
                break;
            case "pointer":
                if (!permissions.Contains("input"))
                {
                    await SendAsync(Message.Error(Protocol.Errors.Forbidden, "Управление мышью и клавиатурой выключено на компьютере"));
                    break;
                }
                Pointer(msg);
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

        // На экране входа QR-кода нет и быть не должно: подключаются только уже сопряжённые телефоны.
        if (server.Services.SignIn)
        {
            await CloseWithErrorAsync(Protocol.Errors.Forbidden, "Компьютер на экране входа. Подключите телефон после входа в Windows");
            return;
        }

        // Через интернет (проброс порта на роутере) — только уже сопряжённые телефоны.
        if (!NetworkInfo.IsLocal(remote))
        {
            await CloseWithErrorAsync(Protocol.Errors.Forbidden, "Сопряжение — только в домашней сети. Подключите телефон к Wi-Fi рядом с компьютером");
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
        permissions = [.. server.Permissions];
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
        permissions = [.. server.Permissions];
        // signin — это помощник службы на экране входа: после входа в Windows его сменит обычный агент.
        await SendAsync(Message.Build("welcome", ("agent", server.Info()), ("permissions", permissions),
            ("signin", server.Services.SignIn ? true : null)));
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

    ScreenStream? screen;

    async Task StartScreenAsync(System.Text.Json.Nodes.JsonObject msg)
    {
        StopScreen();
        var source = server.Services.Screen;
        var monitors = source.Monitors();
        if (monitors.Count == 0)
        {
            await SendAsync(Message.Error(Protocol.Errors.Forbidden, "Этот компьютер не умеет показывать экран"));
            return;
        }
        var wanted = msg.Str("monitor");
        var capture = source.Open(monitors.Any(m => m.Id == wanted) ? wanted : null);
        // Видео H.264 — если телефон умеет и экран умеет (на экране входа у помощника — только JPEG).
        var fps = (int)(msg.Num("fps") ?? 30);
        var video = msg.Str("codec") == "h264" && source is IVideoSource videoSource
            ? new VideoRequest(videoSource, fps, (int)(msg.Num("bitrate") ?? 8_000_000))
            : null;
        screen = new ScreenStream(capture, SendBinaryAsync,
            (int)(msg.Num("maxWidth") ?? 1600), (int)(msg.Num("quality") ?? 70), fps, video, SendAsync);
        await SendAsync(Message.Build("screen_info",
            ("monitor", capture.Monitor.Id),
            ("codec", video is null ? "jpeg" : "h264"),
            ("monitors", monitors.Select(m => new { id = m.Id, name = m.Name, width = m.Width, height = m.Height, primary = m.Primary }).ToList())));
        screen.Start();
        server.OnScreenWatching(this, true);
    }

    void StopScreen()
    {
        if (screen is null)
            return;
        screen.Dispose();
        screen = null;
        server.OnScreenWatching(this, false);
    }

    AudioStream? audio;

    async Task StartAudioAsync(System.Text.Json.Nodes.JsonObject msg)
    {
        StopAudio();
        try
        {
            audio = new AudioStream(server.Services.Audio ?? new NullAudio(), SendBinaryAsync,
                msg.Bool("mute") ?? false, (int)(msg.Num("bitrate") ?? 96_000));
        }
        catch (Exception e)
        {
            await SendAsync(Message.Error(Protocol.Errors.Forbidden, e.Message));
        }
    }

    void StopAudio()
    {
        audio?.Dispose();
        audio = null;
    }

    /// <summary>Касание на картинке экрана: x и y от 0 до 1 внутри показанного монитора.</summary>
    void Pointer(System.Text.Json.Nodes.JsonObject msg)
    {
        var monitor = screen?.Monitor
            ?? server.Services.Screen.Monitors().FirstOrDefault(m => m.Id == msg.Str("monitor"));
        if (monitor is null || msg.Dbl("x") is not { } x || msg.Dbl("y") is not { } y)
            return;
        var px = monitor.X + (int)Math.Round(Math.Clamp(x, 0, 1) * (monitor.Width - 1));
        var py = monitor.Y + (int)Math.Round(Math.Clamp(y, 0, 1) * (monitor.Height - 1));
        server.Input.MoveTo(px, py);
    }

    public async Task SendBinaryAsync(byte[] data)
    {
        if (ws.State != WebSocketState.Open)
            return;
        await sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            sendLock.Release();
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
