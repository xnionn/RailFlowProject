using System.Collections.Immutable;
using RailFlow.Core.Functional;

namespace RailFlow.Core.Domain;

public sealed record RevenueLine(string OfferingTitle, int Bookings, int Passengers, decimal Revenue);

/// <summary>
/// Week 8: domain-specific extension methods built on LINQ, so reports read as one line:
/// <c>receipts.ForOrigin("LONDON").TotalRevenue()</c>.
/// </summary>
public static class BookingQueries
{
    public static IEnumerable<BookingReceipt> ForOrigin(this IEnumerable<BookingReceipt> receipts, string station) =>
        receipts.Filter(r => string.Equals(r.Origin, station, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<BookingReceipt> ForOffering(this IEnumerable<BookingReceipt> receipts, string offeringTitle) =>
        receipts.Filter(r => string.Equals(r.OfferingTitle, offeringTitle, StringComparison.OrdinalIgnoreCase));

    public static decimal TotalRevenue(this IEnumerable<BookingReceipt> receipts) =>
        receipts.Sum(r => r.AmountPaid);

    /// <summary>
    /// Average of an empty sequence throws in LINQ, so "no bookings yet" is returned as Nothing instead.
    /// </summary>
    public static Maybe<decimal> AverageTicketPrice(this IEnumerable<BookingReceipt> receipts)
    {
        var amounts = receipts.Select(r => r.AmountPaid).ToList();
        return amounts.Count == 0
            ? new UnhandledNothing<decimal>()
            : new Something<decimal>(Math.Round(amounts.Average(), 2));
    }

    public static ImmutableList<RevenueLine> RevenueByOffering(this IEnumerable<BookingReceipt> receipts) =>
        receipts
            .GroupBy(r => r.OfferingTitle)
            .Select(g => new RevenueLine(g.Key, g.Count(), g.Sum(r => r.Passengers), g.Sum(r => r.AmountPaid)))
            .OrderByDescending(line => line.Revenue)
            .ToImmutableList();

    /// <summary>
    /// Uses the IComparable ordering of BookingReceipt (by amount paid).
    /// </summary>
    public static ImmutableList<BookingReceipt> MostExpensive(this IEnumerable<BookingReceipt> receipts, int count) =>
        receipts.OrderDescending().Take(count).ToImmutableList();

    public static IEnumerable<string> RouteLabels(this IEnumerable<BookingReceipt> receipts) =>
        receipts.Transform(r => $"{r.Origin} → {r.Destination}");

    /// <summary>
    /// Merges receipts from several stores; duplicates collapse because receipts are equal by BookingId.
    /// </summary>
    public static ImmutableList<BookingReceipt> MergeDistinct(params IEnumerable<BookingReceipt>[] sources) =>
        sources.SelectMany(s => s).Distinct().OrderByDescending(r => r.IssuedAtUtc).ToImmutableList();
}
