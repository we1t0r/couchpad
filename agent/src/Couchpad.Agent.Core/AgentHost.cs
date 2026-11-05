namespace Couchpad.Agent.Core;

/// <param name="Loopback">Слушать только 127.0.0.1 — для разработки и тестов (без окна брандмауэра).</param>
/// <param name="Input">Куда отправлять ввод; по умолчанию — настоящие мышь и клавиатура Windows.</param>
/// <param name="SignIn">Помощник службы на экране входа Windows (см. <see cref="AgentServices.SignIn"/>).</param>
/// <param name="Power">Питание и мониторы; по умолчанию — Windows. Если <paramref name="Input"/> — запись
/// (<see cref="RecordingInput"/>), то и питание пишется туда же: тесты никогда не выключат ПК.</param>
public sealed record AgentOptions(
    string DataDir,
    int Port = Protocol.DefaultPort,
    bool Mdns = true,
    bool Loopback = false,
    IInputSink? Input = null,
    IPowerSink? Power = null,
    IWakeOnLanSetup? Wol = null,
    IClipboard? Clipboard = null,
    IScreenSource? Screen = null,
    bool SignIn = false,
    IAudioSource? Audio = null,
    string? Name = null)
{
    /// <summary>Что можно телефону с экрана входа Windows: файлы и буфер обмена — только после входа.</summary>
    public static readonly string[] SignInPermissions = ["input", "power", "screen"];

    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Couchpad");
}

/// <summary>Собирает агента целиком: личность, список телефонов, сервер, объявление в сети.</summary>
public sealed class AgentHost(AgentOptions options) : IAsyncDisposable
{
    public string DataDir => options.DataDir;
    public AgentIdentity Identity { get; } = AgentIdentity.LoadOrCreate(options.DataDir, options.Name);
    public PairingStore Devices { get; } = new(options.DataDir);
    public PairingSecrets Secrets { get; } = new();
    public AgentServer Server { get; private set; } = null!;
    readonly IPowerSink powerSink = options.Power
        ?? options.Input as IPowerSink
        ?? (OperatingSystem.IsWindows() ? new WindowsPower() : new NullPower());
    PowerScheduler? power;

    /// <summary>Проверка и настройка включения по сети.</summary>
    public IWakeOnLanSetup Wol { get; } = options.Wol ?? (OperatingSystem.IsWindows() ? new WindowsWakeOnLan() : new NullWakeOnLan());
    public PowerScheduler Power => power ??= new PowerScheduler(powerSink);

    /// <summary>Какие папки ПК видит телефон.</summary>
    public ShareStore Shares { get; } = new(options.DataDir);
    FileService? files;
    public FileService Files => files ??= new FileService(Shares);
    PhoneFiles? phoneFiles;

    /// <summary>Папки телефона для окна «Файлы телефона».</summary>
    public PhoneFiles PhoneFiles => phoneFiles ??= new PhoneFiles(Server, Files, Outbox);

    /// <summary>Файлы, отправленные на телефон из Проводника.</summary>
    public Outbox Outbox { get; } = new();
    MdnsAdvertiser? mdns;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var input = options.Input ?? (OperatingSystem.IsWindows() ? new WindowsInput() : new NullInput());
        // Корзина есть только в настоящей Windows; при записи ввода в файл (тесты) удаляем насовсем внутри тестовой папки.
        Action<string> recycle = OperatingSystem.IsWindows() && options.Input is not RecordingInput
            ? RecycleBin.Send
            : path =>
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else File.Delete(path);
            };
        var services = new AgentServices(input, Power, Wol, Files, options.Clipboard ?? options.Input as IClipboard ?? new NullClipboard(), recycle, Outbox,
            // При записи ввода (тесты) — ненастоящий экран: автотест не должен снимать экран разработчика.
            options.Screen ?? (options.Input is RecordingInput ? new SyntheticScreen() : new NullScreen()),
            options.SignIn ? AgentOptions.SignInPermissions : null, options.SignIn,
            options.Audio ?? (options.Input is RecordingInput ? new SyntheticAudio() : new NullAudio()));
        Server = new AgentServer(Identity, Devices, Secrets, Info, services);
        if (OperatingSystem.IsWindows() && powerSink is WindowsPower windows)
            windows.MonitorsProblem += msg =>
            {
                Power.Report(PowerAction.MonitorsOff, msg);
                _ = Server.NotifyAsync(Protocol.Errors.PowerFailed, msg);
            };
        await Server.StartAsync(options.Port, options.Loopback, ct);
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
        return $"couchpad://pair?v={Protocol.Version}&id={Identity.Id}&n={Uri.EscapeDataString(Identity.Name)}"
               + $"&h={string.Join(',', hosts)}&p={Server.Port}&fp={Identity.Fingerprint}&k={key}";
    }

    /// <summary>«Отправить на телефон»: сколько файлов принято и скольким телефонам ушло предложение сейчас.</summary>
    public async Task<(int Files, int Phones)> SendToPhoneAsync(IEnumerable<string> paths)
    {
        var items = Outbox.Add(paths);
        return items.Count == 0 ? (0, 0) : (items.Count, await Server.OfferAsync(items));
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
        power?.Dispose();
        (powerSink as IDisposable)?.Dispose();
    }
}
