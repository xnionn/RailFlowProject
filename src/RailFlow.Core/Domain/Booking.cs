using System.Collections.Immutable;
using System.Globalization;
using RailFlow.Core.Functional;

namespace RailFlow.Core.Domain;

// --- Week 6: Handling Temporal Coupling ---
// Each booking stage is its own immutable record, and each step takes the previous stage and
// returns the next one. Charge accepts only a PricedBooking, and in the app a PricedBooking only comes
// out of ApplyPromo, which needs a ValidatedBooking: "charge before validation" is not a call sequence
// the types allow.

/// <param name="Owner">User name of the signed-in account that books, or "" for a guest.</param>
/// <param name="TravelDate">Day of travel chosen on the planner.</param>
/// <param name="DepartureTime">Planned departure time from the origin station.</param>
public sealed record BookingDraft(
    PricedItinerary Itinerary,
    int Passengers,
    string PassengerName,
    string Email,
    string Owner = "",
    DateOnly? TravelDate = null,
    TimeOnly? DepartureTime = null);

public sealed record ValidatedBooking(
    PricedItinerary Itinerary,
    int Passengers,
    string PassengerName,
    string Email,
    string Owner = "",
    DateOnly? TravelDate = null,
    TimeOnly? DepartureTime = null);

public sealed record PricedBooking(
    ValidatedBooking Booking,
    string PromoCode,
    decimal DiscountRate,
    decimal ServiceFee,
    decimal AmountDue);

