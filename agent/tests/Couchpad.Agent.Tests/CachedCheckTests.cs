using Couchpad.Agent.Core;

namespace Couchpad.Agent.Tests;

public class CachedCheckTests
{
    [Fact]
    public void Computes_at_once_then_at_most_once_per_period()
    {
        long time = 1_000;
        var calls = 0;
        var answer = true;
        var check = CachedCheck.Create(() => { calls++; return answer; }, 300, () => time);

        Assert.True(check()); // первый же вызов — настоящее значение, а не «false по умолчанию»
        answer = false;
        time += 100;
        Assert.True(check()); // ещё в пределах 300 мс — из кэша
        Assert.Equal(1, calls);
        time += 301;
        Assert.False(check());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Works_right_after_boot_with_a_small_clock() =>
        Assert.True(CachedCheck.Create(() => true, 300, () => 5)());
}
