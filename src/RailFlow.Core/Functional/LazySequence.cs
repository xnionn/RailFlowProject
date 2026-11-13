namespace RailFlow.Core.Functional;

/// <summary>
/// Week 8: Extension Methods and LINQ.
/// Hand-written lazy operators showing how Where / Select work inside: nothing runs
/// until the sequence is enumerated, and it runs again on every enumeration.
/// Used by BookingQueries.ForOrigin; elsewhere the built-in LINQ operators are used.
/// </summary>
public static class LazySequence
{
    public static IEnumerable<T> Filter<T>(this IEnumerable<T> source, Func<T, bool> predicate)
    {
        foreach (var item in source)
        {
            if (predicate(item))
                yield return item;
        }
    }

    public static IEnumerable<TOut> Transform<TIn, TOut>(this IEnumerable<TIn> source, Func<TIn, TOut> selector)
    {
        foreach (var item in source)
        {
            yield return selector(item);
        }
    }
}
