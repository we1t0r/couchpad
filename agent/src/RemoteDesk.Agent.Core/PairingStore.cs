using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDesk.Agent.Core;

public sealed record PairedDevice(
    string DeviceId,
    string Name,
    string Platform,
    string TokenHash,
    DateTimeOffset PairedAt,
    DateTimeOffset? LastSeen);

/// <summary>
/// Список сопряжённых телефонов (devices.json). Токены хранятся только в виде SHA-256.
/// </summary>
public sealed class PairingStore
{
    readonly string path;
    readonly Lock gate = new();
    List<PairedDevice> devices;

    public event Action? Changed;

    public PairingStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        path = Path.Combine(dataDir, "devices.json");
        devices = File.Exists(path)
            ? JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(path), Message.Options) ?? []
            : [];
    }

    public IReadOnlyList<PairedDevice> All
    {
        get { lock (gate) return devices.ToList(); }
    }

    /// <summary>Добавляет (или пересопрягает) телефон и возвращает его новый токен.</summary>
    public string Add(string deviceId, string name, string platform)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            devices.RemoveAll(d => d.DeviceId == deviceId);
            devices.Add(new PairedDevice(deviceId, name, platform, Hash(token), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            Save();
        }
        Changed?.Invoke();
        return token;
    }

    /// <summary>Проверяет токен телефона. При успехе обновляет «последний раз в сети».</summary>
    public PairedDevice? Validate(string deviceId, string token)
    {
        PairedDevice? found;
        lock (gate)
        {
            var index = devices.FindIndex(d => d.DeviceId == deviceId);
            if (index < 0)
                return null;
            var expected = Encoding.ASCII.GetBytes(devices[index].TokenHash);
            var actual = Encoding.ASCII.GetBytes(Hash(token));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                return null;
            found = devices[index] = devices[index] with { LastSeen = DateTimeOffset.UtcNow };
            Save();
        }
        Changed?.Invoke();
        return found;
    }

    public bool Remove(string deviceId)
    {
        bool removed;
        lock (gate)
        {
            removed = devices.RemoveAll(d => d.DeviceId == deviceId) > 0;
            if (removed)
                Save();
        }
        if (removed)
            Changed?.Invoke();
        return removed;
    }

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    void Save()
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(devices, Message.Options));
        File.Move(tmp, path, overwrite: true);
    }
}
