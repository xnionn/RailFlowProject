using System.Collections.Immutable;
using RailFlow.Core.Functional;

namespace RailFlow.Core.Domain;

public sealed record PipelineStepDiagnostic(
    string StepName,
    string Status, // "Something", "Nothing", "Error", "Skipped"
    string DetailMessage,
    double DurationMs);

public sealed record PipelineExecutionReport(
    Maybe<PricedItinerary> Outcome,
    ImmutableList<PipelineStepDiagnostic> Diagnostics,
    ImmutableList<string> SideEffectLogs);

/// <summary>
/// Implements the Chapter 17 Worked Route-Pricing Pipeline from Lecture L6_1
/// using Railway-Oriented Programming (Bind, Map, OnSomething, OnNothing, OnError).
/// </summary>
public sealed class RailwayPipelineOrchestrator(
    IMappingSystem mappingSystem,
    IRoutePlanner routePlanner,
    ITrafficMonitor trafficMonitor,
    IPricingCalculator pricingCalculator)
{
    /// <summary>
    /// Validates raw user inputs into a ValidatedUserInput Discriminated Union (Lecture L5_1).
    /// </summary>
    public static ValidatedUserInput ValidateInput(
        string? origin,
        string? destination,
        int passengers,
        CustomerOffering offering) =>
        (origin?.Trim(), destination?.Trim(), passengers) switch
        {
            (null or "", _, _) => new EmptyRouteQuery("Origin Station"),
            (_, null or "", _) => new EmptyRouteQuery("Destination Station"),
            (var o, var d, _) when string.Equals(o, d, StringComparison.OrdinalIgnoreCase) =>
                new SameStationError(o),
            (_, _, <= 0 or > 50) => new InvalidPassengerCount(passengers),
            (var o, var d, var p) => new ValidRouteQuery(o, d, p, offering)
        };

    /// <summary>
    /// Pure Railway-Oriented composition directly matching Lecture L6_1 slide 42:
    /// FindLocation(start) -> Bind(PlanRoute) -> Bind(CheckForRoadworks) -> Bind(CalculatePrice)
    /// with OnSomething / OnNothing / OnError side-effect handlers.
    /// </summary>
    public PipelineExecutionReport ExecutePipeline(
        string startPlace,
        string endPlace,
        CustomerOffering offering,
        int passengers = 1)
    {
        var logs = new List<string>();
        var diagnostics = new List<PipelineStepDiagnostic>();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Step 1: Resolve Start & End Coordinates via IMappingSystem
        var t0 = sw.Elapsed.TotalMilliseconds;
        var startMaybe = mappingSystem.FindLocation(startPlace);
        var endMaybe = mappingSystem.FindLocation(endPlace);

        var coordsMaybe = startMaybe.Bind(s => endMaybe.Map(e => (Start: s, End: e)));
        var t1 = sw.Elapsed.TotalMilliseconds;

        RecordStep("1. IMappingSystem.FindLocation", coordsMaybe,
            c => $"Resolved {c.Start.StationCode} ({c.Start.Latitude:F2}, {c.Start.Longitude:F2}) -> {c.End.StationCode} ({c.End.Latitude:F2}, {c.End.Longitude:F2})",
            t1 - t0, diagnostics);

        // Step 2: Plan Route via IRoutePlanner
        var t2Start = sw.Elapsed.TotalMilliseconds;
        var plannedMaybe = coordsMaybe.Bind(c => routePlanner.PlanRoute(c.Start, c.End));
        var t2End = sw.Elapsed.TotalMilliseconds;

        RecordStep("2. IRoutePlanner.PlanRoute", plannedMaybe,
            r => $"{r.Segments.Count} segment(s), {r.TotalDistanceKm:F1} km, ~{r.EstimatedMinutes} min",
            t2End - t2Start, diagnostics, skipped: coordsMaybe is not Something<(GeoCoord, GeoCoord)>);

        // Step 3: Check Traffic / Roadworks via ITrafficMonitor
        var t3Start = sw.Elapsed.TotalMilliseconds;
        var trafficMaybe = plannedMaybe.Bind(trafficMonitor.CheckForRoadworks);
        var t3End = sw.Elapsed.TotalMilliseconds;

        RecordStep("3. ITrafficMonitor.CheckForRoadworks", trafficMaybe,
            t => t.HasRoadworksOrTrackMaintenance
                ? $"Roadworks Active (x{t.DelaySeverityMultiplier:F2}): {t.TrafficAdvisory}"
                : $"Clear Track (x1.00): {t.TrafficAdvisory}",
            t3End - t3Start, diagnostics, skipped: plannedMaybe is not Something<PlannedRoute>);

        // Step 4: Calculate Final Price via IPricingCalculator
        var t4Start = sw.Elapsed.TotalMilliseconds;
        var priceMaybe = trafficMaybe
            .Bind(rt => pricingCalculator.CalculatePrice(rt, offering, passengers))
            .OnSomething(p => logs.Add($"[SUCCESS] Priced itinerary {p.TrafficRoute.Route.Origin} -> {p.TrafficRoute.Route.Destination}: {p.Currency}{p.FinalPrice:F2}"))
            .OnNothing(() => logs.Add($"[NOTHING] Route or station lookup returned Nothing for '{startPlace}' -> '{endPlace}'."))
            .OnError(ex => logs.Add($"[ERROR] Unhandled fault caught in monad pipeline: {ex.Message}"));
        var t4End = sw.Elapsed.TotalMilliseconds;

        RecordStep("4. IPricingCalculator.CalculatePrice", priceMaybe,
            p => $"Final Fare: {p.Currency}{p.FinalPrice:F2} (Base: {p.Currency}{p.BaseFare:F2}, Roadworks: +{p.Currency}{p.RoadworksSurcharge:F2}, Package: +{p.Currency}{p.OfferingAddon:F2})",
            t4End - t4Start, diagnostics, skipped: trafficMaybe is not Something<RouteWithTraffic>);

        return new PipelineExecutionReport(
            priceMaybe,
            diagnostics.ToImmutableList(),
            logs.ToImmutableList());
    }

    private static void RecordStep<T>(
        string stepName,
        Maybe<T> result,
        Func<T, string> formatter,
        double durationMs,
        List<PipelineStepDiagnostic> list,
        bool skipped = false)
    {
        if (skipped)
        {
            list.Add(new PipelineStepDiagnostic(stepName, "Skipped", "Short-circuited on bypass track", 0.0));
            return;
        }

        var (status, detail) = result switch
        {
            Something<T> { InnerValue: { } val } => ("Something", formatter(val)),
            Nothing<T> => ("Nothing", "No matching data found (Nothing<T>)"),
            Error<T> { ErrorDetails: var ex } => ("Error", $"Fault captured: {ex.Message}"),
            _ => ("Nothing", "Empty state")
        };

        list.Add(new PipelineStepDiagnostic(stepName, status, detail, Math.Round(durationMs, 3)));
    }
}
