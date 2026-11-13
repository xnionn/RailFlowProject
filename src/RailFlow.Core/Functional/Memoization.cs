using System.Collections.Concurrent;

namespace RailFlow.Core.Functional;

public sealed record MemoizationStats(int CacheHits, int CacheMisses, int CachedEntries)
{
    public double HitRatio => CacheHits + CacheMisses == 0
        ? 0.0
        : Math.Round((double)CacheHits / (CacheHits + CacheMisses) * 100.0, 1);
}

public sealed class MemoizedFunc<TIn, TOut> where TIn : notnull
{
    private readonly ConcurrentDictionary<TIn, Lazy<TOut>> _cache = new();
    private int _hits;
    private int _misses;
    private readonly Func<TIn, TOut> _sourceFunc;

    public MemoizedFunc(Func<TIn, TOut> sourceFunc)
    {
        _sourceFunc = sourceFunc;
    }

    public TOut Invoke(TIn input)
    {
        if (_cache.TryGetValue(input, out var existing))
        {
            Interlocked.Increment(ref _hits);
            return existing.Value;
        }

        Interlocked.Increment(ref _misses);
        var lazy = _cache.GetOrAdd(
            input,
            key => new Lazy<TOut>(() => _sourceFunc(key), LazyExecutionMode));
        return lazy.Value;
    }

    private static LazyThreadSafetyMode LazyExecutionMode => LazyThreadSafetyMode.ExecutionAndPublication;

    public MemoizationStats GetStats() => new(_hits, _misses, _cache.Count);

    public void Clear()
    {
        _cache.Clear();
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
    }
}

/// <summary>
/// Memoization Extensions.
/// </summary>
public static class Memoization
{
    /// <summary>
    /// Simple closure-based Memoize.
    /// </summary>
    public static Func<TIn, TOut> Memoize<TIn, TOut>(this Func<TIn, TOut> @this)
        where TIn : notnull
    {
        var dict = new Dictionary<TIn, TOut>();
        return x =>
        {
            if (!dict.ContainsKey(x))
                dict.Add(x, @this(x));
            return dict[x];
        };
    }

    /// <summary>
    /// MemoizeBy with custom key selector for complex objects.
    /// </summary>
    public static Func<TIn, TOut> MemoizeBy<TIn, TKey, TOut>(
        this Func<TIn, TOut> @this,
        Func<TIn, TKey> keySelector)
        where TKey : notnull
    {
        var dict = new ConcurrentDictionary<TKey, Lazy<TOut>>();
        return x =>
        {
            var key = keySelector(x);
            return dict.GetOrAdd(
                key,
                _ => new Lazy<TOut>(() => @this(x), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        };
    }

    /// <summary>
    /// Thread-safe memoization that also counts hits and misses (shown in the Lab tab).
    /// </summary>
    public static MemoizedFunc<TIn, TOut> MemoizeThreadSafeWithStats<TIn, TOut>(this Func<TIn, TOut> @this)
        where TIn : notnull =>
        new(@this);

    /// <summary>
    /// Async memoization using ConcurrentDictionary and Lazy<Task<TOut>>.
    /// Automatically evicts faulted tasks so transient failures can recover.
    /// </summary>
    public static Func<TIn, Task<TOut>> MemoizeAsync<TIn, TOut>(this Func<TIn, Task<TOut>> @this)
        where TIn : notnull
    {
        var cache = new ConcurrentDictionary<TIn, Lazy<Task<TOut>>>();
        return async input =>
        {
            var lazy = cache.GetOrAdd(
                input,
                key => new Lazy<Task<TOut>>(
                    () => Task.Run(() => @this(key)),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                return await lazy.Value.ConfigureAwait(false);
            }
            catch
            {
                cache.TryRemove(input, out _);
                throw;
            }
        };
    }
}
