using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class MfaAssuranceCacheTests
{
    [Fact]
    public void Try_get_returns_the_cached_state_within_the_five_minute_lifetime()
    {
        var cache = new MfaAssuranceCache(new MutableTimeProvider(DateTimeOffset.UtcNow));
        cache.Set("subject", MfaAssuranceVerificationState.VerifiedFactorPresent);

        var hit = cache.TryGet("subject", out var state);

        Assert.True(hit);
        Assert.Equal(MfaAssuranceVerificationState.VerifiedFactorPresent, state);
    }

    [Fact]
    public void Try_get_expires_the_entry_after_five_minutes()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var cache = new MfaAssuranceCache(timeProvider);
        cache.Set("subject", MfaAssuranceVerificationState.VerifiedFactorPresent);
        timeProvider.Advance(MfaAssuranceCache.Lifetime);

        var hit = cache.TryGet("subject", out var state);

        Assert.False(hit);
        Assert.Equal(MfaAssuranceVerificationState.Unavailable, state);
    }

    [Fact]
    public void Try_get_keeps_the_entry_one_second_before_expiry()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var cache = new MfaAssuranceCache(timeProvider);
        cache.Set("subject", MfaAssuranceVerificationState.VerifiedFactorAbsent);
        timeProvider.Advance(MfaAssuranceCache.Lifetime - TimeSpan.FromSeconds(1));

        var hit = cache.TryGet("subject", out var state);

        Assert.True(hit);
        Assert.Equal(MfaAssuranceVerificationState.VerifiedFactorAbsent, state);
    }

    [Fact]
    public void Set_ignores_unavailable_results_so_failures_are_retried()
    {
        var cache = new MfaAssuranceCache(new MutableTimeProvider(DateTimeOffset.UtcNow));
        cache.Set("subject", MfaAssuranceVerificationState.Unavailable);

        var hit = cache.TryGet("subject", out var state);

        Assert.False(hit);
        Assert.Equal(MfaAssuranceVerificationState.Unavailable, state);
    }

    [Fact]
    public void Entries_do_not_leak_between_subjects()
    {
        var cache = new MfaAssuranceCache(new MutableTimeProvider(DateTimeOffset.UtcNow));
        cache.Set("subject-a", MfaAssuranceVerificationState.VerifiedFactorPresent);

        var hit = cache.TryGet("subject-b", out _);

        Assert.False(hit);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
