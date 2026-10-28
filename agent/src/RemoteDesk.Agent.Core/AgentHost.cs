namespace RemoteDesk.Agent.Core;

public sealed record AgentOptions(string DataDir, int Port = Protocol.DefaultPort, bool Mdns = true)
{
    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDesk");
}

/// <summary>Собирает агента целиком: личность, список телефонов, сервер, объявление в сети.</summary>
public sealed class AgentHost(AgentOptions options) : IAsyncDisposable
{
    public AgentIdentity Identity { get; } = AgentIdentity.LoadOrCreate(options.DataDir);
    public PairingStore Devices { get; } = new(options.DataDir);
    public PairingSecrets Secrets { get; } = new();
    public AgentServer Server { get; private set; } = null!;
    MdnsAdvertiser? mdns;

    public async Task StartAsync(CancellationToken ct = default)
    {
        Server = new AgentServer(Identity, Devices, Secrets, Info);
        await Server.StartAsync(options.Port, ct);
        if (options.Mdns)
            mdns = new MdnsAdvertiser(Identity, Server.Port, NetworkInfo.Addresses());
    }

    public AgentInfo Info() => new(
        Identity.Id,
        Identity.Name,
        NetworkInfo.OsDescription(),
        Protocol.AgentVersion,
        Protocol.Version,
        NetworkInfo.Interfaces());

    /// <summary>Ссылка для QR-кода сопряжения (см. protocol/PROTOCOL.md).</summary>
    public string PairingLink(IReadOnlyList<string>? addresses = null)
    {
        var hosts = addresses ?? NetworkInfo.Addresses();
        var key = Secrets.Current.Key;
        return $"rdesk://pair?v={Protocol.Version}&id={Identity.Id}&n={Uri.EscapeDataString(Identity.Name)}"
               + $"&h={string.Join(',', hosts)}&p={Server.Port}&fp={Identity.Fingerprint}&k={key}";
    }

    /// <summary>Отзывает доступ телефона и сразу отключает его.</summary>
    public async Task RevokeAsync(string deviceId)
    {
        Devices.Remove(deviceId);
        await Server.DisconnectAsync(deviceId, Protocol.Errors.Revoked, "Доступ отозван на компьютере");
    }

    public async ValueTask DisposeAsync()
    {
        mdns?.Dispose();
        if (Server is not null)
            await Server.DisposeAsync();
    }
}
