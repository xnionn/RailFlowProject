namespace RailFlow.Core.Functional;

/// <summary>
/// Higher-Order Combinators & Monad Extensions.
/// Implements Map, Bind, BindAsync, OnSomething, OnNothing, OnError,
/// Fork, Alt, Compose, Transduce, Tap, and Flatten.
/// </summary>
public static class Combinators
{
    // --- Return (unit) ---
    public static Maybe<T> ToMaybe<T>(this T? value) =>
        value is null ? new UnhandledNothing<T>() : new Something<T>(value);

    // --- Map & Bind for Maybe<T> (Handles value types like 0 or false safely without default(T) bug) ---
    public static Maybe<TOut> Map<TIn, TOut>(this Maybe<TIn> @this, Func<TIn, TOut> map)
    {
        try
        {
            return @this switch
            {
                Something<TIn> { InnerValue: { } val } => new Something<TOut>(map(val)),
                UnhandledNothing<TIn> => new UnhandledNothing<TOut>(),
                Nothing<TIn> => new Nothing<TOut>(),
                UnhandledError<TIn> { ErrorDetails: var e } => new UnhandledError<TOut>(e),
                Error<TIn> { ErrorDetails: var e } => new Error<TOut>(e),
                _ => new UnhandledNothing<TOut>()
            };
        }
        catch (Exception ex)
        {
            return new UnhandledError<TOut>(ex);
        }
    }

    public static Maybe<TOut> Bind<TIn, TOut>(this Maybe<TIn> @this, Func<TIn, Maybe<TOut>> bind)
    {
        try
        {
            return @this switch
            {
                Something<TIn> { InnerValue: { } val } => bind(val),
                UnhandledNothing<TIn> => new UnhandledNothing<TOut>(),
                Nothing<TIn> => new Nothing<TOut>(),
                UnhandledError<TIn> { ErrorDetails: var e } => new UnhandledError<TOut>(e),
                Error<TIn> { ErrorDetails: var e } => new Error<TOut>(e),
                _ => new UnhandledNothing<TOut>()
            };
        }
        catch (Exception ex)
        {
            return new UnhandledError<TOut>(ex);
        }
    }

    // --- Flatten nested Maybe<Maybe<T>> ---
    public static Maybe<T> Flatten<T>(this Maybe<Maybe<T>> @this) =>
        @this switch
        {
            Something<Maybe<T>> { InnerValue: { } inner } => inner,
            UnhandledNothing<Maybe<T>> => new UnhandledNothing<T>(),
            Nothing<Maybe<T>> => new Nothing<T>(),
            UnhandledError<Maybe<T>> { ErrorDetails: var e } => new UnhandledError<T>(e),
            Error<Maybe<T>> { ErrorDetails: var e } => new Error<T>(e),
            _ => new UnhandledNothing<T>()
        };

    // --- Side-effect handlers: each problem is handled (logged) once ---
    public static Maybe<T> OnSomething<T>(this Maybe<T> @this, Action<T> onSomething)
    {
        if (@this is Something<T> { InnerValue: { } val })
        {
            onSomething(val);
        }
        return @this;
    }

    public static Maybe<T> OnNothing<T>(this Maybe<T> @this, Action onNothing)
    {
        if (@this is UnhandledNothing<T>)
        {
            onNothing();
            return new Nothing<T>();
        }
        return @this;
    }

    public static Maybe<T> OnError<T>(this Maybe<T> @this, Action<Exception> onError)
    {
        if (@this is UnhandledError<T> { ErrorDetails: var ex })
        {
            onError(ex);
            return new Error<T>(ex);
        }
        return @this;
    }

    // --- Async composition ---
    public static async Task<Maybe<TOut>> BindAsync<TIn, TOut>(
        this Task<Maybe<TIn>> @this,
        Func<TIn, Task<Maybe<TOut>>> bind)
    {
        try
        {
            var awaited = await @this.ConfigureAwait(false);
            return awaited switch
            {
                Something<TIn> { InnerValue: { } val } => await bind(val).ConfigureAwait(false),
                UnhandledNothing<TIn> => new UnhandledNothing<TOut>(),
                Nothing<TIn> => new Nothing<TOut>(),
                UnhandledError<TIn> { ErrorDetails: var e } => new UnhandledError<TOut>(e),
                Error<TIn> { ErrorDetails: var e } => new Error<TOut>(e),
                _ => new UnhandledNothing<TOut>()
            };
        }
        catch (Exception ex)
        {
            return new UnhandledError<TOut>(ex);
        }
    }

    public static async Task<Maybe<TOut>> MapAsync<TIn, TOut>(
        this Task<Maybe<TIn>> @this,
        Func<TIn, TOut> map)
    {
        var awaited = await @this.ConfigureAwait(false);
        return awaited.Map(map);
    }

    // --- Higher-order combinators ---

    /// <summary>
    /// Tap combinator: runs a side effect (the app uses it for logging) and returns the original value.
    /// </summary>
    public static T Tap<T>(this T @this, Action<T> sideEffect)
    {
        sideEffect(@this);
        return @this;
    }

    /// <summary>
    /// Fork combinator: fans out an input across multiple prong functions and combines the results.
    /// </summary>
    public static TOut Fork<TIn, TMiddle, TOut>(
        this TIn @this,
        Func<IEnumerable<TMiddle>, TOut> joinFunc,
        params Func<TIn, TMiddle>[] prongs)
    {
        var intermediateValues = prongs.Select(x => x(@this));
        return joinFunc(intermediateValues);
    }

    /// <summary>
    /// Two-prong Fork combinator: computes two projections (e.g. Sum and Count) in parallel conceptually and joins them.
    /// </summary>
    public static TOut Fork<TIn, T1, T2, TOut>(
        this TIn @this,
        Func<TIn, T1> prong1,
        Func<TIn, T2> prong2,
        Func<T1, T2, TOut> joinFunc) =>
        joinFunc(prong1(@this), prong2(@this));

    /// <summary>
    /// Alt combinator: tries each candidate function until one returns a non-default / valid result.
    /// </summary>
    public static TOut Alt<TIn, TOut>(this TIn @this, params Func<TIn, TOut>[] args) =>
        args.Select(x => x(@this))
            .First(x => ! EqualityComparer<TOut>.Default.Equals(x, default));

    /// <summary>
    /// AltMaybe combinator: tries each candidate Maybe-producing function until one returns Something<TOut>.
    /// </summary>
    public static Maybe<TOut> AltMaybe<TIn, TOut>(this TIn @this, params Func<TIn, Maybe<TOut>>[] candidates)
    {
        Maybe<TOut> last = new UnhandledNothing<TOut>();
        foreach (var fn in candidates)
        {
            last = fn(@this);
            if (last is Something<TOut>)
                return last;
        }
        return last;
    }

    /// <summary>
    /// Compose combinator: chains two functions f1: TIn -> TMid and f2: TMid -> TOut into TIn -> TOut.
    /// </summary>
    public static Func<TIn, TOut> Compose<TIn, TMid, TOut>(
        this Func<TIn, TMid> f1,
        Func<TMid, TOut> f2) =>
        x => f2(f1(x));

    /// <summary>
    /// Transduce combinator: fuses an enumerable pipeline transformation and aggregation into a single reusable function.
    /// </summary>
    public static Func<IEnumerable<TIn>, TOut> Transduce<TIn, TFilterOut, TOut>(
        this Func<IEnumerable<TIn>, IEnumerable<TFilterOut>> transformer,
        Func<IEnumerable<TFilterOut>, TOut> aggregator) =>
        x => aggregator(transformer(x));
}
