namespace RailFlow.Core.Functional;

/// <summary>
/// Trampoline for deferred recursive computation.
/// A recursive function returns either the final result (<see cref="Done{T}"/>)
/// or a thunk describing the next recursive call (<see cref="More{T}"/>).
/// <see cref="Trampoline.Run{T}"/> unrolls the thunks in a loop, so the call stack never grows.
/// </summary>
public abstract record Trampoline<T>;

public sealed record Done<T>(T Result) : Trampoline<T>;

public sealed record More<T>(Func<Trampoline<T>> Next) : Trampoline<T>;

public static class Trampoline
{
    public static Trampoline<T> Return<T>(T result) => new Done<T>(result);

    public static Trampoline<T> Suspend<T>(Func<Trampoline<T>> next) => new More<T>(next);

    public static T Run<T>(this Trampoline<T> trampoline)
    {
        var current = trampoline;
        while (current is More<T> more)
        {
            current = more.Next();
        }

        return ((Done<T>)current).Result;
    }
}
