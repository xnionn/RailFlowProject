using System.Collections.Immutable;
using RailFlow.Core.Domain;
using RailFlow.Core.Functional;

namespace RailFlow.Infrastructure.Services;

/// <summary>
/// Live interactive Chaos & Traffic state so Dispatcher/Admin users can inject
/// roadworks or exceptions into any stage of the Railway-Oriented pipeline.
/// </summary>
public sealed class ChaosAndTrafficController
{
    private readonly HashSet<string> _roadworksStations = new(StringComparer.OrdinalIgnoreCase)
    {
        "FRANKFURT",
        "LYON"
    };

    public bool SimulateMappingServiceCrash { get; set; }
    public bool SimulateRoutePlannerCrash { get; set; }
    public bool SimulateTrafficMonitorCrash { get; set; }
    public bool SimulatePricingCrash { get; set; }
    public double RoadworksMultiplier { get; set; } = 1.10; // Exact 1.1x multiplier from Lecture L6_1 slide 40

    public event Action<string>? OnTrafficAlertBroadcast;

    public IReadOnlyCollection<string> ActiveRoadworksStations => _roadworksStations.ToList();

    public bool HasRoadworksAt(string station) => _roadworksStations.Contains(station);

    public void ToggleRoadworks(string station, string actorRole)
    {
        var normalized = station.Trim().ToUpperInvariant();
        if (_roadworksStations.Contains(normalized))
        {
            _roadworksStations.Remove(normalized);
            OnTrafficAlertBroadcast?.Invoke($"[{actorRole}] Cleared track maintenance at {normalized}. Multiplier restored to 1.00x.");
        }
        else
        {
            _roadworksStations.Add(normalized);
            OnTrafficAlertBroadcast?.Invoke($"[{actorRole}] Activated Roadworks Alert at {normalized}! Pricing multiplier set to {RoadworksMultiplier:F2}x.");
        }
    }

    public void BroadcastCustomAdvisory(string message)
    {
        OnTrafficAlertBroadcast?.Invoke(message);
    }
}

/// <summary>
/// Concrete IMappingSystem (Lecture L6_1) initialized using the Partially Applied CSV Parser (Lecture L7_1).
/// </summary>
public sealed class RailMappingSystem : IMappingSystem
{
    private readonly ImmutableDictionary<string, GeoCoord> _stations;
    private readonly ChaosAndTrafficController _chaos;

    private const string EmbeddedStationsCsv = """
        LONDON,51.5308,-0.1238
        PARIS,48.8809,2.3553
        BRUSSELS,50.8357,4.3365
        AMSTERDAM,52.3791,4.9003
        COLOGNE,50.9432,6.9586
        FRANKFURT,50.1071,8.6638
        ZURICH,47.3779,8.5403
        MILAN,45.4859,9.2043
        LYON,45.7606,4.8594
        MUNICH,48.1402,11.5599
        BERLIN,52.5251,13.3694
        VIENNA,48.1851,16.3766
        """;

    public RailMappingSystem(ChaosAndTrafficController chaos)
    {
        _chaos = chaos;
        var rows = BenchmarkAndLoopLab.StandardCsvParser(EmbeddedStationsCsv);
        var builder = ImmutableDictionary.CreateBuilder<string, GeoCoord>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (row.Count >= 3 &&
                double.TryParse(row[1], System.Globalization.CultureInfo.InvariantCulture, out var lat) &&
                double.TryParse(row[2], System.Globalization.CultureInfo.InvariantCulture, out var lon))
            {
                builder[row[0]] = new GeoCoord(lat, lon, row[0].ToUpperInvariant());
            }
        }

