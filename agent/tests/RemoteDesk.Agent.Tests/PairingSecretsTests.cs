using Microsoft.Extensions.Time.Testing;
using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent.Tests;

public sealed class PairingSecretsTests
{
    [Fact]
    public void Expired_secret_is_rejected()
    {
        var time = new FakeTimeProvider();
        var secrets = new PairingSecrets(time);
        var key = secrets.Current.Key;

        time.Advance(PairingSecrets.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(PairResult.Expired, secrets.TryConsume(key));
    }

    [Fact]
    public void Reading_after_expiry_rotates_and_notifies()
    {
        var time = new FakeTimeProvider();
        var secrets = new PairingSecrets(time);
        var key = secrets.Current.Key;
        var changed = 0;
        secrets.Changed += () => changed++;

        time.Advance(PairingSecrets.Lifetime);

        Assert.NotEqual(key, secrets.Current.Key);
        Assert.Equal(1, changed);
    }
}
