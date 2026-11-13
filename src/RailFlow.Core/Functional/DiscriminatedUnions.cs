namespace RailFlow.Core.Functional;

/// <summary>
/// Generic Discriminated Union representing optionality and explicit failure states
/// Includes UnhandledNothing and UnhandledError to prevent duplicate logging across chains.
/// </summary>
public abstract record Maybe<T>
{
    public virtual T? Value => default;
}

public sealed record Something<T>(T InnerValue) : Maybe<T>
{
    public override T? Value => InnerValue;
}

public record Nothing<T> : Maybe<T>;

public sealed record UnhandledNothing<T> : Nothing<T>;

public record Error<T>(Exception ErrorDetails) : Maybe<T>
{
    public string Message => ErrorDetails.Message;
}

public sealed record UnhandledError<T>(Exception ErrorDetails) : Error<T>(ErrorDetails);

/// <summary>
/// Result<T> Discriminated Union.
/// </summary>
public abstract record Result<T>;
public sealed record Success<T>(T Data) : Result<T>;
public sealed record Failure<T>(string Reason, Exception? Exception = null) : Result<T>;

/// <summary>
/// Either<TLeft, TRight> Discriminated Union.
/// By convention, Left holds validation/fallback info and Right holds the primary value.
/// </summary>
public abstract record Either<TLeft, TRight>;
public sealed record Left<TLeft, TRight>(TLeft Value) : Either<TLeft, TRight>;
public sealed record Right<TLeft, TRight>(TRight Value) : Either<TLeft, TRight>;

/// <summary>
/// Reader<TEnv, TVal> Monad for Functional Dependency Injection.
/// </summary>
public sealed class Reader<TEnv, TVal>(Func<TEnv, TVal> run)
{
    public Func<TEnv, TVal> Run { get; } = run;

    public Reader<TEnv, TOut> Map<TOut>(Func<TVal, TOut> map) =>
        new(env => map(Run(env)));

    public Reader<TEnv, TOut> Bind<TOut>(Func<TVal, Reader<TEnv, TOut>> bind) =>
        new(env => bind(Run(env)).Run(env));
}

/// <summary>
/// State<TState, TVal> Monad for explicit immutable state transitions.
/// </summary>
public sealed class State<TState, TVal>(Func<TState, (TVal Value, TState NextState)> run)
{
    public Func<TState, (TVal Value, TState NextState)> Run { get; } = run;

    public State<TState, TOut> Map<TOut>(Func<TVal, TOut> map) =>
        new(s =>
        {
            var (val, next) = Run(s);
            return (map(val), next);
        });

    public State<TState, TOut> Bind<TOut>(Func<TVal, State<TState, TOut>> bind) =>
        new(s =>
        {
            var (val, next) = Run(s);
            return bind(val).Run(next);
        });
}
