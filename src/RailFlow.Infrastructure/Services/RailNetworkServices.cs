using System.Collections.Immutable;
using RailFlow.Core.Domain;
using RailFlow.Core.Functional;
using RailFlow.Infrastructure.Storage;

namespace RailFlow.Infrastructure.Services;

/// <summary>
/// Shared dispatcher settings for one server: stations with engineering works and the simulated service failures.
/// One instance is used by every user, so the station set is an immutable value that is swapped atomically.
/// </summary>
public sealed class ChaosAndTrafficController
{
    private ImmutableHashSet<string> _roadworksStations =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "FRANKFURT", "LYON");

    public bool SimulateMappingServiceCrash { get; set; }
    public bool SimulateRoutePlannerCrash { get; set; }
    public bool SimulateTrafficMonitorCrash { get; set; }
    public bool SimulatePricingCrash { get; set; }
    public bool SimulatePaymentOutage { get; set; }
    public double RoadworksMultiplier { get; set; } = 1.10; // fares +10% on routes with engineering works

    public event Action<string>? OnTrafficAlertBroadcast;

    public IReadOnlyCollection<string> ActiveRoadworksStations => Volatile.Read(ref _roadworksStations);

    public bool HasRoadworksAt(string station) => Volatile.Read(ref _roadworksStations).Contains(station);

    public void ToggleRoadworks(string station, string actorRole)
    {
        var normalized = station.Trim().ToUpperInvariant();
        var started = false;

        // Retries if another user changed the set at the same moment, so no toggle is lost.
        ImmutableInterlocked.Update(ref _roadworksStations, current =>
        {
            started = !current.Contains(normalized);
            return started ? current.Add(normalized) : current.Remove(normalized);
        });

        var name = StationNames.Display(normalized);
        OnTrafficAlertBroadcast?.Invoke(started
            ? $"{actorRole}: engineering works started at {name}; connections to {name} cost +{(RoadworksMultiplier - 1) * 100:0}%."
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
    private readonly ChaosAndTrafficController _chaos;

    public RailMappingSystem(ChaosAndTrafficController chaos)
        : this(chaos, EmbeddedNetwork.Dataset)
    {
    }

    public RailMappingSystem(ChaosAndTrafficController chaos, NetworkDataset dataset)
    {
        _chaos = chaos;
        _stations = dataset.Stations
            .GroupBy(s => s.StationCode, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public Maybe<GeoCoord> FindLocation(string placeName)
    {
        if (_chaos.SimulateMappingServiceCrash)
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
    private readonly ChaosAndTrafficController _chaos;

    public RailRoutePlanner(ChaosAndTrafficController chaos)
        : this(chaos, EmbeddedNetwork.Dataset)
    {
    }

    public RailRoutePlanner(ChaosAndTrafficController chaos, NetworkDataset dataset)
    {
        _chaos = chaos;
        _graphEngine = new TransitGraphEngine(dataset.Connections);
    }

    public TransitGraphEngine GraphEngine => _graphEngine;

    public Maybe<PlannedRoute> PlanRoute(GeoCoord start, GeoCoord end)
    {
        if (_chaos.SimulateRoutePlannerCrash)
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
/// Concrete ITrafficMonitor inspecting intermediate stations for active roadworks/track maintenance.
/// </summary>
public sealed class RailTrafficMonitor(ChaosAndTrafficController chaos) : ITrafficMonitor
{
    public Maybe<RouteWithTraffic> CheckForRoadworks(PlannedRoute route)
    {
        if (chaos.SimulateTrafficMonitorCrash)
        {
            return new UnhandledError<RouteWithTraffic>(
                new HttpRequestException("Engineering works feed is unavailable (simulated failure)."));
        }

        // A section is affected when either of its stations has engineering works.
        var affectedSections = route.Segments
            .Where(s => chaos.HasRoadworksAt(s.FromStation) || chaos.HasRoadworksAt(s.ToStation))
            .ToList();

        if (affectedSections.Count > 0)
        {
            var multiplier = chaos.RoadworksMultiplier;
            var stations = affectedSections
                .SelectMany(s => new[] { s.FromStation, s.ToStation })
                .Where(chaos.HasRoadworksAt)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(StationNames.Display);
            var affectedKm = affectedSections.Sum(s => s.DistanceKm);
            var sections = string.Join(", ", affectedSections.Select(s =>
                $"{StationNames.Display(s.FromStation)} → {StationNames.Display(s.ToStation)}"));

            return new Something<RouteWithTraffic>(new RouteWithTraffic(
                Route: route,
                HasRoadworksOrTrackMaintenance: true,
                DelaySeverityMultiplier: multiplier,
                TrafficAdvisory: $"Engineering works at {string.Join(", ", stations)}: +{(multiplier - 1.0) * 100:F0}% on {sections} ({affectedKm:N0} km).",
                AffectedDistanceKm: affectedKm));
        }

        return new Something<RouteWithTraffic>(new RouteWithTraffic(
            Route: route,
            HasRoadworksOrTrackMaintenance: false,
            DelaySeverityMultiplier: 1.0,
            TrafficAdvisory: "No engineering works on this route."));
    }
}

/// <summary>
/// Concrete IPricingCalculator: a per-km base fare, the engineering-works surcharge on the affected kilometres only,
/// and the package price chosen by pattern matching over the CustomerOffering union.
/// </summary>
public sealed class RailPricingCalculator(ChaosAndTrafficController chaos) : IPricingCalculator
{
    private const decimal BaseRatePerKm = 0.22m;

    public Maybe<PricedItinerary> CalculatePrice(
        RouteWithTraffic route,
        CustomerOffering offering,
        int passengers)
    {
        if (chaos.SimulatePricingCrash)
        {
            return new UnhandledError<PricedItinerary>(
                new DivideByZeroException("Fare calculation service failed (simulated failure)."));
        }

        if (passengers <= 0)
            return new UnhandledNothing<PricedItinerary>();

        var baseDistanceFare = Math.Round((decimal)route.Route.TotalDistanceKm * BaseRatePerKm * passengers, 2);

        // The surcharge applies to the kilometres that pass through engineering works, not to the whole journey.
        var multiplier = (decimal)route.DelaySeverityMultiplier;
        var affectedFare = (decimal)route.AffectedDistanceKm * BaseRatePerKm * passengers;
        var roadworksSurcharge = route.HasRoadworksOrTrackMaintenance
            ? Math.Round(affectedFare * (multiplier - 1.0m), 2)
            : 0.00m;

        // Exhaustive Switch Expression Pattern Matching on CustomerOffering Discriminated Union
        var offeringAddon = offering switch
        {
            StandardDayTrip { IncludesReturnTicket: true } =>
                Math.Round(baseDistanceFare * 0.65m, 2), // Discounted return leg
            StandardDayTrip { IncludesReturnTicket: false } =>
                0.00m,
            RailHoliday { Nights.Value: var nights, HotelTier: var tier } =>
                nights * (tier == "Luxury 5-Star" ? 190m : 110m) * passengers,
            RailHolidayWithMeals { Nights.Value: var nights, LoungeAccess: var lounge } =>
                (nights * 165m + (lounge ? 45m : 20m)) * passengers,
            _ => 0.00m
        };

        var finalPrice = baseDistanceFare + roadworksSurcharge + offeringAddon;

        var audit = ImmutableList.Create(
            $"Base fare: {route.Route.TotalDistanceKm:F1} km x €{BaseRatePerKm}/km x {passengers} passenger(s) = €{baseDistanceFare:F2}",
            route.HasRoadworksOrTrackMaintenance
                ? $"Engineering works: {(multiplier - 1.0m) * 100:0}% of the fare for {route.AffectedDistanceKm:N0} affected km = +€{roadworksSurcharge:F2}"
                : "Engineering works surcharge: €0.00",
            $"Package ({offering.Title}): +€{offeringAddon:F2}",
            $"Total: €{finalPrice:F2}"
        );

        return new Something<PricedItinerary>(new PricedItinerary(
            TrafficRoute: route,
            Offering: offering,
            BaseFare: baseDistanceFare,
            RoadworksSurcharge: roadworksSurcharge,
            OfferingAddon: offeringAddon,
            FinalPrice: finalPrice,
            Currency: "€",
            PipelineAuditLog: audit));
    }
}
