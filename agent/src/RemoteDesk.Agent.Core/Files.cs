using System.Diagnostics;
using System.Text.Json;

namespace RemoteDesk.Agent.Core;

/// <summary>Папка, открытая телефону. <see cref="Id"/> — короткий постоянный ключ для протокола.</summary>
public sealed record ShareRoot(string Id, string Name, string Path, bool Writable);

/// <summary>Ошибка доступа к файлу с кодом для телефона и понятным текстом.</summary>
public sealed class FileAccessException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Элемент списка папки.</summary>
public sealed record FileEntry(string Name, bool Dir, long Size, DateTimeOffset Modified);

/// <summary>
/// Что открыто телефону: выбранные папки (по умолчанию Рабочий стол, Загрузки, Документы, Изображения)
/// или весь компьютер (все диски). Хранится в shares.json в папке данных агента.
/// </summary>
public sealed class ShareStore
{
    sealed record Saved(List<ShareRoot> Folders, bool WholeComputer, bool WholeComputerWritable, string? Inbox);

    readonly string file;
    Saved saved;

    public event Action? Changed;

    public ShareStore(string dataDir)
    {
        file = System.IO.Path.Combine(dataDir, "shares.json");
        saved = Load() ?? new Saved(Defaults(), false, false, null);
    }

    public IReadOnlyList<ShareRoot> Folders => saved.Folders;
    public bool WholeComputer => saved.WholeComputer;
    public bool WholeComputerWritable => saved.WholeComputerWritable;

    /// <summary>Куда складывать файлы, присланные с телефона кнопкой «Поделиться». По умолчанию — «Загрузки».</summary>
    public string InboxPath => saved.Inbox ?? KnownFolder(Environment.SpecialFolder.UserProfile, "Downloads");

    /// <summary>Все корни, которые видит телефон: папки и (если включено) диски.</summary>
    public IReadOnlyList<ShareRoot> Roots()
    {
        var roots = new List<ShareRoot>(saved.Folders.Where(f => Directory.Exists(f.Path)));
        if (saved.WholeComputer)
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
            {
                var letter = drive.Name.TrimEnd('\\', ':');
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Локальный диск" : drive.VolumeLabel;
                roots.Add(new ShareRoot("drive-" + letter.ToLowerInvariant(), $"{label} ({letter}:)", drive.RootDirectory.FullName,
                    saved.WholeComputerWritable));
            }
        }
        return roots;
    }

    public void Update(IEnumerable<ShareRoot> folders, bool wholeComputer, bool wholeComputerWritable, string? inbox = null)
    {
        saved = new Saved(folders.ToList(), wholeComputer, wholeComputerWritable, inbox ?? saved.Inbox);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(saved, Message.Options));
        File.Move(tmp, file, overwrite: true);
        Changed?.Invoke();
    }

    /// <summary>Новая папка в списке: id из имени, без повторов.</summary>
    public static ShareRoot NewFolder(string path, bool writable, IEnumerable<ShareRoot> existing)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name))
            name = path;
        var id = "f-" + Guid.NewGuid().ToString("N")[..8];
        while (existing.Any(e => e.Id == id))
            id = "f-" + Guid.NewGuid().ToString("N")[..8];
        return new ShareRoot(id, name, System.IO.Path.GetFullPath(path), writable);
    }

    Saved? Load()
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(file), Message.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // По умолчанию писать можно только в «Загрузки»: туда же падают файлы с телефона.
    static List<ShareRoot> Defaults() =>
    [
        new("desktop", "Рабочий стол", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
        new("downloads", "Загрузки", KnownFolder(Environment.SpecialFolder.UserProfile, "Downloads"), true),
        new("documents", "Документы", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), false),
        new("pictures", "Изображения", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), false),
    ];

    static string KnownFolder(Environment.SpecialFolder parent, string child) =>
        System.IO.Path.Combine(Environment.GetFolderPath(parent), child);
}