/// <summary>
/// Final, persisted stage of a booking (Week 3: immutable model with well-defined equality and ordering).
/// Two receipts with the same BookingId are the same booking, whatever their other fields say,
/// so duplicates disappear when receipts are merged from several stores.
/// Receipts are ordered by amount paid, so <c>Order()</c> lists the cheapest first.
/// Stores a complete snapshot of the booked trip (date, departure, arrival/day offset and itinerary legs)
/// so tickets remain accurate even after the planner search changes.
/// </summary>
public sealed record BookingReceipt(
    string BookingId,
    string TransactionId,
    string PassengerName,
    string Email,
    string Origin,
    string Destination,
    string OfferingTitle,
    int Passengers,
    decimal AmountPaid,
    string Currency,
    DateTime IssuedAtUtc,
    string Owner = "",
    DateOnly? TravelDate = null,
    TimeOnly? DepartureTime = null,
    TimeOnly? ArrivalTime = null,
    int ArrivalDayOffset = 0,
    ImmutableList<RouteSegment>? Legs = null) : IComparable<BookingReceipt>
{
    public bool Equals(BookingReceipt? other) =>
        other is not null && string.Equals(BookingId, other.BookingId, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(BookingId);

    public int CompareTo(BookingReceipt? other)
    {
        if (other is null) return 1;
        var byAmount = AmountPaid.CompareTo(other.AmountPaid);
        return byAmount != 0 ? byAmount : string.CompareOrdinal(BookingId, other.BookingId);
    }
}

/// <summary>
/// The booking workflow: Validate -> ApplyPromo -> Charge.
/// Expected problems (bad e-mail, unknown promo code, declined card) are returned as Failure,
/// never thrown (Week 7). The only side effect, the payment, is injected as a function (Week 6),
/// so tests can pass a fake gateway and count its calls.
/// </summary>
public static class BookingWorkflow
{
    public const decimal ServiceFee = 2.50m;

    private static readonly ImmutableDictionary<string, decimal> PromoCodes =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["STUDENT10"] = 0.10m,
            ["RAILFLOW20"] = 0.20m
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    // Week 5: small pure steps, curried and partially applied, then combined into one fare pipeline.
    private static readonly Func<decimal, decimal, decimal> ApplyDiscount = (rate, amount) => amount - amount * rate;
    private static readonly Func<decimal, decimal, decimal> AddFee = (fee, amount) => amount + fee;

    public static decimal RoundToCents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static Func<decimal, decimal> FarePipeline(decimal discountRate, decimal serviceFee) =>
        Pipelining.Pipeline(
            ApplyDiscount.Curry()(discountRate),
            AddFee.Curry()(serviceFee),
            RoundToCents);

    /// <param name="today">The current date, passed in so the check stays a pure function. A travel date before it is refused.</param>
    public static Result<ValidatedBooking> Validate(BookingDraft draft, DateOnly? today = null) =>
        Result.Ok(draft with { PassengerName = draft.PassengerName.Trim(), Email = draft.Email.Trim() })
            .Ensure(d => today is null || d.TravelDate is null || d.TravelDate >= today, "The travel date is in the past.")
            .Ensure(d => d.PassengerName.Length >= 2, "Passenger name must have at least 2 characters.")
            .Ensure(d => d.PassengerName.Length <= 60, "Passenger name must be at most 60 characters.")
            .Ensure(d => !d.PassengerName.Contains('|'), "Passenger name must not contain '|'.")
            .Ensure(d => IsPlausibleEmail(d.Email), "E-mail address is not valid.")
            .Ensure(d => d.Passengers is >= 1 and <= 50, "Passenger count must be between 1 and 50.")
            .Ensure(d => d.Itinerary.FinalPrice > 0m, "The itinerary has no price to charge.")
            .Map(d => new ValidatedBooking(d.Itinerary, d.Passengers, d.PassengerName, d.Email, d.Owner, d.TravelDate, d.DepartureTime));

    public static Result<PricedBooking> ApplyPromo(ValidatedBooking booking, string? promoCode) =>
        Quote(booking.Itinerary.FinalPrice, promoCode)
            .Map(quote => new PricedBooking(
                Booking: booking,
                PromoCode: quote.PromoCode,
                DiscountRate: quote.DiscountRate,
                ServiceFee: quote.ServiceFee,
                AmountDue: quote.AmountDue));

    /// <summary>
    /// The same fare pipeline as <see cref="ApplyPromo"/>, without a booking: the form uses it to show the
    /// amount to pay while the passenger types, so the total never changes at the moment of payment.
    /// </summary>
    public static Result<FareQuote> Quote(decimal fare, string? promoCode) =>
        ResolvePromo(promoCode)
            .Map(promo => new FareQuote(
                Fare: fare,
                PromoCode: promo.Code,
                DiscountRate: promo.Rate,
                Discount: RoundToCents(fare * promo.Rate),
                ServiceFee: ServiceFee,
                AmountDue: fare.Pipe(FarePipeline(promo.Rate, ServiceFee))));

    public static Result<BookingReceipt> Charge(
        PricedBooking priced,
        Func<decimal, Result<string>> paymentGateway,
        DateTime issuedAtUtc)
    {
        var route = priced.Booking.Itinerary.TrafficRoute.Route;
        TimeOnly? arrivalTime = null;
        var arrivalDayOffset = 0;
        if (priced.Booking.DepartureTime is { } dep)
        {
            var lastStop = RecursiveTimetable.BuildTimetable(route, dep)[^1];
            arrivalTime = lastStop.Arrival;
            arrivalDayOffset = lastStop.DayOffset;
        }

        return paymentGateway(priced.AmountDue)
            .Map(transactionId => new BookingReceipt(
                BookingId: BookingIdFrom(transactionId),
                TransactionId: transactionId,
                PassengerName: priced.Booking.PassengerName,
                Email: priced.Booking.Email,
                Origin: route.Origin,
                Destination: route.Destination,
                OfferingTitle: priced.Booking.Itinerary.Offering.Title,
                Passengers: priced.Booking.Passengers,
                AmountPaid: priced.AmountDue,
                Currency: priced.Booking.Itinerary.Currency,
                IssuedAtUtc: issuedAtUtc,
                Owner: priced.Booking.Owner,
                TravelDate: priced.Booking.TravelDate,
                DepartureTime: priced.Booking.DepartureTime,
                ArrivalTime: arrivalTime,
                ArrivalDayOffset: arrivalDayOffset,
                Legs: route.Segments));
    }

    /// <summary>
    /// Runs the whole workflow and keeps every stage, so the UI can show where it stopped.
    /// </summary>
    public static BookingTrace Run(
        BookingDraft draft,
        string? promoCode,
        Func<decimal, Result<string>> paymentGateway,
        DateTime issuedAtUtc)
    {
        var validated = Validate(draft, DateOnly.FromDateTime(issuedAtUtc));
        var priced = validated.Bind(v => ApplyPromo(v, promoCode));
        var receipt = priced.Bind(p => Charge(p, paymentGateway, issuedAtUtc));
        return new BookingTrace(validated, priced, receipt);
    }

    private static Result<(string Code, decimal Rate)> ResolvePromo(string? promoCode) =>
        string.IsNullOrWhiteSpace(promoCode)
            ? Result.Ok((Code: "", Rate: 0m))
            : PromoCodes.TryGetValue(promoCode.Trim(), out var rate)
                ? Result.Ok((Code: promoCode.Trim().ToUpperInvariant(), Rate: rate))
                : Result.Fail<(string, decimal)>($"Unknown promo code '{promoCode.Trim()}'.");

    private static bool IsPlausibleEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0
            && at == email.LastIndexOf('@')
            && email.IndexOf('.', at) > at + 1
            && !email.EndsWith('.')
            && !email.Any(char.IsWhiteSpace)
            && !email.Contains('|');
    }

    private static string BookingIdFrom(string transactionId) =>
        "RF-" + (transactionId.StartsWith("TX-", StringComparison.Ordinal) ? transactionId[3..] : transactionId);
}

