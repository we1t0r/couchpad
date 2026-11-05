using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Couchpad.Agent;

/// <summary>
/// «Отправить на телефон» в контекстном меню Проводника. Explorer запускает <c>Couchpad.exe --send "файл"</c>;
/// эта копия передаёт путь уже запущенному агенту через именованный канал (только для текущего пользователя) и выходит.
/// </summary>
static class SendToPhone
{
    const string VerbKey = @"Software\Classes\*\shell\Couchpad.SendToPhone";

    /// <summary>Устойчивое имя для папки данных: string.GetHashCode в .NET меняется от запуска к запуску.</summary>
    public static string InstanceId(string dataDir) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant())))[..12];

    static string PipeName(string dataDir) => "Couchpad.Agent." + InstanceId(dataDir);

    /// <summary>Мьютекс «агент этого пользователя запущен» — по нему же служба входа уступает агенту порт.</summary>
    public static string AgentMutex(string dataDir) => @"Local\Couchpad.Agent." + InstanceId(dataDir);

    /// <summary>Пункт меню для всех файлов. В Windows 11 он в «Показать дополнительные параметры».</summary>
    public static void Register()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(VerbKey);
            key.SetValue("MUIVerb", "Отправить на телефон (Couchpad)");
            key.SetValue("Icon", $"\"{Environment.ProcessPath}\"");
            using var command = key.CreateSubKey("command");
            command.SetValue("", $"\"{Environment.ProcessPath}\" --send \"%1\"");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Без пункта меню агент работает как обычно.
        }
    }

    /// <summary>Удаление Couchpad: убрать пункт из меню Проводника.</summary>
    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(VerbKey, throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }
    }

    /// <summary>Передать пути запущенному агенту. false — агент не ответил.</summary>
    public static bool TrySend(string dataDir, IReadOnlyList<string> paths)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(dataDir), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', paths));
            pipe.Write(bytes);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Принимать пути от других копий, пока не отменят.</summary>
    public static async Task ListenAsync(string dataDir, Action<string[]> onFiles, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName(dataDir), PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(ct);
                var paths = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (paths.Length > 0)
                    onFiles(paths);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Отправитель оборвал канал — ждём следующего.
            }
        }
    }
}
