using System.Collections.Immutable;
using RailFlow.Core.Domain;
using RailFlow.Core.Functional;
using RailFlow.Infrastructure.Storage;

namespace RailFlow.Infrastructure.Services;

/// <summary>
/// Shared dispatcher settings for one server: stations with engineering works and the simulated service failures.
/// One instance is used by every user, so the station set is an immutable value that is swapped atomically.
/// </summary>
public sealed class OperationsControls
{
    private ImmutableHashSet<string> _stationsWithWorks =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "FRANKFURT", "LYON");

    public bool SimulateMappingServiceCrash { get; set; }
    public bool SimulateRoutePlannerCrash { get; set; }
    public bool SimulateTrafficMonitorCrash { get; set; }
    public bool SimulatePricingCrash { get; set; }
    public bool SimulatePaymentOutage { get; set; }
    public double EngineeringWorksMultiplier { get; set; } = 1.10; // fares +10% on routes with engineering works

    public event Action<string>? OnTrafficAlertBroadcast;

    public IReadOnlyCollection<string> StationsWithEngineeringWorks => Volatile.Read(ref _stationsWithWorks);

    public bool HasEngineeringWorksAt(string station) => Volatile.Read(ref _stationsWithWorks).Contains(station);

    public void ToggleEngineeringWorks(string station, string actorRole)
    {
        var normalized = station.Trim().ToUpperInvariant();
        var started = false;

        // Retries if another user changed the set at the same moment, so no toggle is lost.
        ImmutableInterlocked.Update(ref _stationsWithWorks, current =>
        {
            started = !current.Contains(normalized);
            return started ? current.Add(normalized) : current.Remove(normalized);
        });

        var name = StationNames.Display(normalized);
        OnTrafficAlertBroadcast?.Invoke(started
            ? $"{actorRole}: engineering works started at {name}; connections to {name} cost +{(EngineeringWorksMultiplier - 1) * 100:0}%."
            : $"{actorRole}: engineering works at {name} finished.");
    }

    public void BroadcastCustomAdvisory(string message)
    {
        OnTrafficAlertBroadcast?.Invoke(message);
    }
}

/// <summary>
/// Concrete IMappingSystem over the station list loaded from CSV (see NetworkDataLoader).
/// </summary>
public sealed class RailMappingSystem : IMappingSystem
{
    private readonly ImmutableDictionary<string, GeoCoord> _stations;
    private readonly OperationsControls _operations;

    public RailMappingSystem(OperationsControls operations)
        : this(operations, EmbeddedNetwork.Dataset)
    {
    }

    public RailMappingSystem(OperationsControls operations, NetworkDataset dataset)
    {
        _operations = operations;
        _stations = dataset.Stations
            .GroupBy(s => s.StationCode, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public Maybe<GeoCoord> FindLocation(string placeName)
    {
        if (_operations.SimulateMappingServiceCrash)
        {
            return new UnhandledError<GeoCoord>(
                new InvalidOperationException("Station lookup service did not respond (simulated failure)."));
        }

        if (string.IsNullOrWhiteSpace(placeName))
            return new UnhandledNothing<GeoCoord>();

        return _stations.TryGetValue(placeName.Trim(), out var coord)
            ? new Something<GeoCoord>(coord)
            : new UnhandledNothing<GeoCoord>();
    }

    public IReadOnlyList<GeoCoord> GetAllStations() =>
        _stations.Values.OrderBy(s => s.StationCode).ToList();
}

/// <summary>
/// Concrete IRoutePlanner backed by the Memoized BFS TransitGraphEngine.
/// </summary>
public sealed class RailRoutePlanner : IRoutePlanner
{
    private readonly TransitGraphEngine _graphEngine;
    private readonly OperationsControls _operations;

    public RailRoutePlanner(OperationsControls operations)
        : this(operations, EmbeddedNetwork.Dataset)
    {
    }

    public RailRoutePlanner(OperationsControls operations, NetworkDataset dataset)
    {
        _operations = operations;
        _graphEngine = new TransitGraphEngine(dataset.Connections);
    }

    public TransitGraphEngine GraphEngine => _graphEngine;

    public Maybe<PlannedRoute> PlanRoute(GeoCoord start, GeoCoord end)
    {
        if (_operations.SimulateRoutePlannerCrash)
        {
            return new UnhandledError<PlannedRoute>(
                new TimeoutException("Route planner timed out (simulated failure)."));
        }

        return _graphEngine
            .FindShortestPathMemoized(start.StationCode, end.StationCode)
            .Map(bfs => new PlannedRoute(
                Origin: start.StationCode,
                Destination: end.StationCode,
                Segments: bfs.Segments,
                TotalDistanceKm: bfs.TotalDistanceKm,
                EstimatedMinutes: bfs.TotalMinutes));
    }
}

/// <summary>
/// Concrete ITrafficMonitor inspecting intermediate stations for active engineering works/track maintenance.
/// </summary>
public sealed class RailTrafficMonitor(OperationsControls operations) : ITrafficMonitor
{
    public Maybe<RouteWithTraffic> CheckEngineeringWorks(PlannedRoute route)
    {
        if (operations.SimulateTrafficMonitorCrash)
        {
            return new UnhandledError<RouteWithTraffic>(
                new HttpRequestException("Engineering works feed is unavailable (simulated failure)."));
        }

        // A section is affected when either of its stations has engineering works.
        var affectedSections = route.Segments
            .Where(s => operations.HasEngineeringWorksAt(s.FromStation) || operations.HasEngineeringWorksAt(s.ToStation))
            .ToList();

        if (affectedSections.Count > 0)
        {
            var multiplier = operations.EngineeringWorksMultiplier;
            var stations = affectedSections
                .SelectMany(s => new[] { s.FromStation, s.ToStation })
                .Where(operations.HasEngineeringWorksAt)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(StationNames.Display);
            var affectedKm = affectedSections.Sum(s => s.DistanceKm);
            var sections = string.Join(", ", affectedSections.Select(s =>
                $"{StationNames.Display(s.FromStation)} → {StationNames.Display(s.ToStation)}"));

            return new Something<RouteWithTraffic>(new RouteWithTraffic(
                Route: route,
                HasEngineeringWorks: true,
                FareMultiplier: multiplier,
                TrafficAdvisory: $"Engineering works at {string.Join(", ", stations)}: +{(multiplier - 1.0) * 100:F0}% on {sections} ({affectedKm:N0} km).",
                AffectedDistanceKm: affectedKm));
        }

        return new Something<RouteWithTraffic>(new RouteWithTraffic(
            Route: route,
            HasEngineeringWorks: false,
            FareMultiplier: 1.0,
            TrafficAdvisory: "No engineering works on this route."));
    }
}

/// <summary>
/// IPricingCalculator over the pure <see cref="Tariff"/>. Its only job besides that is the dispatcher's
/// "Fare calculation fails" switch, which makes it return an Error value instead of a price.
/// </summary>
public sealed class RailPricingCalculator(OperationsControls operations) : IPricingCalculator
{
    public Maybe<PricedItinerary> CalculatePrice(RouteWithTraffic route, CustomerOffering offering, int passengers)
    {
        if (operations.SimulatePricingCrash)
            return new UnhandledError<PricedItinerary>(
                new InvalidOperationException("Fare calculation service failed (simulated failure)."));

        return passengers <= 0
            ? new UnhandledNothing<PricedItinerary>()
            : new Something<PricedItinerary>(Tariff.Price(route, offering, passengers));
    }
}
