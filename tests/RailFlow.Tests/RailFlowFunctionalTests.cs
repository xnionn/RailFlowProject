using RailFlow.Core.Domain;
using RailFlow.Core.Functional;
using RailFlow.Infrastructure.Services;

namespace RailFlow.Tests;

public class RailFlowFunctionalTests
{
    // --- 1. Lecture L6_1: Three Formal Monad Laws ---

    [Fact]
    public void MonadLaw1_LeftIdentity_Holds()
    {
        // return x >>= f  ===  f(x)
        int x = 42;
        Func<int, Maybe<int>> f = v => new Something<int>(v * 2);

        var left = x.ToMaybe().Bind(f);
        var right = f(x);

        Assert.Equal(right, left);
    }

    [Fact]
    public void MonadLaw2_RightIdentity_Holds()
    {
        // m >>= return  ===  m
        Maybe<string> m = "LONDON".ToMaybe();

        var transformed = m.Bind(x => x.ToMaybe());

        Assert.Equal(m, transformed);
    }

    [Fact]
    public void MonadLaw3_Associativity_Holds()
    {
        // (m >>= f) >>= g  ===  m >>= (x => f(x) >>= g)
        Maybe<int> m = 10.ToMaybe();
        Func<int, Maybe<int>> f = x => (x + 5).ToMaybe();
        Func<int, Maybe<int>> g = y => (y * 3).ToMaybe();

        var leftSide = m.Bind(f).Bind(g);
        var rightSide = m.Bind(x => f(x).Bind(g));

        Assert.Equal(leftSide, rightSide);
        Assert.Equal(45, leftSide.Value);
    }

    // --- 2. Lecture L6_1: Value-Type default(T) Pitfall & UnhandledError Transition ---

    [Fact]
    public void Maybe_PrimitiveZeroAndFalse_AreTreatedAsSomething_NotNothing()
    {
        // Lecture L6_1 highlights the classic C# bug where `x == default(T)` treats 0 as Nothing.
        var zeroMaybe = 0.ToMaybe().Map(x => x + 100);
        var falseMaybe = false.ToMaybe().Map(b => !b);

        var sZero = Assert.IsType<Something<int>>(zeroMaybe);
        Assert.Equal(100, sZero.InnerValue);

        var sFalse = Assert.IsType<Something<bool>>(falseMaybe);
        Assert.True(sFalse.InnerValue);
    }

    [Fact]
    public void OnError_And_OnNothing_PreventDuplicateLoggingAcrossChains()
    {
        int errorLogCount = 0;
        Maybe<int> initial = new Something<int>(10);

        var final = initial
            .Map<int, int>(_ => throw new InvalidOperationException("Boom"))
            .OnError(_ => errorLogCount++)
            .Map(x => x * 2)
            .OnError(_ => errorLogCount++); // Should NOT fire a second time because UnhandledError became Error!

        Assert.IsType<Error<int>>(final);
        Assert.Equal(1, errorLogCount);
    }

    [Fact]
    public async Task BindAsync_ComposesTaskMaybesCleanly()
    {
        Task<Maybe<int>> Step1(int x) => Task.FromResult<Maybe<int>>(new Something<int>(x + 10));
        Task<Maybe<int>> Step2(int y) => Task.FromResult<Maybe<int>>(new Something<int>(y * 2));

        var outcome = await Step1(5).BindAsync(Step2);

        var s = Assert.IsType<Something<int>>(outcome);
        Assert.Equal(30, s.InnerValue);
    }

    // --- 3. Lecture L4_1: Higher-Order Combinators (Fork, Alt, Compose, Transduce) ---

    [Fact]
    public void ForkCombinator_ComputesAverageInSingleDeclarativeExpression()
    {
        var scores = new[] { 10.0, 20.0, 30.0, 40.0 };

        double average = scores.Fork(
            s => s.Sum(),
            s => s.Count(),
            (sum, count) => sum / count);

        Assert.Equal(25.0, average);
    }

    [Fact]
    public void ComposeAndTransduceCombinators_FusePipeline()
    {
        Func<int, int> doubleIt = x => x * 2;
        Func<int, string> formatIt = x => $"Val={x}";
        var composed = doubleIt.Compose(formatIt);

        Assert.Equal("Val=14", composed(7));

        Func<IEnumerable<int>, IEnumerable<int>> filterEvens = seq => seq.Where(n => n % 2 == 0);
        Func<IEnumerable<int>, int> sumAll = seq => seq.Sum();
        var transduced = filterEvens.Transduce(sumAll);

        Assert.Equal(12, transduced(new[] { 1, 2, 3, 4, 5, 6 }));
    }

    // --- 4. Lecture L7_1: Currying, Partial Application, Trampoline & Custom IEnumerable ---

    [Fact]
    public void CurriedAndPartialCsvParser_ParsesTrimmedImmutableGrid()
    {
        var csv = "LONDON , 460 \n PARIS , 310 ";
        var parsed = BenchmarkAndLoopLab.StandardCsvParser(csv);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("LONDON", parsed[0][0]);
        Assert.Equal("460", parsed[0][1]);

        var curriedResult = BenchmarkAndLoopLab.CurriedCsvParser("\n")(",")(true)(csv);
        Assert.Equal(parsed[0][0], curriedResult[0][0]);
    }

