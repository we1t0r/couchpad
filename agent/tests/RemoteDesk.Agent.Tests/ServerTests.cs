using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "rdesk-test-" + Guid.NewGuid().ToString("N"));
    readonly RecordingInput input = new();
    readonly FakeWol wol = new();
    AgentHost host = null!;

    AgentOptions Options() => new(dir, Port: 0, Mdns: false, Loopback: true, Input: input, Wol: wol);

    sealed class FakeWol : IWakeOnLanSetup
    {
        public WolReport Check() => new(
            [new WolCheck("cable", WolState.Ok, "Кабель", null), new WolCheck("magic", WolState.Problem, "Пакет", "Исправить")],
            CanFix: true);

        public Task<bool> FixAsync() => Task.FromResult(true);
    }

    public async Task InitializeAsync()
    {
        host = new AgentHost(Options());
        await host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await host.DisposeAsync();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    async Task<ClientWebSocket> ConnectAsync(string? pinnedFingerprint = null)
    {
        var fp = pinnedFingerprint ?? host.Identity.Fingerprint;
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is not null && AgentIdentity.FingerprintOf(cert.GetRawCertData()) == fp;
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{host.Server.Port}/ws"), CancellationToken.None);
        return ws;
    }

    static async Task SendAsync(ClientWebSocket ws, string text) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    static async Task<JsonObject> ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
        return JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))!.AsObject();
    }

    async Task<string> PairAsync(string deviceId = "phone-1")
    {
        using var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("pair", ("secret", host.Secrets.Current.Key), ("deviceId", deviceId),
            ("deviceName", "Тестовый телефон"), ("platform", "android")));
        var reply = await ReceiveAsync(ws);
        Assert.Equal("paired", reply!.Str("t"));
        return reply.Str("token")!;
    }

    [Fact]
    public async Task Pairing_gives_token_and_agent_info()
    {
        var keyBefore = host.Secrets.Current.Key;
        using var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("pair", ("secret", keyBefore), ("deviceId", "phone-1"),
            ("deviceName", "Тестовый телефон"), ("platform", "android")));

        var reply = await ReceiveAsync(ws);

        Assert.Equal("paired", reply!.Str("t"));
        Assert.False(string.IsNullOrEmpty(reply.Str("token")));
        Assert.Equal(host.Identity.Id, reply["agent"]!["id"]!.GetValue<string>());
        Assert.Equal(Protocol.Version, reply["agent"]!["protocol"]!.GetValue<int>());
        Assert.Single(host.Devices.All);
        Assert.NotEqual(keyBefore, host.Secrets.Current.Key);
    }

    [Fact]
    public async Task Secret_is_single_use()
    {
        var key = host.Secrets.Current.Key;
        await PairAsync();

        using var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("pair", ("secret", key), ("deviceId", "phone-2")));
        var reply = await ReceiveAsync(ws);

        Assert.Equal("error", reply!.Str("t"));
        Assert.Equal(Protocol.Errors.BadSecret, reply.Str("code"));
        Assert.Single(host.Devices.All);
    }

    [Fact]
    public async Task Hello_with_token_then_ping()
    {
        var token = await PairAsync();

        using var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("hello", ("deviceId", "phone-1"), ("token", token)));
        var welcome = await ReceiveAsync(ws);
        Assert.Equal("welcome", welcome!.Str("t"));
        Assert.Contains("input", welcome["permissions"]!.AsArray().Select(p => p!.GetValue<string>()));

        await SendAsync(ws, Message.Build("ping", ("n", 42)));
        var pong = await ReceiveAsync(ws);
        Assert.Equal("pong", pong!.Str("t"));
        Assert.Equal(42, pong.Num("n"));
    }

    [Fact]
    public async Task Hello_with_wrong_token_is_rejected()
    {
        await PairAsync();

        using var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("hello", ("deviceId", "phone-1"), ("token", "wrong")));
        var reply = await ReceiveAsync(ws);

        Assert.Equal(Protocol.Errors.Revoked, reply!.Str("code"));
    }

    [Fact]
    public async Task Messages_before_auth_are_rejected()
    {
        using var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("ping", ("n", 1)));
        var reply = await ReceiveAsync(ws);

        Assert.Equal(Protocol.Errors.NotAuthenticated, reply!.Str("code"));
    }

    [Fact]
    public async Task Revoke_disconnects_and_forgets_device()
    {
        var token = await PairAsync();
        using var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("hello", ("deviceId", "phone-1"), ("token", token)));
        await ReceiveAsync(ws);

        await host.RevokeAsync("phone-1");

        var reply = await ReceiveAsync(ws);
        Assert.Equal(Protocol.Errors.Revoked, reply!.Str("code"));
        Assert.Empty(host.Devices.All);
    }

    [Fact]
    public async Task Client_with_wrong_pin_cannot_connect()
    {
        var wrong = Convert.ToBase64String(SHA256.HashData([1, 2, 3]));
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(wrong));
    }

    [Fact]
    public async Task Unknown_message_type_keeps_connection()
    {
        var token = await PairAsync();
        using var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("hello", ("deviceId", "phone-1"), ("token", token)));
        await ReceiveAsync(ws);

        await SendAsync(ws, Message.Build("from_the_future", ("x", 1)));
        Assert.Equal(Protocol.Errors.BadMessage, (await ReceiveAsync(ws))!.Str("code"));

        await SendAsync(ws, Message.Build("ping", ("n", 7)));
        Assert.Equal("pong", (await ReceiveAsync(ws))!.Str("t"));
    }

    [Fact]
    public async Task Input_is_rejected_before_auth_and_released_on_disconnect()
    {
        using (var anonymous = await ConnectAsync())
        {
            await SendAsync(anonymous, Message.Build("move", ("dx", 10), ("dy", 10)));
            Assert.Equal(Protocol.Errors.NotAuthenticated, (await ReceiveAsync(anonymous)).Str("code"));
        }
        Assert.Empty(input.Lines);

        var token = await PairAsync();
        var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("hello", ("deviceId", "phone-1"), ("token", token)));
        await ReceiveAsync(ws);

        await SendAsync(ws, Message.Build("key", ("k", "ctrl"), ("down", true)));
        await SendAsync(ws, Message.Build("button", ("b", "left"), ("down", true)));
        await SendAsync(ws, Message.Build("key", ("k", "nope"), ("down", true)));
        Assert.Equal(Protocol.Errors.BadMessage, (await ReceiveAsync(ws)).Str("code"));

        // Телефон «пропал», не отпустив Ctrl и ЛКМ.
        ws.Abort();
        ws.Dispose();
        await WaitUntil(() => input.Lines.Count >= 4);

        Assert.Equal(["key 0x11 down", "button left down", "button left up", "key 0x11 up"], input.Lines);
    }

    [Fact]
    public async Task Power_timer_is_shared_with_all_phones_and_can_be_cancelled()
    {
        var token = await PairAsync();
        using var a = await ConnectAsync();
        await SendAsync(a, Message.Build("hello", ("deviceId", "phone-1"), ("token", token)));
        await ReceiveAsync(a);

        await SendAsync(a, Message.Build("power", ("a", "shutdown"), ("in", 600), ("force", true)));
        var timer = await ReceiveAsync(a);
        Assert.Equal("power_timer", timer.Str("t"));
        Assert.Equal("shutdown", timer.Str("a"));
        Assert.Equal(true, timer.Bool("force"));
        Assert.InRange(timer.Num("at")!.Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 590_000, 600_000);

        // Второй вход того же телефона сразу узнаёт о таймере.
        using var b = await ConnectAsync();
        await SendAsync(b, Message.Build("hello", ("deviceId", "phone-1"), ("token", token)));
        await ReceiveAsync(b);
        Assert.Equal("shutdown", (await ReceiveAsync(b)).Str("a"));

        await SendAsync(b, Message.Build("power_cancel"));
        Assert.Null((await ReceiveAsync(a)).Str("a"));
        Assert.Null((await ReceiveAsync(b)).Str("a"));

        await SendAsync(a, Message.Build("power", ("a", "lock")));
        await SendAsync(a, Message.Build("power", ("a", "explode")));
        Assert.Equal(Protocol.Errors.BadMessage, (await ReceiveAsync(a)).Str("code"));
        Assert.Equal(["power lock"], input.Lines);
    }

    [Fact]
    public async Task Wol_check_reports_state_and_setup_opens_wizard()
    {
        var token = await PairAsync();
        using var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("hello", ("deviceId", "phone-1"), ("token", token)));
        await ReceiveAsync(ws);
        var opened = 0;
        host.Server.WolSetupRequested += () => opened++;

        await SendAsync(ws, Message.Build("wol_check"));
        var status = await ReceiveAsync(ws);
        Assert.Equal("wol_status", status.Str("t"));
        Assert.Equal(false, status.Bool("ready"));
        Assert.Equal(true, status.Bool("canFix"));
        var checks = status["checks"]!.AsArray();
        Assert.Equal("problem", checks[1]!["state"]!.GetValue<string>());
        Assert.Null(checks[0]!["hint"]);
        Assert.Equal(0, opened);

        await SendAsync(ws, Message.Build("wol_setup"));
        Assert.Equal("wol_status", (await ReceiveAsync(ws)).Str("t"));
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task Clipboard_get_and_set()
    {
        var token = await PairAsync();
        using var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("hello", ("deviceId", "phone-1"), ("token", token)));
        await ReceiveAsync(ws);

        await SendAsync(ws, Message.Build("clip_set", ("text", "с телефона")));
        Assert.Equal("clip_done", (await ReceiveAsync(ws)).Str("t"));
        await SendAsync(ws, Message.Build("clip_get"));
        Assert.Equal("с телефона", (await ReceiveAsync(ws)).Str("text"));
    }

    [Fact]
    public async Task Phone_folders_requests_and_offers_reach_the_phone()
    {
        var token = await PairAsync();
        await WaitUntil(() => host.Server.ConnectedDeviceIds.Count == 0);

        // Файл, отправленный, пока телефон не подключён, уходит ему сразу после входа.
        var file = Path.Combine(dir, "отчёт.txt");
        File.WriteAllText(file, "с ПК");
        Assert.Equal((1, 0), await host.SendToPhoneAsync([file]));

        using var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("hello", ("deviceId", "phone-1"), ("token", token)));
        Assert.Equal("welcome", (await ReceiveAsync(ws)).Str("t"));
        var offer = await ReceiveAsync(ws);
        Assert.Equal("offer", offer.Str("t"));
        Assert.Equal("отчёт.txt", offer["files"]![0]!.AsObject().Str("name"));

        // ПК спрашивает папки телефона — телефон (тут — тест) отвечает.
        var roots = host.PhoneFiles.RootsAsync("phone-1");
        var ask = await ReceiveAsync(ws);
        Assert.Equal(("pf", "roots"), (ask.Str("t"), ask.Str("op")));
        await SendAsync(ws, Message.Build("pf_r", ("id", ask.Num("id")), ("roots", new[] { new { id = "p-1", name = "Камера", writable = false } })));
        Assert.Equal([new PhoneRoot("p-1", "Камера", false)], await roots);

        // Ошибка телефона доходит до окна на ПК как есть.
        var mkdir = host.PhoneFiles.MkdirAsync("phone-1", "p-1", "Новая");
        ask = await ReceiveAsync(ws);
        await SendAsync(ws, Message.Build("pf_r", ("id", ask.Num("id")), ("error", "Только для чтения")));
        Assert.Equal("Только для чтения", (await Assert.ThrowsAsync<PhoneFilesException>(() => mkdir)).Message);

        // Скачать с телефона: ПК заводит папку для приёма, телефон получает её имя в «target».
        var into = Path.Combine(dir, "из телефона");
        Directory.CreateDirectory(into);
        var download = host.PhoneFiles.DownloadAsync("phone-1", "p-1", "DCIM/фото.jpg", into);
        ask = await ReceiveAsync(ws);
        Assert.Equal(("push", "DCIM/фото.jpg"), (ask.Str("op"), ask.Str("path")));
        Assert.StartsWith("pull-", ask.Str("target"));
        Assert.Equal(Path.GetFullPath(into), host.Files.Resolve(ask.Str("target"), "", out _));
        await SendAsync(ws, Message.Build("pf_r", ("id", ask.Num("id")), ("ok", true)));
        await download;
    }

    [Fact]
    public async Task Screen_streams_frames_and_taps_land_on_the_monitor()
    {
        var token = await PairAsync();
        using var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("hello", ("deviceId", "phone-1"), ("token", token)));
        await ReceiveAsync(ws);

        string? watching = null;
        host.Server.ScreenWatching += (phone, on) => watching = on ? phone : null;
        await SendAsync(ws, Message.Build("screen_start", ("monitor", "test-2"), ("maxWidth", 800)));
        var info = await ReceiveAsync(ws);
        Assert.Equal(("screen_info", "test-2"), (info.Str("t"), info.Str("monitor")));
        Assert.Equal(2, info["monitors"]!.AsArray().Count);

        // Первый кадр — бинарный: тип 1, номер 1, размер 16×9, дальше JPEG.
        var buffer = new byte[64 * 1024];
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var frame = await ws.ReceiveAsync(buffer, cts.Token);
            Assert.Equal(WebSocketMessageType.Binary, frame.MessageType);
            Assert.Equal(1, buffer[0]);
            Assert.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(1)));
            Assert.Equal((16, 9), (System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(5)),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(7))));
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, buffer[9..11]);
        }
        Assert.Equal("Тестовый телефон", watching);

        // Касание в середине второго монитора (он правее первого на 1600 пикселей).
        await SendAsync(ws, Message.Build("pointer", ("x", 0.5), ("y", 0.5)));
        await SendAsync(ws, Message.Build("click", ("b", "left")));
        await WaitUntil(() => input.Lines.Contains("button left up"));
        Assert.Contains("moveto 2400 450", input.Lines);

        await SendAsync(ws, Message.Build("screen_stop"));
        await WaitUntil(() => watching is null);
        Assert.Null(watching);
    }

    [Theory]
    [InlineData("192.168.0.102", "255.255.255.0", "192.168.0.255")]
    [InlineData("10.1.2.3", "255.255.0.0", "10.1.255.255")]
    public void Subnet_broadcast(string ip, string mask, string expected) =>
        Assert.Equal(expected, NetworkInfo.Broadcast(System.Net.IPAddress.Parse(ip), System.Net.IPAddress.Parse(mask)));

    [Theory]
    [InlineData("192.168.0.5", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::ffff:192.168.1.2", true)]
    [InlineData("fe80::1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("203.0.113.5", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("2a00:1450:4010::1", false)]
    public void Local_or_internet_address(string ip, bool local) =>
        Assert.Equal(local, NetworkInfo.IsLocal(System.Net.IPAddress.Parse(ip)));

    static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
    }

    [Fact]
    public void Pairing_link_contains_everything_for_qr()
    {
        var link = new Uri(host.PairingLink(["192.168.0.10"]));
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);

        Assert.Equal("rdesk", link.Scheme);
        Assert.Equal(host.Identity.Id, query["id"]);
        Assert.Equal("192.168.0.10", query["h"]);
        Assert.Equal(host.Server.Port.ToString(), query["p"]);
        Assert.Equal(host.Identity.Fingerprint, query["fp"]);
        Assert.Equal(host.Secrets.Current.Key, query["k"]);
    }

    [Fact]
    public async Task Devices_and_identity_survive_restart()
    {
        await PairAsync();
        var id = host.Identity.Id;
        var fp = host.Identity.Fingerprint;
        await host.DisposeAsync();

        host = new AgentHost(Options());
        await host.StartAsync();

        Assert.Equal(id, host.Identity.Id);
        Assert.Equal(fp, host.Identity.Fingerprint);
        Assert.Single(host.Devices.All);
    }

    [Fact]
    public async Task Sign_in_helper_lets_paired_phones_see_the_screen_but_not_files_or_pairing()
    {
        var token = await PairAsync();
        var key = host.Secrets.Current.Key;
        await host.DisposeAsync();

        // Помощник службы входа: та же папка данных, тот же сертификат — телефон его узнаёт.
        host = new AgentHost(Options() with { SignIn = true });
        await host.StartAsync();

        using (var pairing = await ConnectAsync())
        {
            await SendAsync(pairing, Message.Build("pair", ("secret", key), ("deviceId", "phone-2"), ("deviceName", "Чужой"), ("platform", "android")));
            var refused = await ReceiveAsync(pairing);
            Assert.Equal(Protocol.Errors.Forbidden, refused.Str("code"));
        }

        using var ws = await ConnectAsync();
        await SendAsync(ws, Message.Build("hello", ("deviceId", "phone-1"), ("token", token)));
        var welcome = await ReceiveAsync(ws);
        Assert.Equal("welcome", welcome.Str("t"));
        Assert.True(welcome.Bool("signin"));
        Assert.Equal(AgentOptions.SignInPermissions, welcome["permissions"]!.AsArray().Select(p => p!.GetValue<string>()));

        await SendAsync(ws, Message.Build("clip_get"));
        Assert.Equal(Protocol.Errors.Forbidden, (await ReceiveAsync(ws)).Str("code"));

        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{host.Server.Port}/api/files/roots");
        request.Headers.Add("X-RDesk-Device", "phone-1");
        request.Headers.Add("X-RDesk-Token", token);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(request)).StatusCode);

        await SendAsync(ws, Message.Build("key", ("k", "a"), ("down", true)));
        await SendAsync(ws, Message.Build("ping", ("n", 1)));
        Assert.Equal("pong", (await ReceiveAsync(ws)).Str("t"));
    }
}