/// <summary>
/// What the passenger will pay, shown before the payment is taken.
/// </summary>
public sealed record FareQuote(decimal Fare, string PromoCode, decimal DiscountRate, decimal Discount, decimal ServiceFee, decimal AmountDue);

public sealed record BookingTrace(
    Result<ValidatedBooking> Validated,
    Result<PricedBooking> Priced,
    Result<BookingReceipt> Receipt);

/// <summary>
/// Week 4: one place that defines the text form of a receipt. ToLine and FromLine are plain functions
/// with no file I/O, so the format can be tested against strings, and both the writer and the reader use them.
/// </summary>
public static class ReceiptLineCodec
{
    private const char Separator = '|';
    private const char LegSeparator = ';';
    private const char LegFieldSeparator = '~';
    private const int FieldCount = 17;

    /// <summary>Lines written before bookings stored times and route legs (13 fields) or owner/travel date (11 fields); they are still read.</summary>
    private const int LegacyDateFieldCount = 13;
    private const int OldFieldCount = 11;

    public static string ToLine(BookingReceipt r) =>
        string.Join(Separator,
            r.BookingId,
            r.TransactionId,
            r.PassengerName,
            r.Email,
            r.Origin,
            r.Destination,
            r.OfferingTitle,
            r.Passengers.ToString(CultureInfo.InvariantCulture),
            r.AmountPaid.ToString(CultureInfo.InvariantCulture),
            r.Currency,
            r.IssuedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            r.Owner,
            r.TravelDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            r.DepartureTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "",
            r.ArrivalTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "",
            r.ArrivalDayOffset.ToString(CultureInfo.InvariantCulture),
            EncodeLegs(r.Legs));

