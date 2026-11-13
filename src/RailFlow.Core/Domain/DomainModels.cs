using System.Collections.Immutable;
using RailFlow.Core.Functional;

namespace RailFlow.Core.Domain;

// --- Records of the route-pricing pipeline ---

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

/// <summary>
/// A planned route after the engineering-works check. AffectedDistanceKm is the length of the sections that start or end
/// at a station with works; only those kilometres carry the surcharge.
/// </summary>
public sealed record RouteWithTraffic(
    PlannedRoute Route,
    bool HasEngineeringWorks,
    double FareMultiplier,
    string TrafficAdvisory,
    double AffectedDistanceKm = 0.0);

public sealed record PricedItinerary(
    RouteWithTraffic TrafficRoute,
    CustomerOffering Offering,
    decimal BaseFare,
    decimal EngineeringWorksSurcharge,
    decimal OfferingAddon,
    decimal FinalPrice,
    string Currency,
    ImmutableList<string> PipelineAuditLog);

// --- Discriminated Union: CustomerOffering ---
public abstract record CustomerOffering(string Title, string Description);

public sealed record StandardDayTrip(
    string Title,
    string Description,
    bool IncludesReturnTicket) : CustomerOffering(Title, Description);

public sealed record RailHoliday(
    string Title,
    string Description,
    HotelNights Nights,
    HotelTier Tier) : CustomerOffering(Title, Description);

public sealed record RailHolidayWithMeals(
    string Title,
    string Description,
    HotelNights Nights,
    HotelTier Tier,
    string DiningPlan,
    bool LoungeAccess) : CustomerOffering(Title, Description);

public readonly record struct HotelNights(int Value);

public enum HotelTier
{
    Comfort,
    Luxury,
    Boutique
}

// --- Discriminated Union: UserInput Validation ---
public abstract record ValidatedUserInput;
public sealed record ValidRouteQuery(string Origin, string Destination, int Passengers, CustomerOffering Offering) : ValidatedUserInput;
public sealed record EmptyRouteQuery(string FieldName) : ValidatedUserInput;
public sealed record SameStationError(string Station) : ValidatedUserInput;
public sealed record InvalidPassengerCount(int AttemptedCount) : ValidatedUserInput;

// --- Service interfaces used by the route-pricing pipeline ---
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
    Maybe<RouteWithTraffic> CheckEngineeringWorks(PlannedRoute route);
}

public interface IPricingCalculator
{
    Maybe<PricedItinerary> CalculatePrice(RouteWithTraffic route, CustomerOffering offering, int passengers);
}

// --- State of the step-by-step train simulation ---
public sealed record TransitSimulationState(
    int StepIndex,
    string CurrentStation,
    double ProgressKm,
    double TotalDistanceKm,
    int AccumulatedMinutes,
    string LastEventDescription,
    bool ReachedDestination);
