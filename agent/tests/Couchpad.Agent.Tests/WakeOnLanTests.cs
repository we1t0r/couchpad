using Couchpad.Agent.Core;
using Xunit.Abstractions;

namespace Couchpad.Agent.Tests;

public sealed class WakeOnLanTests(ITestOutputHelper output)
{
    /// <summary>Проверка только читает реестр и powercfg — на машине разработчика ничего не меняет.</summary>
    [Fact]
    public void Real_check_runs_and_always_mentions_bios()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var report = new WindowsWakeOnLan().Check();
        foreach (var c in report.Checks)
            output.WriteLine($"{c.State,-8} {c.Title}{(c.Hint is null ? "" : " — " + c.Hint)}");

        Assert.Contains(report.Checks, c => c.Id == "bios" && c.State == WolState.Unknown);
        Assert.Contains(report.Checks, c => c.Id == "cable");
    }
}
