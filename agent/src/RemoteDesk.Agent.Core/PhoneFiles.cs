using System.Text.Json.Nodes;

namespace RemoteDesk.Agent.Core;

/// <summary>Ошибка от телефона или связи с ним — текст можно показать человеку как есть.</summary>
public sealed class PhoneFilesException(string message) : Exception(message);

/// <summary>Папка телефона, которую человек открыл компьютеру в приложении.</summary>
public sealed record PhoneRoot(string Id, string Name, bool Writable);

/// <summary>
/// Файлы телефона для окна «Файлы телефона» на ПК (см. «Папки телефона» в PROTOCOL.md).
/// Список и действия — запросами через WebSocket; сами файлы телефон передаёт обычной загрузкой по HTTPS.
/// </summary>
public sealed class PhoneFiles(AgentServer server, FileService files, Outbox outbox)
{
    /// <summary>Подключённые сейчас телефоны: id и имя.</summary>
    public IReadOnlyList<PairedDevice> Connected() =>
        server.Devices.All.Where(d => server.ConnectedDeviceIds.Contains(d.DeviceId)).ToList();

    public async Task<IReadOnlyList<PhoneRoot>> RootsAsync(string deviceId)
    {
        var reply = await server.AskPhoneAsync(deviceId, "roots");
        return (reply["roots"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(r => new PhoneRoot(r.Str("id") ?? "", r.Str("name") ?? "", r.Bool("writable") ?? false))
            .Where(r => r.Id.Length > 0)
            .ToList();
    }

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string deviceId, string root, string path)
    {
        var reply = await server.AskPhoneAsync(deviceId, "list", ("root", root), ("path", path));
        return (reply["entries"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(e => new FileEntry(e.Str("name") ?? "", e.Bool("dir") ?? false, e.Num("size") ?? 0,
                DateTimeOffset.FromUnixTimeMilliseconds(e.Num("modified") ?? 0)))
            .Where(e => e.Name.Length > 0)
            .ToList();
    }

    public Task MkdirAsync(string deviceId, string root, string path) => server.AskPhoneAsync(deviceId, "mkdir", ("root", root), ("path", path));

    public Task RenameAsync(string deviceId, string root, string path, string name) =>
        server.AskPhoneAsync(deviceId, "rename", ("root", root), ("path", path), ("name", name));

    public Task DeleteAsync(string deviceId, string root, string path) => server.AskPhoneAsync(deviceId, "delete", ("root", root), ("path", path));

    /// <summary>Скачать файл телефона в папку ПК. Готово — когда сработает <see cref="FileService.Pulled"/>.</summary>
    public Task DownloadAsync(string deviceId, string root, string path, string toFolder) =>
        server.AskPhoneAsync(deviceId, "push", ("root", root), ("path", path), ("target", files.AddPullTarget(toFolder)));

    /// <summary>Положить файл ПК в папку телефона: телефон сам скачает его (прогресс — на телефоне).</summary>
    public async Task UploadAsync(string deviceId, string root, string folder, string localFile)
    {
        var item = outbox.Add([localFile]).FirstOrDefault() ?? throw new PhoneFilesException("Можно отправить только файл, не папку");
        await server.AskPhoneAsync(deviceId, "pull", ("root", root), ("path", folder), ("file", item.Id), ("name", item.Name), ("size", item.Size));
    }
}
