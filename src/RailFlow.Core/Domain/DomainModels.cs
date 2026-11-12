using System.Collections.Immutable;
using RailFlow.Core.Functional;

namespace RailFlow.Core.Domain;

// --- Lecture L33 & L6_1 Worked Route-Pricing Domain Records ---

public sealed record GeoCoord(double Latitude, double Longitude, string StationCode);

public sealed record RouteSegment(
    string FromStation,
    string ToStation,
    double DistanceKm,
    int BaseMinutes,
    string LineName);

public sealed record PlannedRoute(
    string Origin,
    string Destination,
    ImmutableList<RouteSegment> Segments,
    double TotalDistanceKm,
    int EstimatedMinutes);

public sealed record RouteWithTraffic(
    PlannedRoute Route,
    bool HasRoadworksOrTrackMaintenance,
    double DelaySeverityMultiplier,
    string TrafficAdvisory);

public sealed record PricedItinerary(
    RouteWithTraffic TrafficRoute,
    CustomerOffering Offering,
    decimal BaseFare,
    decimal RoadworksSurcharge,
    decimal OfferingAddon,
    decimal FinalPrice,
    string Currency,
    ImmutableList<string> PipelineAuditLog);

// --- Lecture L5_1 Discriminated Union: CustomerOffering ---
public abstract record CustomerOffering(string Title, string Description);

public sealed record StandardDayTrip(
    string Title,
    string Description,
    bool IncludesReturnTicket) : CustomerOffering(Title, Description);

public sealed record RailHoliday(
    string Title,
    string Description,
    intHotelNights Nights,
    string HotelTier) : CustomerOffering(Title, Description);

public sealed record RailHolidayWithMeals(
    string Title,
    string Description,
    intHotelNights Nights,
    string HotelTier,
    string DiningPlan,
    bool LoungeAccess) : CustomerOffering(Title, Description);

public readonly record struct intHotelNights(int Value);

// --- Lecture L5_1 Discriminated Union: UserInput Validation ---
public abstract record ValidatedUserInput;
public sealed record ValidRouteQuery(string Origin, string Destination, int Passengers, CustomerOffering Offering) : ValidatedUserInput;
public sealed record EmptyRouteQuery(string FieldName) : ValidatedUserInput;
public sealed record SameStationError(string Station) : ValidatedUserInput;
public sealed record InvalidPassengerCount(int AttemptedCount) : ValidatedUserInput;

// --- Lecture L6_1 Worked Example Interfaces ---
public interface IMappingSystem
{
    Maybe<GeoCoord> FindLocation(string placeName);
    IReadOnlyList<GeoCoord> GetAllStations();
}

public interface IRoutePlanner
{
    Maybe<PlannedRoute> PlanRoute(GeoCoord start, GeoCoord end);
}

public interface ITrafficMonitor
{
    Maybe<RouteWithTraffic> CheckForRoadworks(PlannedRoute route);
}

public interface IPricingCalculator
{
    Maybe<PricedItinerary> CalculatePrice(RouteWithTraffic route, CustomerOffering offering, int passengers);
}

// --- Lecture L7_1 Snakes & Ladders / Transit Delay Indefinite Loop Simulation State ---
public sealed record TransitSimulationState(
    int StepIndex,
    string CurrentStation,
    double ProgressKm,
    double TotalDistanceKm,
    int AccumulatedMinutes,
    string LastEventDescription,
    bool ReachedDestination);
