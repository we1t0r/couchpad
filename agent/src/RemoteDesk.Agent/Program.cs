using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent;

static class Program
{
    /// <summary>
    /// Аргументы:
    ///   --pair            сразу открыть окно с QR-кодом;
    ///   --data-dir путь   своя папка данных (по умолчанию %APPDATA%\RemoteDesk);
    ///   --port N          свой порт (по умолчанию 47800);
    ///   --loopback        слушать только 127.0.0.1 и не объявляться в сети (для разработки);
    ///   --dev-link файл   записать ссылку сопряжения в файл (для автотестов приложения);
    ///   --input-log файл  не трогать мышь и клавиатуру, а записывать ввод в файл (для автотестов).
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        var options = ParseArgs(args);

        // Один агент на пользователя: второй экземпляр просто выходит.
        using var mutex = new Mutex(true, @"Local\RemoteDesk.Agent." + options.DataDir.GetHashCode(), out var first);
        if (!first)
            return 0;

        ApplicationConfiguration.Initialize();

        AgentHost host;
        try
        {
            host = new AgentHost(options);
            // StartAsync на пуле потоков, чтобы не зависнуть на контексте UI.
            Task.Run(() => host.StartAsync()).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
        {
            MessageBox.Show(
                $"Не удалось запустить RemoteDesk: порт {options.Port} занят другой программой.\n\n"
                + "Закройте её или укажите другой порт в настройках.\n\nПодробности: " + e.Message,
                "RemoteDesk", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // Для автотестов клиента: записать ссылку сопряжения в файл (в режиме --loopback — с адресом 127.0.0.1).
        var linkIndex = Array.IndexOf(args, "--dev-link");
        if (linkIndex >= 0 && linkIndex + 1 < args.Length)
            File.WriteAllText(args[linkIndex + 1], host.PairingLink(options.Loopback ? ["127.0.0.1"] : null));

        var showPairing = args.Contains("--pair") || host.Devices.All.Count == 0;
        Application.Run(new TrayContext(host, showPairing));

        Task.Run(() => host.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
        return 0;
    }

    static AgentOptions ParseArgs(string[] args)
    {
        var dataDir = AgentOptions.DefaultDataDir;
        var port = Protocol.DefaultPort;
        var loopback = false;
        IInputSink? input = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input-log" when i + 1 < args.Length:
                    input = new RecordingInput(args[++i]);
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    dataDir = args[++i];
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p):
                    port = p;
                    i++;
                    break;
                case "--loopback":
                    loopback = true;
                    break;
            }
        }
        return new AgentOptions(dataDir, port, Mdns: !loopback, Loopback: loopback, Input: input);
    }
}
