using System.Collections.Immutable;
using RailFlow.Core.Domain;
using RailFlow.Core.Functional;
using RailFlow.Infrastructure.Services;

namespace RailFlow.Tests;

/// <summary>
/// The step-by-step simulation, the timetable and the result card must tell the same story,
/// and the engineering-works surcharge must only apply to the affected part of the journey.
/// </summary>
public sealed class SimulationAndPricingTests
{
    // LONDON -(120 min, 460 km)- PARIS -(110 min, 400 km)- LYON
    private static readonly PlannedRoute TwoLegs = new(
        "LONDON",
        "LYON",
        ImmutableList.Create(
            new RouteSegment("LONDON", "PARIS", 460, 120, "Eurostar"),
            new RouteSegment("PARIS", "LYON", 400, 110, "TGV")),
        TotalDistanceKm: 860,
        EstimatedMinutes: 230);

    [Fact]
    public void JourneyMinutes_IncludesOneStopPerIntermediateStation()
    {
        Assert.Equal(230 + RecursiveTimetable.DwellMinutes, RecursiveTimetable.JourneyMinutes(TwoLegs));
    }

    [Fact]
    public void JourneyMinutes_MatchesTheTimetable()
    {
        var timetable = RecursiveTimetable.BuildTimetable(TwoLegs, new TimeOnly(8, 0));

        var minutes = (int)(timetable[^1].Arrival - timetable[0].Departure).TotalMinutes;

        Assert.Equal(RecursiveTimetable.JourneyMinutes(TwoLegs), minutes);
    }

    [Theory]
    [InlineData(60, 230.0, "LONDON", "On Eurostar towards Paris")]
    [InlineData(122, 460.0, "PARIS", "Stopped at Paris")]
    [InlineData(180, 660.0, "PARIS", "On TGV towards Lyon")]
    [InlineData(235, 860.0, "LYON", "Arrived at Lyon")]
    public void PositionAt_UsesSectionSpeedsAndStops(int minutes, double km, string lastStation, string description)
    {
        var (actualKm, actualStation, actualDescription) = BenchmarkAndLoopLab.PositionAt(TwoLegs, minutes);

        Assert.Equal(km, actualKm, precision: 1);
        Assert.Equal(lastStation, actualStation);
        Assert.Equal(description, actualDescription);
    }

    [Fact]
    public void Simulation_ArrivesExactlyWhenTheTimetableSays()
    {
        var steps = BenchmarkAndLoopLab.RunTransitSimulation(TwoLegs, stepMinutes: 20);

        var last = steps[^1];
        Assert.True(last.ReachedDestination);
        Assert.Equal(RecursiveTimetable.JourneyMinutes(TwoLegs), last.AccumulatedMinutes);
        Assert.Equal(860, last.ProgressKm);
        Assert.Equal("LYON", last.CurrentStation);
        // Distance never goes backwards.
        Assert.True(steps.Zip(steps.Skip(1)).All(p => p.Second.ProgressKm >= p.First.ProgressKm));
    }

    [Fact]
    public void Surcharge_AppliesOnlyToAffectedKilometres()
    {
        var chaos = new ChaosAndTrafficController();
        var pricing = new RailPricingCalculator(chaos);
        var withWorks = new RouteWithTraffic(TwoLegs, true, 1.10, "works", AffectedDistanceKm: 400);

        var priced = Assert.IsType<Something<PricedItinerary>>(
            pricing.CalculatePrice(withWorks, new StandardDayTrip("Single", "One way", false), passengers: 1)).InnerValue!;

        // Base fare 860 km x 0.22 = 189.20; surcharge 10% of 400 km x 0.22 = 8.80.
        Assert.Equal(189.20m, priced.BaseFare);
        Assert.Equal(8.80m, priced.RoadworksSurcharge);
        Assert.Equal(198.00m, priced.FinalPrice);
    }

    [Fact]
    public void Monitor_MarksOnlySectionsTouchingAStationWithWorks()
    {
        var chaos = new ChaosAndTrafficController(); // works at FRANKFURT and LYON by default
        var monitor = new RailTrafficMonitor(chaos);

        var checkedRoute = Assert.IsType<Something<RouteWithTraffic>>(monitor.CheckForRoadworks(TwoLegs)).InnerValue!;

        Assert.True(checkedRoute.HasRoadworksOrTrackMaintenance);
        Assert.Equal(400, checkedRoute.AffectedDistanceKm);
        Assert.Contains("Paris → Lyon", checkedRoute.TrafficAdvisory);
        Assert.DoesNotContain("London → Paris", checkedRoute.TrafficAdvisory);
    }
}
