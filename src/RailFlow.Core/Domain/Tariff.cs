using System.Collections.Immutable;

namespace RailFlow.Core.Domain;

/// <summary>
/// The fare rules, as pure functions: a rate per kilometre and passenger, a surcharge on the kilometres that
/// pass through engineering works, and the package price chosen by the case of the CustomerOffering union.
/// The pricing service in Infrastructure only adds the simulated failure switch around <see cref="Price"/>.
/// </summary>
public static class Tariff
{
    public const decimal RatePerKm = 0.22m;
    public const string Currency = "€";

    /// <summary>A return ticket costs this share of the outbound fare.</summary>
    public const decimal ReturnShare = 0.65m;

    /// <summary>Hotel price per night and passenger.</summary>
    public static decimal NightlyRate(HotelTier tier) => tier switch
    {
        HotelTier.Comfort => 110m,
        HotelTier.Luxury => 190m,
        HotelTier.Boutique => 165m,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null)
    };

    public static decimal LoungePrice(bool lounge) => lounge ? 45m : 20m;

    public static decimal BaseFare(double distanceKm, int passengers) =>
        Math.Round((decimal)distanceKm * RatePerKm * passengers, 2);

    /// <summary>The extra share (multiplier − 1) is charged only on the affected kilometres, not on the whole trip.</summary>
    public static decimal EngineeringWorksSurcharge(RouteWithTraffic route, int passengers) =>
        route.HasEngineeringWorks
            ? Math.Round((decimal)route.AffectedDistanceKm * RatePerKm * passengers * ((decimal)route.FareMultiplier - 1.0m), 2)
            : 0.00m;

    public static decimal PackagePrice(CustomerOffering offering, decimal baseFare, int passengers) => offering switch
    {
        StandardDayTrip { IncludesReturnTicket: true } => Math.Round(baseFare * ReturnShare, 2),
        StandardDayTrip { IncludesReturnTicket: false } => 0.00m,
        RailHoliday { Nights.Value: var nights, Tier: var tier } => nights * NightlyRate(tier) * passengers,
        RailHolidayWithMeals { Nights.Value: var nights, Tier: var tier, LoungeAccess: var lounge } =>
            (nights * NightlyRate(tier) + LoungePrice(lounge)) * passengers,
        _ => throw new ArgumentOutOfRangeException(nameof(offering), offering, "Unknown package.")
    };

    /// <summary>Prices a trip; the same inputs always give the same itinerary.</summary>
    public static PricedItinerary Price(RouteWithTraffic route, CustomerOffering offering, int passengers)
    {
        var baseFare = BaseFare(route.Route.TotalDistanceKm, passengers);
        var surcharge = EngineeringWorksSurcharge(route, passengers);
        var package = PackagePrice(offering, baseFare, passengers);
        var total = baseFare + surcharge + package;

        var lines = ImmutableList.Create(
            $"Base fare: {route.Route.TotalDistanceKm:N0} km × {Currency}{RatePerKm}/km × {passengers} = {Currency}{baseFare:N2}",
            route.HasEngineeringWorks
                ? $"Engineering works: {((decimal)route.FareMultiplier - 1.0m) * 100:0}% on {route.AffectedDistanceKm:N0} affected km = +{Currency}{surcharge:N2}"
                : $"Engineering works surcharge: {Currency}0.00",
            $"Package ({offering.Title}): +{Currency}{package:N2}",
            $"Total: {Currency}{total:N2}");

        return new PricedItinerary(route, offering, baseFare, surcharge, package, total, Currency, lines);
    }
}
