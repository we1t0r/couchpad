using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk.Agent.Core;

public enum PairResult { Ok, Bad, Expired }

/// <summary>
/// Одноразовый ключ сопряжения из QR-кода: 16 случайных байт, живёт <see cref="Lifetime"/>,
/// после успешного сопряжения сразу заменяется новым.
/// </summary>
public sealed class PairingSecrets(TimeProvider? time = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    readonly TimeProvider time = time ?? TimeProvider.System;
    readonly Lock gate = new();
    string key = "";
    DateTimeOffset expires = DateTimeOffset.MinValue;

    /// <summary>Ключ сменился: QR-код на экране нужно перерисовать.</summary>
    public event Action? Changed;

    public (string Key, DateTimeOffset Expires) Current
    {
        get
        {
            bool rotated;
            (string, DateTimeOffset) result;
            lock (gate)
            {
                rotated = time.GetUtcNow() >= expires;
                if (rotated)
                    RotateLocked();
                result = (key, expires);
            }
            if (rotated)
                Changed?.Invoke();
            return result;
        }
    }

    public PairResult TryConsume(string secret)
    {
        lock (gate)
        {
            if (key.Length == 0)
                return PairResult.Bad;
            var match = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(secret));
            if (!match)
                return PairResult.Bad;
            if (time.GetUtcNow() >= expires)
                return PairResult.Expired;
            RotateLocked();
        }
        Changed?.Invoke();
        return PairResult.Ok;
    }

    public void Rotate()
    {
        lock (gate)
            RotateLocked();
        Changed?.Invoke();
    }

    void RotateLocked()
    {
        key = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        expires = time.GetUtcNow() + Lifetime;
    }
}
