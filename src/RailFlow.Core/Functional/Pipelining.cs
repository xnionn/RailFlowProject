namespace RailFlow.Core.Functional;

/// <summary>
/// Week 5: Composing Functions Using Pipelining.
/// <c>Pipe</c> lets a calculation read left-to-right instead of inside-out,
/// <c>Pipeline</c> folds any number of steps into one reusable function with Aggregate.
/// </summary>
public static class Pipelining
{
    /// <summary>
    /// value.Pipe(f) == f(value), so steps read in the order they run.
    /// </summary>
    public static TOut Pipe<TIn, TOut>(this TIn value, Func<TIn, TOut> step) => step(value);

    /// <summary>
    /// Combines the steps (first to last) into a single function. With no steps it is the identity function.
    /// </summary>
    public static Func<T, T> Pipeline<T>(params Func<T, T>[] steps) =>
        steps.Aggregate((Func<T, T>)(x => x), (composed, next) => composed.Compose(next));
}