        _stations = builder.ToImmutable();
    }

    public Maybe<GeoCoord> FindLocation(string placeName)
    {
        if (_chaos.SimulateMappingServiceCrash)
        {
            return new UnhandledError<GeoCoord>(
                new InvalidOperationException("IMappingSystem satellite telemetry timeout (Simulated Fault)."));
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
/// Concrete IRoutePlanner (Lecture L6_1) backed by the Memoized BFS TransitGraphEngine (Lecture L8_1).
/// </summary>
public sealed class RailRoutePlanner : IRoutePlanner
{
    private readonly TransitGraphEngine _graphEngine;
    private readonly ChaosAndTrafficController _chaos;

    private const string EmbeddedConnectionsCsv = """
        LONDON,PARIS,460,135,Eurostar High-Speed
        LONDON,BRUSSELS,370,120,Eurostar Channel Line
        PARIS,BRUSSELS,310,82,Thalys Red Corridor
        BRUSSELS,AMSTERDAM,210,110,Benelux Intercity
        BRUSSELS,COLOGNE,220,105,ICE International
        COLOGNE,FRANKFURT,190,62,ICE Rhine-Main Sprinter
        FRANKFURT,BERLIN,545,235,ICE Sprinter North-East
        FRANKFURT,MUNICH,390,195,ICE Bavarian Express
        FRANKFURT,ZURICH,400,230,ECE Helvetia
        PARIS,LYON,465,118,TGV Sud-Est Duplex
        LYON,ZURICH,415,215,TGV Lyria Alpine
        LYON,MILAN,445,265,Frecciarossa Transalpine
        ZURICH,MILAN,285,195,Gotthard Base Tunnel EC
        MUNICH,VIENNA,405,240,Railjet Danube Express
        BERLIN,VIENNA,680,460,ICE Vindobona Corridor
        """;

    public RailRoutePlanner(ChaosAndTrafficController chaos)
    {
        _chaos = chaos;
        var rows = BenchmarkAndLoopLab.StandardCsvParser(EmbeddedConnectionsCsv);
        var edges = new List<GraphConnection>();
        foreach (var r in rows)
        {
            if (r.Count >= 5 &&
                double.TryParse(r[2], System.Globalization.CultureInfo.InvariantCulture, out var km) &&
                int.TryParse(r[3], System.Globalization.CultureInfo.InvariantCulture, out var min))
            {
                edges.Add(new GraphConnection(r[0], r[1], km, min, r[4]));
            }
        }
        _graphEngine = new TransitGraphEngine(edges);
    }

    public TransitGraphEngine GraphEngine => _graphEngine;

    public Maybe<PlannedRoute> PlanRoute(GeoCoord start, GeoCoord end)
    {
        if (_chaos.SimulateRoutePlannerCrash)
        {
            return new UnhandledError<PlannedRoute>(
                new TimeoutException("IRoutePlanner graph solver encountered a deadlock (Simulated Fault)."));
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
/// Concrete ITrafficMonitor (Lecture L6_1) inspecting intermediate stations for active roadworks/track maintenance.
/// </summary>
public sealed class RailTrafficMonitor(ChaosAndTrafficController chaos) : ITrafficMonitor
{
    public Maybe<RouteWithTraffic> CheckForRoadworks(PlannedRoute route)
    {
        if (chaos.SimulateTrafficMonitorCrash)
        {
            return new UnhandledError<RouteWithTraffic>(
                new HttpRequestException("ITrafficMonitor sensor feed disconnected (Simulated 503 Fault)."));
        }

        var traversedStations = route.Segments
            .SelectMany(s => new[] { s.FromStation, s.ToStation })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var affected = traversedStations
            .Where(chaos.HasRoadworksAt)
            .ToList();

        if (affected.Count > 0)
        {
            var multiplier = chaos.RoadworksMultiplier;
            return new Something<RouteWithTraffic>(new RouteWithTraffic(
                Route: route,
                HasRoadworksOrTrackMaintenance: true,
                DelaySeverityMultiplier: multiplier,
                TrafficAdvisory: $"Engineering works active at {string.Join(", ", affected)} (+{(multiplier - 1.0) * 100:F0}% dynamic tariff factor)."));
        }

        return new Something<RouteWithTraffic>(new RouteWithTraffic(
            Route: route,
            HasRoadworksOrTrackMaintenance: false,
            DelaySeverityMultiplier: 1.0,
            TrafficAdvisory: "All traversed rail corridors operating at nominal green signal capacity."));
    }
}

/// <summary>
/// Concrete IPricingCalculator (Lectures L5_1 & L6_1) combining the 1.1x Roadworks Multiplier
/// with Pattern Matching over the CustomerOffering Discriminated Union (StandardDayTrip, RailHoliday, RailHolidayWithMeals).
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
                new DivideByZeroException("IPricingCalculator tariff matrix overflow (Simulated Fault)."));
        }

        if (passengers <= 0)
            return new UnhandledNothing<PricedItinerary>();

        var baseDistanceFare = Math.Round((decimal)route.Route.TotalDistanceKm * BaseRatePerKm * passengers, 2);

        // Lecture L6_1 Slide 40: Apply 1.1x multiplier if roadworks are present on the route
        var multiplier = (decimal)route.DelaySeverityMultiplier;
        var roadworksSurcharge = route.HasRoadworksOrTrackMaintenance
            ? Math.Round(baseDistanceFare * (multiplier - 1.0m), 2)
            : 0.00m;

        // Lecture L5_1 Exhaustive Switch Expression Pattern Matching on CustomerOffering Discriminated Union
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
            $"Base Rail Distance ({route.Route.TotalDistanceKm:F1} km x €{BaseRatePerKm}/km x {passengers} pax): €{baseDistanceFare:F2}",
            route.HasRoadworksOrTrackMaintenance
                ? $"Roadworks Surcharge (x{route.DelaySeverityMultiplier:F2} factor from L6_1): +€{roadworksSurcharge:F2}"
                : "Roadworks Surcharge: €0.00 (Clear corridor)",
            $"CustomerOffering Union Match [{offering.GetType().Name} — {offering.Title}]: +€{offeringAddon:F2}",
            $"Total Computed Fare: €{finalPrice:F2}"
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
