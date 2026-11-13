using RailFlow.Core.Domain;
using RailFlow.Infrastructure.Services;

namespace RailFlow.Web.Components.Tabs;

/// <summary>
/// The fields of the trip form. Home keeps the only instance, because other tabs use them too (the timetable
/// starts at the chosen departure time); PlanTab binds its inputs straight to it. Undo and Redo load a stored
/// snapshot back into it.
/// </summary>
public sealed class PlannerInputs
{
    public string Origin { get; set; } = "LONDON";
    public string Destination { get; set; } = "ZURICH";
    public int Passengers { get; set; } = 2;
    public TimeOnly Departure { get; set; } = new(8, 0);
    public DateOnly TravelDate { get; set; } = DateOnly.FromDateTime(DateTime.Today.AddDays(1));

    // The form shows station names ("London"); the search and the history keep the codes ("LONDON").
    public string OriginName
    {
        get => UiText.Station(Origin);
        set => Origin = ToStationCode(value);
    }

    public string DestinationName
    {
        get => UiText.Station(Destination);
        set => Destination = ToStationCode(value);
    }

    public static string ToStationCode(string? name) => (name ?? "").Trim().ToUpperInvariant();

    /// <summary>The value of the package drop-down; <see cref="ToOffering"/> turns it into a <see cref="CustomerOffering"/>.</summary>
    public string OfferingKey { get; set; } = "HolidayWithMeals";

    /// <summary>Drop-down entries: key and a short label. The full description is shown under the field.</summary>
    public static IReadOnlyList<(string Key, string Label)> Packages { get; } =
    [
        ("DayTripSingle", "Single ticket"),
        ("DayTripReturn", "Return ticket"),
        ("HolidayComfort", "City break, 3 nights"),
        ("HolidayLuxury", "Grand tour, 5 nights"),
        ("HolidayWithMeals", "Alpine package, 4 nights")
    ];

    public CustomerOffering ToOffering() => OfferingKey switch
    {
        "DayTripSingle" => new StandardDayTrip("Single ticket", "One-way rail fare", false),
        "DayTripReturn" => new StandardDayTrip("Return ticket", "Return journey at 65% of the outbound fare", true),
        "HolidayComfort" => new RailHoliday("City break", "3 nights in a 4-star hotel", new HotelNights(3), HotelTier.Comfort),
        "HolidayLuxury" => new RailHoliday("Grand tour", "5 nights in a 5-star hotel", new HotelNights(5), HotelTier.Luxury),
        _ => new RailHolidayWithMeals("Alpine package", "4 nights with full board and lounge access", new HotelNights(4), HotelTier.Boutique, "Full board", true)
    };

    public void SwapStations() => (Origin, Destination) = (Destination, Origin);

    public PlannerFormSnapshot ToSnapshot(DateTime timestampUtc) =>
        new(Origin, Destination, Passengers, OfferingKey, timestampUtc, TravelDate, Departure);

    public void LoadFrom(PlannerFormSnapshot snapshot)
    {
        Origin = snapshot.Origin;
        Destination = snapshot.Destination;
        Passengers = snapshot.Passengers;
        OfferingKey = snapshot.OfferingKey;
        if (snapshot.TravelDate is { } day)
            TravelDate = day;
        if (snapshot.Departure is { } dep)
            Departure = dep;
    }

    /// <summary>
    /// True when the form still matches the given snapshot across every field that affects the trip or price.
    /// </summary>
    public bool Matches(PlannerFormSnapshot? snapshot) =>
        snapshot is not null
        && string.Equals(Origin, snapshot.Origin, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Destination, snapshot.Destination, StringComparison.OrdinalIgnoreCase)
        && Passengers == snapshot.Passengers
        && string.Equals(OfferingKey, snapshot.OfferingKey, StringComparison.Ordinal)
        && (snapshot.TravelDate is null || TravelDate == snapshot.TravelDate)
        && (snapshot.Departure is null || Departure == snapshot.Departure);

    /// <summary>
    /// Loads validated trip parameters from URL query strings (for example after returning from /login).
    /// Unknown or out-of-range values are ignored so malformed URLs cannot break the form.
    /// </summary>
    public bool ApplyQueryParameters(
        string? from,
        string? to,
        int? passengers,
        string? date,
        string? departure,
        string? package)
    {
        var changed = false;

        if (!string.IsNullOrWhiteSpace(from) && IsPlausibleStationParam(from))
        {
            Origin = ToStationCode(from);
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(to) && IsPlausibleStationParam(to))
        {
            Destination = ToStationCode(to);
            changed = true;
        }

        if (passengers is >= 1 and <= 50)
        {
            Passengers = passengers.Value;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(date)
            && DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate)
            && parsedDate >= DateOnly.FromDateTime(DateTime.Today))
        {
            TravelDate = parsedDate;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(departure)
            && TimeOnly.TryParseExact(departure.Trim(), "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedTime))
        {
            Departure = parsedTime;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(package)
            && Packages.Any(p => string.Equals(p.Key, package.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            OfferingKey = Packages.First(p => string.Equals(p.Key, package.Trim(), StringComparison.OrdinalIgnoreCase)).Key;
            changed = true;
        }

        return changed;
    }

    public static string BuildReturnUrl(string? tab, PlannerFormSnapshot snapshot)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(tab) && !string.Equals(tab, "plan", StringComparison.OrdinalIgnoreCase))
            parts.Add($"tab={Uri.EscapeDataString(tab)}");

        parts.Add($"from={Uri.EscapeDataString(snapshot.Origin)}");
        parts.Add($"to={Uri.EscapeDataString(snapshot.Destination)}");
        parts.Add($"pax={snapshot.Passengers.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (snapshot.TravelDate is { } day)
            parts.Add($"date={day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}");
        if (snapshot.Departure is { } dep)
            parts.Add($"dep={dep.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}");
        parts.Add($"pkg={Uri.EscapeDataString(snapshot.OfferingKey)}");

        return "/?" + string.Join("&", parts);
    }

    public static string BuildLoginHref(string? tab, PlannerFormSnapshot snapshot) =>
        $"login?ReturnUrl={Uri.EscapeDataString(BuildReturnUrl(tab, snapshot))}";

    private static bool IsPlausibleStationParam(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length is >= 2 and <= 40 && trimmed.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_');
    }
}
