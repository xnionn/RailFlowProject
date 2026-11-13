using System.Collections;

namespace RailFlow.Core.Functional;

/// <summary>
/// Currying, partial application and indefinite-loop helpers
/// </summary>
public static class CurryingAndLoops
{
    // --- Currying ---
    public static Func<T1, Func<T2, TOut>> Curry<T1, T2, TOut>(this Func<T1, T2, TOut> f) =>
        x => y => f(x, y);

    public static Func<T1, Func<T2, Func<T3, TOut>>> Curry<T1, T2, T3, TOut>(this Func<T1, T2, T3, TOut> f) =>
        x => y => z => f(x, y, z);

    public static Func<T1, Func<T2, Func<T3, Func<T4, TOut>>>> Curry<T1, T2, T3, T4, TOut>(this Func<T1, T2, T3, T4, TOut> f) =>
        w => x => y => z => f(w, x, y, z);

    // --- Partial Application ---
    public static Func<T2, TOut> Partial<T1, T2, TOut>(this Func<T1, T2, TOut> f, T1 arg1) =>
        y => f(arg1, y);

    public static Func<T2, T3, TOut> Partial<T1, T2, T3, TOut>(this Func<T1, T2, T3, TOut> f, T1 arg1) =>
        (y, z) => f(arg1, y, z);

    public static Func<T3, TOut> Partial<T1, T2, T3, TOut>(this Func<T1, T2, T3, TOut> f, T1 arg1, T2 arg2) =>
        z => f(arg1, arg2, z);

    public static Func<T4, TOut> Partial<T1, T2, T3, T4, TOut>(
        this Func<T1, T2, T3, T4, TOut> f, T1 arg1, T2 arg2, T3 arg3) =>
        z => f(arg1, arg2, arg3, z);

    // --- IterateUntil ---
    /// <summary>
    /// Applies updateState until endCondition holds. The caller only supplies pure functions over immutable state;
    /// the loop itself is an ordinary while loop, so it uses no extra stack per step (see Trampoline for recursion).
    /// </summary>
    public static T IterateUntil<T>(this T initialState, Func<T, T> updateState, Func<T, bool> endCondition)
    {
        var currentState = initialState;
        while (!endCondition(currentState))
        {
            currentState = updateState(currentState);
        }
        return currentState;
    }

    /// <summary>
    /// Generates a lazy history sequence of every step in an indefinite functional loop until the end condition is met.
    /// </summary>
    public static IEnumerable<T> IterateStepsUntil<T>(this T initialState, Func<T, T> updateState, Func<T, bool> endCondition)
    {
        var currentState = initialState;
        yield return currentState;
        while (!endCondition(currentState))
        {
            currentState = updateState(currentState);
            yield return currentState;
        }
    }
}

/// <summary>
/// A hand-written IEnumerable/IEnumerator that applies a step function until a stop condition holds,
/// yielding every state. Used by the loop benchmark as the "custom iterator" variant.
/// </summary>
public sealed class TransitStepEnumerable<TState>(
    TState seed,
    Func<TState, TState> stepFunc,
    Func<TState, bool> isCompleted) : IEnumerable<TState>
{
    public IEnumerator<TState> GetEnumerator() =>
        new TransitStepEnumerator<TState>(seed, stepFunc, isCompleted);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class TransitStepEnumerator<TState> : IEnumerator<TState>
{
    private readonly TState _seed;
    private readonly Func<TState, TState> _stepFunc;
    private readonly Func<TState, bool> _isCompleted;
    private bool _started;
    private bool _finished;
    public TState Current { get; private set; }
    object? IEnumerator.Current => Current;

    public TransitStepEnumerator(
        TState seed,
        Func<TState, TState> stepFunc,
        Func<TState, bool> isCompleted)
    {
        _seed = seed;
        _stepFunc = stepFunc;
        _isCompleted = isCompleted;
        Current = seed;
    }

    public bool MoveNext()
    {
        if (_finished) return false;
        if (!_started)
        {
            _started = true;
            if (_isCompleted(Current))
            {
                _finished = true;
            }
            return true;
        }

        Current = _stepFunc(Current);
        if (_isCompleted(Current))
        {
            _finished = true;
        }
        return true;
    }

    public void Reset()
    {
        _started = false;
        _finished = false;
        Current = _seed;
    }

    public void Dispose() { }
}
