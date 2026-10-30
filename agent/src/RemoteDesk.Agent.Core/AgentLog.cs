namespace RemoteDesk.Agent.Core;

/// <summary>Короткий журнал агента (agent.log в папке данных, до 1 МБ): почему не вышло видео и т. п.</summary>
public static class AgentLog
{
    static readonly object Gate = new();

    /// <summary>Куда писать; null — никуда.</summary>
    public static string? Path { get; set; }

    public static void Write(string line)
    {
        var path = Path;
        if (path is null)
            return;
        lock (Gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