    [Fact]
    public void IterateUntil_Trampoline_And_CustomEnumerable_ExecuteWithoutStackOverflow()
    {
        var finalState = 0.IterateUntil(x => x + 1, x => x >= 10_000);
        Assert.Equal(10_000, finalState);

        var customSeq = new TransitStepEnumerable<int>(1, x => x * 2, x => x >= 64).ToList();
        Assert.Equal(new[] { 1, 2, 4, 8, 16, 32, 64 }, customSeq);
    }

    // --- 5. Lecture L8_1: BFS Graph Search & Thread-Safe Memoization ---

    [Fact]
    public void TransitGraphEngine_FindsShortestPathAndCachesRepeatedLookups()
    {
        var chaos = new OperationsControls();
        var planner = new RailRoutePlanner(chaos);
        planner.GraphEngine.ClearCache();

        var firstLookup = planner.GraphEngine.FindShortestPathMemoized("LONDON", "ZURICH");
        var statsAfterFirst = planner.GraphEngine.GetCacheStats();

        var secondLookup = planner.GraphEngine.FindShortestPathMemoized("LONDON", "ZURICH");
        var statsAfterSecond = planner.GraphEngine.GetCacheStats();

        var path = Assert.IsType<Something<BfsPathResult>>(firstLookup).InnerValue!;
        Assert.Equal("LONDON", path.StartStation);
        Assert.Equal("ZURICH", path.TargetStation);
        Assert.True(path.DegreesOfSeparation >= 2);

        Assert.Equal(0, statsAfterFirst.CacheHits);
        Assert.Equal(1, statsAfterFirst.CacheMisses);
        Assert.Equal(1, statsAfterSecond.CacheHits);
        Assert.Equal(1, statsAfterSecond.CacheMisses);
        Assert.Equal(secondLookup.Value, firstLookup.Value);
    }

    // --- 6. End-to-End Worked Route-Pricing Pipeline & State Container (Lectures L5_1, L6_1, L9_1) ---

    [Fact]
    public void EndToEndPipeline_AppliesEngineeringWorksAndCustomerOfferingUnion()
    {
        var chaos = new OperationsControls(); // LYON & FRANKFURT have engineering works by default (1.10x)
        var mapping = new RailMappingSystem(chaos);
        var planner = new RailRoutePlanner(chaos);
        var traffic = new RailTrafficMonitor(chaos);
        var pricing = new RailPricingCalculator(chaos);
        var orchestrator = new RailwayPipelineOrchestrator(mapping, planner, traffic, pricing);

        var offering = new RailHolidayWithMeals(
            "Alpine Gourmet",
            "4 Nights + Dining",
            new HotelNights(4),
            HotelTier.Boutique,
            "Full Board",
            true);

        var report = orchestrator.ExecutePipeline("LONDON", "ZURICH", offering, passengers: 2);

        var itinerary = Assert.IsType<Something<PricedItinerary>>(report.Outcome).InnerValue!;
        Assert.True(itinerary.TrafficRoute.HasEngineeringWorks);
        Assert.True(itinerary.EngineeringWorksSurcharge > 0m);
        Assert.True(itinerary.FinalPrice > itinerary.BaseFare);
        Assert.Equal(4, report.Diagnostics.Count);
    }

    [Fact]
    public void EndToEndPipeline_ShortCircuitsOnUnknownStationOrSimulatedFailure()
    {
        var chaos = new OperationsControls();
        var mapping = new RailMappingSystem(chaos);
        var planner = new RailRoutePlanner(chaos);
        var traffic = new RailTrafficMonitor(chaos);
        var pricing = new RailPricingCalculator(chaos);
        var orchestrator = new RailwayPipelineOrchestrator(mapping, planner, traffic, pricing);
        var offering = new StandardDayTrip("Single", "Direct", false);

        // 1. Unknown station -> Nothing<PricedItinerary>
        var nothingReport = orchestrator.ExecutePipeline("LONDON", "ATLANTIS", offering, 1);
        Assert.IsAssignableFrom<Nothing<PricedItinerary>>(nothingReport.Outcome);
        Assert.Equal(StepStatus.Skipped, nothingReport.Diagnostics[1].Status);

        // 2. Simulated failure of the engineering-works monitor -> Error<PricedItinerary>
        chaos.SimulateTrafficMonitorCrash = true;
        var errorReport = orchestrator.ExecutePipeline("LONDON", "PARIS", offering, 1);
        Assert.IsAssignableFrom<Error<PricedItinerary>>(errorReport.Outcome);
        Assert.Equal(StepStatus.Error, errorReport.Diagnostics[2].Status);
        Assert.Equal(StepStatus.Skipped, errorReport.Diagnostics[3].Status);
    }

    [Fact]
    public void ImmutableAppStateContainer_SupportsTimeTravelUndoAndRedo()
    {
        var state = new ImmutableAppStateContainer();
        var initialOrigin = state.CurrentSnapshot.Origin;

        state.CommitSnapshot(new PlannerFormSnapshot("PARIS", "MILAN", 3, "DayTripSingle", DateTime.UtcNow));
        Assert.Equal("PARIS", state.CurrentSnapshot.Origin);
        Assert.True(state.CanUndo);

        state.Undo();
        Assert.Equal(initialOrigin, state.CurrentSnapshot.Origin);
        Assert.True(state.CanRedo);

        state.Redo();
        Assert.Equal("PARIS", state.CurrentSnapshot.Origin);
    }
}
