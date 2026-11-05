using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Couchpad.Agent.Core;

/// <summary>Файл, который ПК предлагает телефону («Отправить на телефон» в Проводнике).</summary>
public sealed record OutboxItem(string Id, string Name, long Size);

/// <summary>
/// Файлы, отправленные на телефон с ПК. Телефон скачивает их обычной загрузкой файлов
/// (корень <c>outbox</c>, путь — id), поэтому работает и докачка. Каждый файл доступен час:
/// телефон по случайному id получает только то, что человек сам отправил.
/// </summary>
public sealed class Outbox(TimeProvider? time = null)
{
    public const string Root = "outbox";
    static readonly TimeSpan Keep = TimeSpan.FromHours(1);
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly ConcurrentDictionary<string, (string Path, DateTimeOffset Added)> items = new();

    /// <summary>Добавить файлы (папки и несуществующие пути пропускаются).</summary>
    public IReadOnlyList<OutboxItem> Add(IEnumerable<string> paths)
    {
        Cleanup();
        var added = new List<OutboxItem>();
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                continue;
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            items[id] = (info.FullName, clock.GetUtcNow());
            added.Add(new OutboxItem(id, info.Name, info.Length));
        }
        return added;
    }

    /// <summary>Путь к файлу по id или null, если такого нет или он устарел.</summary>
    public string? PathOf(string? id)
    {
        Cleanup();
        return id is not null && items.TryGetValue(id, out var item) && File.Exists(item.Path) ? item.Path : null;
    }

    void Cleanup()
    {
        var now = clock.GetUtcNow();
        foreach (var (id, item) in items)
            if (now - item.Added > Keep)
                items.TryRemove(id, out _);
    }
}
