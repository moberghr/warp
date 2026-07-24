using Shouldly;
using Warp.Core;

namespace Warp.Tests.Features.Locking;

/// <summary>
/// Unit tests for the shipped single-process <see cref="InProcessLockProvider"/>. Verifies the
/// timeout-zero fast-fail, release-on-dispose, and per-name independence that <c>AddSagas()</c>
/// relies on when no database provider is configured.
/// </summary>
[Trait("Category", "NoDb")]
public class InProcessLockProviderTests
{
    private static CancellationToken CT => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task TryAcquire_WhenHeld_ZeroTimeout_ReturnsNull()
    {
        using var provider = new InProcessLockProvider();

        await using var first = await provider.TryAcquireAsync("k", TimeSpan.Zero, CT);
        first.ShouldNotBeNull();

        var second = await provider.TryAcquireAsync("k", TimeSpan.Zero, CT);
        second.ShouldBeNull();
    }

    [TimedFact]
    public async Task TryAcquire_AfterRelease_Succeeds()
    {
        using var provider = new InProcessLockProvider();

        var first = await provider.TryAcquireAsync("k", TimeSpan.Zero, CT);
        first.ShouldNotBeNull();
        await first.DisposeAsync();

        await using var second = await provider.TryAcquireAsync("k", TimeSpan.Zero, CT);
        second.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task TryAcquire_DifferentNames_AreIndependent()
    {
        using var provider = new InProcessLockProvider();

        await using var a = await provider.TryAcquireAsync("a", TimeSpan.Zero, CT);
        await using var b = await provider.TryAcquireAsync("b", TimeSpan.Zero, CT);

        a.ShouldNotBeNull();
        b.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task TryAcquire_PositiveTimeout_AcquiresWhenFree()
    {
        using var provider = new InProcessLockProvider();

        await using var handle = await provider.TryAcquireAsync("k", TimeSpan.FromSeconds(1), CT);

        handle.ShouldNotBeNull();
    }
}
