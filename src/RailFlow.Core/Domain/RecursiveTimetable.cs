using System.Collections.Immutable;
using RailFlow.Core.Functional;

namespace RailFlow.Core.Domain;

public sealed record TimetableStop(
    int StopIndex,
    string Station,
    TimeOnly Arrival,
    TimeOnly Departure,
    int DayOffset,
    string LineName,
    double CumulativeKm);

/// <summary>
/// Recursive route calculations.
/// The same recursive definition is shown twice: once as plain recursion (one stack frame per segment)
/// and once returning <see cref="Trampoline{T}"/>, which runs in constant stack space.
/// </summary>
public static class RecursiveTimetable
{
    public const int DwellMinutes = 5;

    /// <summary>
    /// Door-to-door minutes: running time plus a stop of <see cref="DwellMinutes"/> at every intermediate station.
    /// This is exactly the gap between the first departure and the last arrival in <see cref="BuildTimetable"/>.
    /// </summary>
    public static int JourneyMinutes(PlannedRoute route) =>
        route.Segments.Sum(s => s.BaseMinutes) + DwellMinutes * Math.Max(0, route.Segments.Count - 1);

    /// <summary>
    /// Plain recursion: base case = no segments left. Fine for real routes (a few segments),
    /// but a route with ~100,000 segments would overflow the call stack.
    /// </summary>
    public static double TotalDistanceRecursive(ImmutableList<RouteSegment> segments, int index = 0) =>
        index >= segments.Count
            ? 0.0
            : segments[index].DistanceKm + TotalDistanceRecursive(segments, index + 1);

    /// <summary>
    /// Same recursion in accumulator form, deferred through a trampoline thunk.
    /// </summary>
    public static Trampoline<double> TotalDistanceTrampolined(
        ImmutableList<RouteSegment> segments,
        int index = 0,
        double accumulator = 0.0) =>
        index >= segments.Count
            ? Trampoline.Return(Math.Round(accumulator, 1))
            : Trampoline.Suspend(() =>
                TotalDistanceTrampolined(segments, index + 1, accumulator + segments[index].DistanceKm));

    /// <summary>
    /// Builds the station-by-station timetable recursively: each call appends one stop and recurses on the rest.
    /// </summary>
    public static ImmutableList<TimetableStop> BuildTimetable(PlannedRoute route, TimeOnly departure)
    {
        var origin = new TimetableStop(
            StopIndex: 0,
            Station: route.Origin,
            Arrival: departure,
            Departure: departure,
            DayOffset: 0,
            LineName: route.Segments.FirstOrDefault()?.LineName ?? "-",
            CumulativeKm: 0.0);

        return BuildStops(route.Segments, 0, origin, ImmutableList.Create(origin)).Run();
    }

    private static Trampoline<ImmutableList<TimetableStop>> BuildStops(
        ImmutableList<RouteSegment> segments,
        int index,
        TimetableStop previous,
        ImmutableList<TimetableStop> acc)
    {
        if (index >= segments.Count)
            return Trampoline.Return(acc);

        var segment = segments[index];
        var isLast = index == segments.Count - 1;

        var arrival = previous.Departure.Add(TimeSpan.FromMinutes(segment.BaseMinutes), out var arrivalWrap);
        var departure = isLast
            ? arrival
            : arrival.Add(TimeSpan.FromMinutes(DwellMinutes), out _);

        var stop = new TimetableStop(
            StopIndex: index + 1,
            Station: segment.ToStation,
            Arrival: arrival,
            Departure: departure,
            DayOffset: previous.DayOffset + arrivalWrap,
            LineName: segment.LineName,
            CumulativeKm: Math.Round(previous.CumulativeKm + segment.DistanceKm, 1));

        return Trampoline.Suspend(() => BuildStops(segments, index + 1, stop, acc.Add(stop)));
    }

    /// <summary>
    /// Creates a synthetic route with many segments, used to prove the trampoline is stack-safe.
    /// </summary>
    public static ImmutableList<RouteSegment> SyntheticSegments(int count, double kmPerSegment = 1.5) =>
        Enumerable.Range(0, count)
            .Select(i => new RouteSegment($"S{i}", $"S{i + 1}", kmPerSegment, 1, "Synthetic"))
            .ToImmutableList();
}
