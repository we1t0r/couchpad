using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "rdesk-test-" + Guid.NewGuid().ToString("N"));
    AgentHost host = null!;

    public async Task InitializeAsync()
    {
        host = new AgentHost(new AgentOptions(dir, Port: 0, Mdns: false, Loopback: true));
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

        host = new AgentHost(new AgentOptions(dir, Port: 0, Mdns: false, Loopback: true));
        await host.StartAsync();

        Assert.Equal(id, host.Identity.Id);
        Assert.Equal(fp, host.Identity.Fingerprint);
        Assert.Single(host.Devices.All);
    }
}
