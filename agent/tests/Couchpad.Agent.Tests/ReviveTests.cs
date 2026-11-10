using Couchpad.Agent.Core;

namespace Couchpad.Agent.Tests;

/// <summary>Агента закрыла Windows, а перезагрузка не состоялась — когда его запускать снова.</summary>
public sealed class ReviveTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "couchpad-revive-" + Guid.NewGuid().ToString("N"));
    static readonly DateTime Logon = new(2026, 11, 2, 2, 54, 37, DateTimeKind.Utc);
    static readonly DateTime Closed = new(2026, 11, 10, 10, 48, 37, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Due_when_closed_in_the_same_logon_and_windows_had_time_to_decide()
    {
        Assert.True(Revive.Due(Closed, Logon, Closed + TimeSpan.FromSeconds(61)));
    }

    [Fact]
    public void Not_due_while_windows_may_still_be_closing_programs()
    {
        Assert.False(Revive.Due(Closed, Logon, Closed + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Not_due_after_a_real_restart()
    {
        // Вход в Windows новее отметки: перезагрузка состоялась, агента запустит автозапуск.
        Assert.False(Revive.Due(Closed, Closed + TimeSpan.FromMinutes(2), Closed + TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void Not_due_on_the_sign_in_screen_or_when_the_mark_is_old()
    {
        Assert.False(Revive.Due(Closed, null, Closed + TimeSpan.FromMinutes(1)));
        Assert.False(Revive.Due(Closed, Logon, Closed + TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Mark_keeps_the_action_the_phone_asked_for()
    {
        Assert.Null(Revive.Read(dir));

        Revive.Mark(dir, PowerAction.Restart);
        var marker = Revive.Read(dir);
        Assert.Equal(PowerAction.Restart, marker!.Action);
        Assert.InRange(DateTime.UtcNow - marker.WrittenUtc, TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(30));

        // Windows закрыла агента не по просьбе телефона — действия нет, но запускать снова всё равно нужно.
        Revive.Mark(dir, null);
        Assert.Null(Revive.Read(dir)!.Action);

        Revive.Clear(dir);
        Assert.Null(Revive.Read(dir));
    }

    [Fact]
    public void Scheduler_remembers_the_last_action()
    {
        using var power = new PowerScheduler(new NullPower());
        Assert.Null(power.LastRun);
        power.Run(PowerAction.Restart);
        Assert.Equal(PowerAction.Restart, power.LastRun!.Value.Action);
    }
}
