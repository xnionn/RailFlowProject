using RailFlow.Core.Domain;

namespace RailFlow.Tests;

/// <summary>
/// Input validation of the trip planner (a discriminated union, matched by the UI before the pipeline runs)
/// and the benchmark, whose approaches must all compute the same answer to be comparable.
/// </summary>
public sealed class PlannerValidationTests
{
    private static readonly CustomerOffering Single = new StandardDayTrip("Single ticket", "One-way rail fare", false);

    [Fact]
    public void ValidInput_BecomesValidRouteQuery_WithTrimmedStations()
    {
        var result = RailwayPipelineOrchestrator.ValidateInput("  LONDON ", "PARIS", 2, Single);

        var query = Assert.IsType<ValidRouteQuery>(result);
        Assert.Equal(("LONDON", "PARIS", 2), (query.Origin, query.Destination, query.Passengers));
    }

    [Theory]
    [InlineData(null, "PARIS", "Origin Station")]
    [InlineData("   ", "PARIS", "Origin Station")]
    [InlineData("LONDON", "", "Destination Station")]
    public void MissingStation_IsEmptyRouteQuery(string? origin, string? destination, string field)
    {
        var result = RailwayPipelineOrchestrator.ValidateInput(origin, destination, 1, Single);

        Assert.Equal(field, Assert.IsType<EmptyRouteQuery>(result).FieldName);
    }

    [Fact]
    public void SameStation_IsRejected_IgnoringCase()
    {
        var result = RailwayPipelineOrchestrator.ValidateInput("paris", "PARIS", 1, Single);

        Assert.IsType<SameStationError>(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(51)]
    public void PassengerCountOutsideOneToFifty_IsRejected(int passengers)
    {
        var result = RailwayPipelineOrchestrator.ValidateInput("LONDON", "PARIS", passengers, Single);

        Assert.Equal(passengers, Assert.IsType<InvalidPassengerCount>(result).AttemptedCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void PassengerCountAtTheLimits_IsAccepted(int passengers)
    {
        Assert.IsType<ValidRouteQuery>(RailwayPipelineOrchestrator.ValidateInput("LONDON", "PARIS", passengers, Single));
    }

    [Fact]
    public void Benchmark_AllApproachesComputeTheSameElevation()
    {
        var entries = BenchmarkAndLoopLab.RunElevationTelemetryBenchmark(3_000);

        Assert.Equal(6, entries.Count);
        // 3,000 characters, every third one is 'v': 2,000 up and 1,000 down.
        Assert.All(entries, e => Assert.Equal(1_000, e.ComputedValue.Value));
    }
}
