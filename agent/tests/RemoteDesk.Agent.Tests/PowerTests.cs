using Microsoft.Extensions.Time.Testing;
using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent.Tests;

public sealed class PowerTests
{
    readonly RecordingInput sink = new();
    readonly FakeTimeProvider time = new();
    readonly PowerScheduler power;
    readonly List<PowerTimer?> changes = [];
    readonly List<PowerTimer> warnings = [];

    public PowerTests()
    {
        power = new PowerScheduler(sink, time);
        power.Changed += changes.Add;
        power.Warning += warnings.Add;
    }

    [Fact]
    public void Run_executes_immediately()
    {
        power.Run(PowerAction.MonitorsOff, keepMonitorsOff: true);
        power.Run(PowerAction.Shutdown, force: true);
        Assert.Equal(["power monitors_off keep", "power shutdown force"], sink.Lines);
    }

    [Fact]
    public void Timer_warns_a_minute_before_and_fires()
    {
        power.Schedule(PowerAction.Restart, TimeSpan.FromMinutes(30));
        Assert.Equal(PowerAction.Restart, power.Current!.Action);

        time.Advance(TimeSpan.FromMinutes(28));
        Assert.Empty(warnings);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(warnings);
        Assert.Empty(sink.Lines);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(["power restart"], sink.Lines);
        Assert.Null(power.Current);
        Assert.Null(changes[^1]);
    }

    [Fact]
    public void Short_timer_warns_right_away()
    {
        power.Schedule(PowerAction.Sleep, TimeSpan.FromSeconds(20));
        time.Advance(TimeSpan.Zero);
        Assert.Single(warnings);
    }

    [Fact]
    public void Cancel_and_replace()
    {
        power.Schedule(PowerAction.Shutdown, TimeSpan.FromMinutes(5));
        power.Schedule(PowerAction.Sleep, TimeSpan.FromMinutes(10)); // заменяет выключение
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Empty(sink.Lines);

        Assert.True(power.Cancel());
        Assert.False(power.Cancel());
        time.Advance(TimeSpan.FromHours(1));
        Assert.Empty(sink.Lines);
        Assert.Null(changes[^1]);
    }

    [Fact]
    public void Running_the_same_action_now_clears_its_timer()
    {
        power.Schedule(PowerAction.MonitorsOff, TimeSpan.FromMinutes(5));
        power.Run(PowerAction.MonitorsOff);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(["power monitors_off"], sink.Lines);
    }

    [Fact]
    public void Failure_on_timer_does_not_crash()
    {
        var failing = new PowerScheduler(new Failing(), time);
        string? error = null;
        failing.Failed += (_, e) => error = e;
        failing.Schedule(PowerAction.Hibernate, TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("выключена", error);
        Assert.Throws<InvalidOperationException>(() => failing.Run(PowerAction.Hibernate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(60 * 60 * 25)]
    public void Bad_delays_are_rejected(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => power.Schedule(PowerAction.Shutdown, TimeSpan.FromSeconds(seconds)));

    sealed class Failing : IPowerSink
    {
        public void Execute(PowerAction action, bool force, bool keepMonitorsOff) => throw new InvalidOperationException("выключена");
    }
}