    public static Result<BookingReceipt> FromLine(string line)
    {
        var f = line.Split(Separator);
        if (f.Length != FieldCount && f.Length != LegacyDateFieldCount && f.Length != OldFieldCount)
            return Result.Fail<BookingReceipt>($"Expected {FieldCount} fields but found {f.Length}.");

        var owner = f.Length >= LegacyDateFieldCount ? f[11] : "";
        DateOnly? travelDate = null;
        if (f.Length >= LegacyDateFieldCount && f[12].Length > 0)
        {
            if (!DateOnly.TryParseExact(f[12], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return Result.Fail<BookingReceipt>($"Travel date '{f[12]}' is not a valid date.");
            travelDate = day;
        }

        TimeOnly? departureTime = null;
        TimeOnly? arrivalTime = null;
        var arrivalDayOffset = 0;
        ImmutableList<RouteSegment>? legs = null;

        if (f.Length == FieldCount)
        {
            if (f[13].Length > 0)
            {
                if (!TimeOnly.TryParseExact(f[13], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dep))
                    return Result.Fail<BookingReceipt>($"Departure time '{f[13]}' is not a valid time.");
                departureTime = dep;
            }

            if (f[14].Length > 0)
            {
                if (!TimeOnly.TryParseExact(f[14], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var arr))
                    return Result.Fail<BookingReceipt>($"Arrival time '{f[14]}' is not a valid time.");
                arrivalTime = arr;
            }

            if (f[15].Length > 0)
            {
                if (!int.TryParse(f[15], NumberStyles.Integer, CultureInfo.InvariantCulture, out arrivalDayOffset) || arrivalDayOffset < 0)
                    return Result.Fail<BookingReceipt>($"Arrival day offset '{f[15]}' is not a valid offset.");
            }

            if (f[16].Length > 0)
            {
                if (DecodeLegs(f[16]) is not { } parsedLegs)
                    return Result.Fail<BookingReceipt>("Route legs are malformed.");
                legs = parsedLegs;
            }
        }

        if (!int.TryParse(f[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var passengers))
            return Result.Fail<BookingReceipt>($"Passengers '{f[7]}' is not a number.");

        if (!decimal.TryParse(f[8], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            return Result.Fail<BookingReceipt>($"Amount '{f[8]}' is not a number.");

        if (!DateTime.TryParse(f[10], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var issued))
            return Result.Fail<BookingReceipt>($"Date '{f[10]}' is not a valid timestamp.");

        return string.IsNullOrWhiteSpace(f[0])
            ? Result.Fail<BookingReceipt>("Booking id is empty.")
            : Result.Ok(new BookingReceipt(
                f[0], f[1], f[2], f[3], f[4], f[5], f[6],
                passengers, amount, f[9], issued, owner, travelDate,
                departureTime, arrivalTime, arrivalDayOffset, legs));
    }

    private static string EncodeLegs(ImmutableList<RouteSegment>? legs) =>
        legs is null || legs.Count == 0
            ? ""
            : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(legs)));

    private static ImmutableList<RouteSegment>? DecodeLegs(string raw)
    {
        if (TryDecodeJsonLegs(raw) is { } jsonLegs)
            return jsonLegs;

        return TryDecodeLegacyDelimitedLegs(raw);
    }

    private static ImmutableList<RouteSegment>? TryDecodeJsonLegs(string raw)
    {
        try
        {
            var json = raw.StartsWith('[')
                ? raw
                : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<RouteSegment>>(json);
            if (parsed is null || parsed.Any(s =>
                    string.IsNullOrWhiteSpace(s.FromStation)
                    || string.IsNullOrWhiteSpace(s.ToStation)
                    || s.DistanceKm <= 0
                    || s.BaseMinutes <= 0))
            {
                return null;
            }
            return parsed.ToImmutableList();
        }
        catch
        {
            return null;
        }
    }

    private static ImmutableList<RouteSegment>? TryDecodeLegacyDelimitedLegs(string raw)
    {
        var items = ImmutableList.CreateBuilder<RouteSegment>();
        foreach (var part in raw.Split(LegSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cells = part.Split(LegFieldSeparator);
            if (cells.Length != 5
                || string.IsNullOrWhiteSpace(cells[0])
                || string.IsNullOrWhiteSpace(cells[1])
                || !double.TryParse(cells[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var km)
                || !int.TryParse(cells[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
            {
                return null;
            }
            items.Add(new RouteSegment(cells[0], cells[1], km, minutes, cells[4]));
        }
        return items.Count > 0 ? items.ToImmutable() : null;
    }
}
