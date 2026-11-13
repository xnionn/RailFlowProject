using RailFlow.Core.Domain;
using RailFlow.Infrastructure.Services;

namespace RailFlow.Web.Components.Tabs;

/// <summary>
/// The fields of the trip form. Home keeps the only instance, because other tabs use them too (the timetable
/// starts at the chosen departure time); PlanTab binds its inputs straight to it. Undo and Redo load a stored
/// snapshot back into it. The travel date and departure time are not part of the Undo history: they do not change the price.
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
        new(Origin, Destination, Passengers, OfferingKey, timestampUtc);

    public void LoadFrom(PlannerFormSnapshot snapshot)
    {
        Origin = snapshot.Origin;
        Destination = snapshot.Destination;
        Passengers = snapshot.Passengers;
        OfferingKey = snapshot.OfferingKey;
    }
}
