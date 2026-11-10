namespace Couchpad.Agent.Core;

public enum PowerAction { Shutdown, Restart, Sleep, Hibernate, Lock, SignOut, MonitorsOff, MonitorsOn }

public static class PowerActions
{
    static readonly Dictionary<string, PowerAction> ByName = new()
    {
        ["shutdown"] = PowerAction.Shutdown,
        ["restart"] = PowerAction.Restart,
        ["sleep"] = PowerAction.Sleep,
        ["hibernate"] = PowerAction.Hibernate,
        ["lock"] = PowerAction.Lock,
        ["signout"] = PowerAction.SignOut,
        ["monitors_off"] = PowerAction.MonitorsOff,
        ["monitors_on"] = PowerAction.MonitorsOn,
    };

    public static PowerAction? Parse(string? name) => name is not null && ByName.TryGetValue(name, out var a) ? a : null;

    public static string Name(this PowerAction action) => ByName.First(p => p.Value == action).Key;

    /// <summary>Как назвать действие в уведомлении на ПК: «Компьютер выключится через…».</summary>
    public static string Describe(this PowerAction action) => action switch
    {
        PowerAction.Shutdown => "Компьютер выключится",
        PowerAction.Restart => "Компьютер перезагрузится",
        PowerAction.Sleep => "Компьютер уйдёт в сон",
        PowerAction.Hibernate => "Компьютер уйдёт в гибернацию",
        PowerAction.Lock => "Компьютер заблокируется",
        PowerAction.SignOut => "Выполнится выход из учётной записи",
        PowerAction.MonitorsOff => "Мониторы выключатся",
        _ => "Мониторы включатся",
    };
}

/// <summary>Питание и мониторы. На Windows — настоящие команды, в тестах — запись вызовов.</summary>
public interface IPowerSink
{
    /// <param name="force">Не ждать, пока программы сохранят данные (только выключение, перезагрузка, выход).</param>
    /// <param name="keepMonitorsOff">Для MonitorsOff: мышь не будит мониторы, только клавиатура ПК или телефон.</param>
    void Execute(PowerAction action, bool force, bool keepMonitorsOff);
}

public sealed class NullPower : IPowerSink
{
    public void Execute(PowerAction action, bool force, bool keepMonitorsOff) { }
}

/// <summary>Отложенное действие: что, когда и принудительно ли.</summary>
public sealed record PowerTimer(PowerAction Action, DateTimeOffset At, bool Force, bool KeepMonitorsOff);

/// <summary>
/// Выполняет действия питания сразу или по таймеру. Таймер один: новый заменяет старый.
/// За минуту до срабатывания (или сразу, если ждать меньше) поднимает <see cref="Warning"/>,
/// чтобы на ПК показать окно с кнопкой «Отменить».
/// </summary>
public sealed class PowerScheduler(IPowerSink sink, TimeProvider? time = null) : IDisposable
{
    public static readonly TimeSpan WarningBefore = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromDays(1);

    readonly TimeProvider time = time ?? TimeProvider.System;
    readonly Lock gate = new();
    ITimer? fireTimer, warnTimer;

    public PowerTimer? Current { get; private set; }

    /// <summary>Последнее выполненное действие и когда — чтобы понять, что Windows закрыла агента из-за него.</summary>
    public (PowerAction Action, DateTimeOffset At)? LastRun { get; private set; }

    /// <summary>Таймер поставлен, отменён или сработал.</summary>
    public event Action<PowerTimer?>? Changed;

    /// <summary>Скоро сработает — показать предупреждение.</summary>
    public event Action<PowerTimer>? Warning;

    /// <summary>Действие не удалось (например, гибернация выключена в Windows).</summary>
    public event Action<PowerAction, string>? Failed;

    /// <summary>Выполнить сейчас. Отложенный таймер того же действия при этом снимается.</summary>
    public void Run(PowerAction action, bool force = false, bool keepMonitorsOff = false)
    {
        lock (gate)
        {
            if (Current?.Action == action)
                ClearLocked();
        }
        Execute(action, force, keepMonitorsOff);
    }

    public PowerTimer Schedule(PowerAction action, TimeSpan delay, bool force = false, bool keepMonitorsOff = false)
    {
        if (delay <= TimeSpan.Zero || delay > MaxDelay)
            throw new ArgumentOutOfRangeException(nameof(delay));
        PowerTimer timer;
        lock (gate)
        {
            ClearLocked();
            timer = new PowerTimer(action, time.GetUtcNow() + delay, force, keepMonitorsOff);
            Current = timer;
            fireTimer = time.CreateTimer(_ => Fire(timer), null, delay, Timeout.InfiniteTimeSpan);
            var warnIn = delay - WarningBefore;
            warnTimer = time.CreateTimer(_ => Warn(timer), null, warnIn > TimeSpan.Zero ? warnIn : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
        Changed?.Invoke(timer);
        return timer;
    }

    /// <summary>true — был таймер и его отменили.</summary>
    public bool Cancel()
    {
        lock (gate)
        {
            if (Current is null)
                return false;
            ClearLocked();
        }
        Changed?.Invoke(null);
        return true;
    }

    void Warn(PowerTimer timer)
    {
        lock (gate)
        {
            if (Current != timer)
                return;
        }
        Warning?.Invoke(timer);
    }

    void Fire(PowerTimer timer)
    {
        lock (gate)
        {
            if (Current != timer)
                return;
            ClearLocked();
        }
        Changed?.Invoke(null);
        // Поток таймера: исключение отсюда уронило бы агента, поэтому только сообщаем об ошибке.
        try
        {
            Execute(timer.Action, timer.Force, timer.KeepMonitorsOff);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Сообщить о сбое, случившемся уже после выполнения (например, мониторы снова включились).</summary>
    public void Report(PowerAction action, string error) => Failed?.Invoke(action, error);

    void Execute(PowerAction action, bool force, bool keepMonitorsOff)
    {
        try
        {
            LastRun = (action, time.GetUtcNow());
            sink.Execute(action, force, keepMonitorsOff);
        }
        catch (Exception e)
        {
            Failed?.Invoke(action, e.Message);
            throw;
        }
    }

    void ClearLocked()
    {
        fireTimer?.Dispose();
        warnTimer?.Dispose();
        fireTimer = warnTimer = null;
        Current = null;
    }

    public void Dispose()
    {
        lock (gate)
            ClearLocked();
    }
}
