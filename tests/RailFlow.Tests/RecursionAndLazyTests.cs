using System.Collections.Immutable;
using RailFlow.Core.Domain;
using RailFlow.Core.Functional;

namespace RailFlow.Tests;

/// <summary>
/// Lecture 9 (recursion and trampolining) and Week 8 (lazy operators / deferred execution).
/// </summary>
public class RecursionAndLazyTests
{
    private static readonly PlannedRoute LondonToZurich = new(
        "LONDON",
        "ZURICH",
        ImmutableList.Create(
            new RouteSegment("LONDON", "PARIS", 460, 135, "Eurostar"),
            new RouteSegment("PARIS", "LYON", 465, 118, "TGV"),
            new RouteSegment("LYON", "ZURICH", 415, 215, "TGV Lyria")),
        1340,
        468);

    [Fact]
    public void TrampolinedRecursion_GivesTheSameResultAsPlainRecursion()
    {
        var plain = RecursiveTimetable.TotalDistanceRecursive(LondonToZurich.Segments);
        var trampolined = RecursiveTimetable.TotalDistanceTrampolined(LondonToZurich.Segments).Run();

        Assert.Equal(1340.0, plain);
        Assert.Equal(plain, trampolined);
    }

    [Fact]
    public void TrampolinedRecursion_IsStackSafe_For200000Segments()
    {
        var segments = RecursiveTimetable.SyntheticSegments(200_000, kmPerSegment: 1.0);

        var total = RecursiveTimetable.TotalDistanceTrampolined(segments).Run();

        Assert.Equal(200_000.0, total);
    }

    [Fact]
    public void Timetable_AddsTravelTimeAndDwellTime_StopByStop()
    {
        var stops = RecursiveTimetable.BuildTimetable(LondonToZurich, new TimeOnly(8, 0));

        Assert.Equal(new[] { "LONDON", "PARIS", "LYON", "ZURICH" }, stops.Select(s => s.Station));
        Assert.Equal(new TimeOnly(10, 15), stops[1].Arrival);      // 08:00 + 135 min
        Assert.Equal(new TimeOnly(10, 20), stops[1].Departure);    // + 5 min dwell
        Assert.Equal(new TimeOnly(12, 18), stops[2].Arrival);      // 10:20 + 118 min
        Assert.Equal(new TimeOnly(15, 58), stops[3].Arrival);      // 12:23 + 215 min
        Assert.Equal(1340.0, stops[3].CumulativeKm);
    }

    [Fact]
    public void Timetable_TracksDayRollover_ForLateDepartures()
    {
        var stops = RecursiveTimetable.BuildTimetable(LondonToZurich, new TimeOnly(22, 30));

        Assert.Equal(0, stops[0].DayOffset);
        Assert.Equal(1, stops[^1].DayOffset);
    }

    [Fact]
    public void Filter_IsLazy_AndReEvaluatesOnEveryEnumeration()
    {
        var calls = 0;
        var amounts = new List<decimal> { 50m, 200m, 1200m };

        var big = amounts.Filter(a => { calls++; return a > 100m; });
        Assert.Equal(0, calls); // nothing has run yet

        Assert.Equal(2, big.Count());
        Assert.Equal(3, calls);

        amounts.Add(500m);
        Assert.Equal(3, big.Count()); // the new item is seen: the query runs again
        Assert.Equal(7, calls);
    }

    [Fact]
    public void Transform_ProjectsLikeSelect()
    {
        var labels = new[] { 1, 2, 3 }.Filter(x => x != 2).Transform(x => $"#{x}");

        Assert.Equal(new[] { "#1", "#3" }, labels);
    }
}
