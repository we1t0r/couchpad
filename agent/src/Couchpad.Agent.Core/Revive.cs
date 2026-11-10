namespace Couchpad.Agent.Core;

/// <summary>
/// Windows закрыла агента при выключении или перезагрузке, а потом отменила её (какая-то программа не завершилась):
/// сеанс пользователя жив, а агента нет — телефон не может подключиться, и автозапуск не поможет.
/// Агент оставляет отметку, когда его закрывает Windows; помощник службы входа по ней запускает агента снова.
/// </summary>
public static class Revive
{
    public const string FileName = "closed-by-windows";

    /// <summary>Раньше не пробуем: Windows может ещё ждать, пока закроются программы.</summary>
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(20);

    /// <summary>Позже отметка уже ни о чём не говорит.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    /// <param name="WrittenUtc">Когда Windows закрыла агента.</param>
    /// <param name="Action">Действие питания, которое незадолго до этого попросил телефон (null — попросил не он).</param>
    public sealed record Marker(DateTime WrittenUtc, PowerAction? Action);

    static string PathIn(string dataDir) => Path.Combine(dataDir, FileName);

    public static void Mark(string dataDir, PowerAction? action)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(PathIn(dataDir), action?.Name() ?? "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Не вышло — агента просто не запустят снова.
        }
    }

    public static Marker? Read(string dataDir)
    {
        try
        {
            var file = new FileInfo(PathIn(dataDir));
            return file.Exists ? new Marker(file.LastWriteTimeUtc, PowerActions.Parse(File.ReadAllText(file.FullName).Trim())) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Clear(string dataDir)
    {
        try
        {
            File.Delete(PathIn(dataDir));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Пора запускать агента снова: отметка сделана в этом же входе в Windows (после настоящей перезагрузки вход новый,
    /// и агента запустит автозапуск), с её появления прошло достаточно, но не слишком много.
    /// </summary>
    public static bool Due(DateTime markerUtc, DateTime? logonUtc, DateTime nowUtc) =>
        logonUtc is { } logon && markerUtc > logon && nowUtc - markerUtc >= MinAge && nowUtc - markerUtc <= MaxAge;

    /// <summary>Сообщение телефону: перезагрузка или выключение не состоялись.</summary>
    public static string AbortedMessage(PowerAction action) => Message.Build("error",
        ("code", Protocol.Errors.PowerAborted),
        ("msg", (action == PowerAction.Restart ? "Windows отменила перезагрузку" : action == PowerAction.SignOut ? "Windows отменила выход" : "Windows отменила выключение")
                + ": какая-то программа не закрылась. Остальные программы могли закрыться."),
        ("a", action.Name()));
}
