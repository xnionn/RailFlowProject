using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RailFlow.Core.Domain;

public enum TicketFormat
{
    PlainText,
    Csv,
    Json
}

/// <summary>
/// Week 6: Changeable Code Using Factories.
/// A behaviour factory: the caller asks for a format and gets back a function
/// <c>BookingReceipt -> string</c>, without knowing how that format is produced.
/// Adding a format means changing this one switch, not every caller.
/// </summary>
public static class TicketExportFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Func<BookingReceipt, string> Create(TicketFormat format) => format switch
    {
        TicketFormat.PlainText => ToPlainText,
        TicketFormat.Csv => ToCsv,
        TicketFormat.Json => receipt => JsonSerializer.Serialize(receipt, JsonOptions),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported ticket format.")
    };

    public static (string ContentType, string Extension) Describe(TicketFormat format) => format switch
    {
        TicketFormat.PlainText => ("text/plain", ".txt"),
        TicketFormat.Csv => ("text/csv", ".csv"),
        TicketFormat.Json => ("application/json", ".json"),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported ticket format.")
    };

    // The text ticket is for people, so it uses station names and grouped amounts like the app;
    // CSV and JSON keep the station codes and plain numbers that other programs expect.
    private static string ToPlainText(BookingReceipt r) =>
        new StringBuilder()
            .AppendLine("RAILFLOW E-TICKET")
            .AppendLine($"Booking      {r.BookingId}")
            .AppendLine($"Passenger    {r.PassengerName} ({r.Passengers} {(r.Passengers == 1 ? "passenger" : "passengers")})")
            .AppendLine($"Route        {StationNames.Display(r.Origin)} → {StationNames.Display(r.Destination)}")
            .AppendLine($"Travel date  {(r.TravelDate is { } day ? day.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture) : "open")}")
            .AppendLine($"Package      {r.OfferingTitle}")
            .AppendLine(FormattableString.Invariant($"Paid         {r.Currency}{r.AmountPaid:N2}"))
            .AppendLine($"Transaction  {r.TransactionId}")
            .Append($"Issued (UTC) {r.IssuedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}")
            .ToString();

    private static string ToCsv(BookingReceipt r) =>
        "booking_id,passenger,origin,destination,travel_date,package,passengers,amount,currency,issued_utc\n" +
        string.Join(',',
            r.BookingId,
            Quote(r.PassengerName),
            r.Origin,
            r.Destination,
            r.TravelDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            Quote(r.OfferingTitle),
            r.Passengers.ToString(CultureInfo.InvariantCulture),
            r.AmountPaid.ToString("F2", CultureInfo.InvariantCulture),
            r.Currency,
            r.IssuedAtUtc.ToString("O", CultureInfo.InvariantCulture));

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
