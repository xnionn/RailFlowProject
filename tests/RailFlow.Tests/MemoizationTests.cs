using RailFlow.Core.Functional;

namespace RailFlow.Tests;

/// <summary>
/// The general memoization helpers (Week 9), Flatten and MapAsync. The app's route search uses its own memoized
/// shortest path; these check that the reusable helpers keep the same promise: the wrapped function
/// runs once per key, and a failure is not cached.
/// </summary>
public sealed class MemoizationTests
{
    [Fact]
    public void Memoize_CallsTheFunctionOncePerInput()
    {
        var calls = 0;
        Func<int, int> square = x => { calls++; return x * x; };
        var cached = square.Memoize();

        Assert.Equal(49, cached(7));
        Assert.Equal(49, cached(7));
        Assert.Equal(9, cached(3));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void MemoizeBy_UsesTheKeySelector_SoEqualKeysShareOneResult()
    {
        var calls = 0;
        Func<string, string> normalise = s => { calls++; return s.Trim().ToUpperInvariant(); };
        var cached = normalise.MemoizeBy(s => s.Trim().ToLowerInvariant());

        Assert.Equal("PARIS", cached("Paris"));
        Assert.Equal("PARIS", cached("  paris "));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task MemoizeAsync_CachesSuccess_ButRetriesAfterAFailure()
    {
        var calls = 0;
        Func<int, Task<int>> flaky = async x =>
        {
            await Task.Yield();
            return ++calls == 1 ? throw new InvalidOperationException("first call fails") : x * 10;
        };
        var cached = flaky.MemoizeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => cached(4));
        Assert.Equal(40, await cached(4));
        Assert.Equal(40, await cached(4));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Flatten_RemovesOneLevelOfMaybe_AndKeepsTheCase()
    {
        Maybe<Maybe<int>> nested = new Something<Maybe<int>>(new Something<int>(5));
        Maybe<Maybe<int>> empty = new UnhandledNothing<Maybe<int>>();
        Maybe<Maybe<int>> failed = new UnhandledError<Maybe<int>>(new TimeoutException());

        Assert.Equal(5, Assert.IsType<Something<int>>(nested.Flatten()).InnerValue);
        Assert.IsType<UnhandledNothing<int>>(empty.Flatten());
        Assert.IsType<TimeoutException>(Assert.IsType<UnhandledError<int>>(failed.Flatten()).ErrorDetails);
    }

    [Fact]
    public async Task MapAsync_MapsAValueThatArrivesLater_AndKeepsNothing()
    {
        var doubled = await Task.FromResult<Maybe<int>>(new Something<int>(21)).MapAsync(x => x * 2);
        var missing = await Task.FromResult<Maybe<int>>(new UnhandledNothing<int>()).MapAsync(x => x * 2);

        Assert.Equal(42, Assert.IsType<Something<int>>(doubled).InnerValue);
        Assert.IsAssignableFrom<Nothing<int>>(missing);
    }
}
