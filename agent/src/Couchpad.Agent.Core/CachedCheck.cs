namespace Couchpad.Agent.Core;

/// <summary>Проверка, которую считаем не чаще раза в <paramref name="periodMs"/> мс: её спрашивают на каждое движение мыши.</summary>
public static class CachedCheck
{
    public static Func<bool> Create(Func<bool> check, int periodMs = 300, Func<long>? clock = null)
    {
        var now = clock ?? (() => Environment.TickCount64);
        var gate = new Lock();
        var computed = false;
        long at = 0;
        var value = false;
        return () =>
        {
            lock (gate)
            {
                // Не «at = long.MinValue»: разность с ним переполняется в минус, и значение не считалось бы никогда.
                if (!computed || now() - at > periodMs)
                {
                    value = check();
                    at = now();
                    computed = true;
                }
                return value;
            }
        };
    }
}
