using System.Collections.Immutable;
using System.Diagnostics;

namespace RailFlow.Core.Functional;

/// <summary>
/// Factory helpers for <see cref="Result{T}"/> (Week 7: Exception Handling in FP).
/// </summary>
public static class Result
{
    public static Result<T> Ok<T>(T value) => new Success<T>(value);

    public static Result<T> Fail<T>(string reason) => new Failure<T>(reason);
}

/// <summary>
/// Map / Bind / Ensure / Match for <see cref="Result{T}"/> and <see cref="Either{TLeft,TRight}"/>
/// (Week 7: failure becomes part of the return type instead of a thrown exception).
/// None of these methods use try/catch: expected failures travel on the failure track.
/// </summary>
public static class ResultExtensions
{
    // --- Result<T> ---

    public static Result<TOut> Map<TIn, TOut>(this Result<TIn> @this, Func<TIn, TOut> map) =>
        @this switch
        {
            Success<TIn> s => new Success<TOut>(map(s.Data)),
            Failure<TIn> f => new Failure<TOut>(f.Reason, f.Exception),
            _ => throw new UnreachableException()
        };

    public static Result<TOut> Bind<TIn, TOut>(this Result<TIn> @this, Func<TIn, Result<TOut>> bind) =>
        @this switch
        {
            Success<TIn> s => bind(s.Data),
            Failure<TIn> f => new Failure<TOut>(f.Reason, f.Exception),
            _ => throw new UnreachableException()
        };

    /// <summary>
    /// Keeps the value on the success track only when the predicate holds.
    /// </summary>
    public static Result<T> Ensure<T>(this Result<T> @this, Func<T, bool> predicate, string reason) =>
        @this.Bind(value => predicate(value) ? Result.Ok(value) : Result.Fail<T>(reason));

    public static TOut Match<T, TOut>(
        this Result<T> @this,
        Func<T, TOut> onSuccess,
        Func<string, TOut> onFailure) =>
        @this switch
        {
            Success<T> s => onSuccess(s.Data),
            Failure<T> f => onFailure(f.Reason),
            _ => throw new UnreachableException()
        };

    public static bool IsSuccess<T>(this Result<T> @this) => @this is Success<T>;

    /// <summary>
    /// Converts a Maybe into a Result: Nothing gets an explanation, Error keeps its exception.
    /// </summary>
    public static Result<T> ToResult<T>(this Maybe<T> @this, string reasonIfNothing) =>
        @this switch
        {
            Something<T> { InnerValue: { } value } => new Success<T>(value),
            Error<T> { ErrorDetails: var ex } => new Failure<T>(ex.Message, ex),
            _ => new Failure<T>(reasonIfNothing)
        };

    /// <summary>
    /// Splits a sequence of results into the accepted values and the failure reasons.
    /// </summary>
    public static (ImmutableList<T> Accepted, ImmutableList<string> Rejected) Partition<T>(
        this IEnumerable<Result<T>> results) =>
        results.Aggregate(
            (Accepted: ImmutableList<T>.Empty, Rejected: ImmutableList<string>.Empty),
            (acc, result) => result switch
            {
                Success<T> s => (acc.Accepted.Add(s.Data), acc.Rejected),
                Failure<T> f => (acc.Accepted, acc.Rejected.Add(f.Reason)),
                _ => acc
            });

    // --- Either<TLeft, TRight> (Right = primary value, Left = alternative / error info) ---

    public static Either<TLeft, TOut> Map<TLeft, TRight, TOut>(
        this Either<TLeft, TRight> @this,
        Func<TRight, TOut> map) =>
        @this switch
        {
            Right<TLeft, TRight> r => new Right<TLeft, TOut>(map(r.Value)),
            Left<TLeft, TRight> l => new Left<TLeft, TOut>(l.Value),
            _ => throw new UnreachableException()
        };

    public static Either<TLeft, TOut> Bind<TLeft, TRight, TOut>(
        this Either<TLeft, TRight> @this,
        Func<TRight, Either<TLeft, TOut>> bind) =>
        @this switch
        {
            Right<TLeft, TRight> r => bind(r.Value),
            Left<TLeft, TRight> l => new Left<TLeft, TOut>(l.Value),
            _ => throw new UnreachableException()
        };

    public static TOut Match<TLeft, TRight, TOut>(
        this Either<TLeft, TRight> @this,
        Func<TLeft, TOut> onLeft,
        Func<TRight, TOut> onRight) =>
        @this switch
        {
            Right<TLeft, TRight> r => onRight(r.Value),
            Left<TLeft, TRight> l => onLeft(l.Value),
            _ => throw new UnreachableException()
        };
}