/// <summary>
/// Файловые операции для телефона. Все пути — относительно открытой папки; выйти за её пределы нельзя
/// ни через «..», ни через ссылки и точки соединения (junction), которые ведут наружу.
/// </summary>
public sealed class FileService(ShareStore shares)
{
    public const string InboxRoot = "inbox";
    public const string PartSuffix = ".rdpart";

    /// <summary>Файл пришёл с телефона в «Входящие» («Поделиться → на ПК»).</summary>
    public event Action<string>? Received;

    public IReadOnlyList<ShareRoot> Roots() => shares.Roots();

    /// <summary>Полный путь внутри открытой папки или исключение 403/404.</summary>
    public string Resolve(string? rootId, string? relative, out ShareRoot root)
    {
        root = rootId == InboxRoot
            ? new ShareRoot(InboxRoot, "Входящие", shares.InboxPath, true)
            : shares.Roots().FirstOrDefault(r => r.Id == rootId)
              ?? throw new FileAccessException(404, "no_root", "Эта папка больше не открыта для телефона");
        var basePath = Path.GetFullPath(root.Path);
        var rel = (relative ?? "").Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(basePath, rel));
        var withSlash = basePath.EndsWith(Path.DirectorySeparatorChar) ? basePath : basePath + Path.DirectorySeparatorChar;
        if (!full.Equals(basePath, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(withSlash, StringComparison.OrdinalIgnoreCase))
            throw new FileAccessException(403, "outside", "Путь ведёт за пределы открытой папки");

        // Точка соединения или символическая ссылка внутри папки могла бы вести куда угодно — не идём по ним.
        var current = basePath;
        foreach (var part in full[basePath.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new FileInfo(current);
            if (info.Exists || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new FileAccessException(403, "link", "Ссылки и точки соединения не открываются с телефона");
            }
        }
        return full;
    }

    public IReadOnlyList<FileEntry> List(string rootId, string? path)
    {
        var dir = Resolve(rootId, path, out _);
        if (!Directory.Exists(dir))
            throw new FileAccessException(404, "not_found", "Папка не найдена");
        var result = new List<FileEntry>();
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
        {
            // Недокачанные файлы и скрытые служебные ссылки телефону не показываем.
            if (info.Name.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase)
                || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            result.Add(info is FileInfo f
                ? new FileEntry(f.Name, false, f.Length, f.LastWriteTimeUtc)
                : new FileEntry(info.Name, true, 0, info.LastWriteTimeUtc));
        }
        return result;
    }

    public string ReadablePath(string rootId, string? path)
    {
        var full = Resolve(rootId, path, out _);
        if (!File.Exists(full))
            throw new FileAccessException(404, "not_found", "Файл не найден");
        return full;
    }

    public void CreateFolder(string rootId, string? path)
    {
        var full = Writable(rootId, path);
        if (File.Exists(full) || Directory.Exists(full))
            throw new FileAccessException(409, "exists", "Такое имя уже есть");
        Directory.CreateDirectory(full);
    }

    public void Rename(string rootId, string? path, string? newName)
    {
        var full = Writable(rootId, path);
        CheckName(newName);
        var target = Path.Combine(Path.GetDirectoryName(full)!, newName!);
        Resolve(rootId, Path.GetRelativePath(Resolve(rootId, "", out _), target), out _);
        if (File.Exists(target) || Directory.Exists(target))
            throw new FileAccessException(409, "exists", "Такое имя уже есть");
        if (Directory.Exists(full))
            Directory.Move(full, target);
        else if (File.Exists(full))
            File.Move(full, target);
        else
            throw new FileAccessException(404, "not_found", "Файл не найден");
    }

    /// <summary>Удаление — в корзину Windows, чтобы можно было передумать.</summary>
    public void Delete(string rootId, string? path, Action<string> toRecycleBin)
    {
        var full = Writable(rootId, path);
        if (full.Equals(Path.GetFullPath(Resolve(rootId, "", out _)), StringComparison.OrdinalIgnoreCase))
            throw new FileAccessException(403, "root", "Саму открытую папку удалить нельзя");
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileAccessException(404, "not_found", "Файл не найден");
        toRecycleBin(full);
    }

    /// <summary>Открыть файл на ПК программой по умолчанию (как двойной щелчок в Проводнике).</summary>
    public void OpenOnPc(string rootId, string? path)
    {
        var full = Resolve(rootId, path, out _);
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileAccessException(404, "not_found", "Файл не найден");
        Process.Start(new ProcessStartInfo(full) { UseShellExecute = true })?.Dispose();
    }

    public enum Conflict { Ask, Replace, Rename }

    /// <summary>
    /// Начало приёма: решаем, под каким именем сохранить (конфликт имён), и резервируем его файлом «имя.rdpart».
    /// Если такой «.rdpart» уже есть — это докачка, возвращаем, сколько уже принято.
    /// </summary>
    public (string Name, long Have) BeginUpload(string rootId, string? folder, string? name, Conflict conflict)
    {
        CheckName(name);
        var dir = Writable(rootId, folder);
        if (!Directory.Exists(dir))
            throw new FileAccessException(404, "not_found", "Папка не найдена");
        var finalPath = Path.Combine(dir, name!);
        if (File.Exists(finalPath + PartSuffix))
            return (name!, new FileInfo(finalPath + PartSuffix).Length);
        if (Directory.Exists(finalPath))
            throw new FileAccessException(409, "exists", "Папка с таким именем уже есть");
        if (File.Exists(finalPath))
        {
            if (conflict == Conflict.Ask)
                throw new FileAccessException(409, "exists", "Файл с таким именем уже есть");
            if (conflict == Conflict.Rename)
                finalPath = FreeName(finalPath);
        }
        File.Create(finalPath + PartSuffix).Dispose();
        return (Path.GetFileName(finalPath), 0);
    }

    /// <summary>
    /// Приём данных с места [offset] (оно должно совпасть с уже принятым — иначе 409 с правильным числом).
    /// Когда принято [size] байт, «.rdpart» становится настоящим файлом. Возвращает, сколько принято всего.
    /// </summary>
    public async Task<long> UploadAsync(string rootId, string? folder, string? name, long size, long offset, Stream body, CancellationToken ct)
    {
        CheckName(name);
        var dir = Writable(rootId, folder);
        var finalPath = Path.Combine(dir, name!);
        var part = finalPath + PartSuffix;
        if (!File.Exists(part))
            throw new FileAccessException(409, "not_started", "Приём не начат — начните заново");
        var have = new FileInfo(part).Length;
        if (offset != have)
            throw new FileAccessException(409, "offset:" + have, $"Принято {have} байт — продолжите с этого места");

        await using (var file = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            await body.CopyToAsync(file, 1 << 16, ct);
        }

        var received = new FileInfo(part).Length;
        if (received > size)
        {
            File.Delete(part);
            throw new FileAccessException(400, "too_big", "Пришло больше данных, чем ожидалось — начните заново");
        }
        if (received == size)
        {
            // Имя выбрано в BeginUpload (в том числе «заменить»), поэтому перезаписываем.
            File.Move(part, finalPath, overwrite: true);
            if (rootId == InboxRoot)
                Received?.Invoke(finalPath);
        }
        return received;
    }

    string Writable(string rootId, string? path)
    {
        var full = Resolve(rootId, path, out var root);
        if (!root.Writable)
            throw new FileAccessException(403, "read_only", $"Папка «{root.Name}» открыта только для чтения. Разрешить запись можно на ПК: значок Couchpad → «Папки для телефона»");
        return full;
    }

    static void CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase))
            throw new FileAccessException(400, "bad_name", "Недопустимое имя файла");
    }

    /// <summary>«фото.jpg» → «фото (2).jpg», «фото (3).jpg»…</summary>
    internal static string FreeName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && !File.Exists(candidate + PartSuffix))
                return candidate;
        }
    }
}
