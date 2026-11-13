using RailFlow.Core.Domain;
using RailFlow.Core.Functional;
using RailFlow.Infrastructure.Services;

namespace RailFlow.Tests;

/// <summary>
/// Week 3 (immutable records, equality, ordering), Week 6 (temporal coupling, injected side effect, factories),
/// Week 7 (Result-based workflow) and Week 8 (LINQ reports).
/// </summary>
public class BookingWorkflowTests
{
    private static readonly DateTime IssuedAt = new(2026, 11, 13, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>A fake payment gateway that records every call instead of charging anyone.</summary>
    private sealed class FakeGateway(bool approve = true)
    {
        public List<decimal> Charges { get; } = [];

        public Result<string> Charge(decimal amount)
        {
            Charges.Add(amount);
            return approve ? Result.Ok($"TX-FAKE{Charges.Count:000}") : Result.Fail<string>("Card declined.");
        }
    }

    internal static PricedItinerary PriceLondonParis()
    {
        var chaos = new OperationsControls();
        var orchestrator = new RailwayPipelineOrchestrator(
            new RailMappingSystem(chaos),
            new RailRoutePlanner(chaos),
            new RailTrafficMonitor(chaos),
            new RailPricingCalculator(chaos));

        var report = orchestrator.ExecutePipeline("LONDON", "PARIS", new StandardDayTrip("Single", "Direct", false), 1);
        return Assert.IsType<Something<PricedItinerary>>(report.Outcome).InnerValue;
    }

    internal static BookingReceipt Receipt(string id, decimal amount, string offering = "Single", string origin = "LONDON") =>
        new(id, "TX-" + id, "Test Passenger", "test@example.com", origin, "PARIS", offering, 1, amount, "€", IssuedAt);

    [Fact]
    public void Workflow_ValidDraft_ProducesReceipt_AndChargesExactlyOnce()
    {
        var itinerary = PriceLondonParis(); // 460 km x €0.22 = €101.20
        var gateway = new FakeGateway();
        var draft = new BookingDraft(itinerary, 1, "  Aigerim Sadykova ", "aigerim@example.com");

        var trace = BookingWorkflow.Run(draft, "student10", gateway.Charge, IssuedAt);

        var receipt = Assert.IsType<Success<BookingReceipt>>(trace.Receipt).Data;
        Assert.Equal("Aigerim Sadykova", receipt.PassengerName);   // trimmed during validation
        Assert.Equal(93.58m, receipt.AmountPaid);                   // 101.20 - 10% + 2.50, rounded
        Assert.Equal("RF-FAKE001", receipt.BookingId);
        Assert.Equal(new[] { 93.58m }, gateway.Charges);
    }

    [Theory]
    [InlineData("A", "a@example.com", "", "Passenger name must have at least 2 characters.")]
    [InlineData("Aigerim", "not-an-email", "", "E-mail address is not valid.")]
    [InlineData("Aigerim", "a@example.com", "FREE100", "Unknown promo code 'FREE100'.")]
    public void Workflow_ExpectedProblems_AreFailures_AndTheGatewayIsNeverCalled(
        string name, string email, string promo, string expectedReason)
    {
        var gateway = new FakeGateway();
        var draft = new BookingDraft(PriceLondonParis(), 1, name, email);

        var trace = BookingWorkflow.Run(draft, promo, gateway.Charge, IssuedAt);

        Assert.Equal(expectedReason, Assert.IsType<Failure<BookingReceipt>>(trace.Receipt).Reason);
        Assert.Empty(gateway.Charges);
    }

    [Fact]
    public void Workflow_DeclinedPayment_FailsAtChargeStage()
    {
        var trace = BookingWorkflow.Run(
            new BookingDraft(PriceLondonParis(), 1, "Aigerim", "a@example.com"), null, new FakeGateway(approve: false).Charge, IssuedAt);

        Assert.True(trace.Validated.IsSuccess());
        Assert.True(trace.Priced.IsSuccess());
        Assert.Equal("Card declined.", Assert.IsType<Failure<BookingReceipt>>(trace.Receipt).Reason);
    }

    [Fact]
    public void SimulatedGateway_RespectsSimulatedPaymentOutage()
    {
        var chaos = new OperationsControls();
        var gateway = new SimulatedPaymentGateway(chaos);

        Assert.True(gateway.Charge(50m).IsSuccess());

        chaos.SimulatePaymentOutage = true;
        Assert.IsType<Failure<string>>(gateway.Charge(50m));
        Assert.Equal(1, gateway.SuccessfulCharges);
    }

    [Fact]
    public void Receipts_AreEqualByBookingId_AndOrderedByAmount()
    {
        var a = Receipt("RF-1", 120m);
        var sameIdDifferentPrice = Receipt("RF-1", 999m);
        var cheaper = Receipt("RF-2", 40m);

        Assert.Equal(a, sameIdDifferentPrice);
        Assert.Equal(a.GetHashCode(), sameIdDifferentPrice.GetHashCode());
        Assert.Single(new[] { a, sameIdDifferentPrice }.Distinct());
        Assert.Equal(new[] { cheaper, a }, new[] { a, cheaper }.Order());
    }

    [Fact]
    public void ReceiptLineCodec_RoundTrips_AndRejectsMalformedLines()
    {
        var original = Receipt("RF-ABC", 1234.5m) with { OfferingTitle = "Alpine Gourmet & Lounge" };

        var line = ReceiptLineCodec.ToLine(original);
        var parsed = Assert.IsType<Success<BookingReceipt>>(ReceiptLineCodec.FromLine(line)).Data;

        Assert.Equal(original.AmountPaid, parsed.AmountPaid);
        Assert.Equal(original.IssuedAtUtc, parsed.IssuedAtUtc);
        Assert.Equal(original.OfferingTitle, parsed.OfferingTitle);
        Assert.IsType<Failure<BookingReceipt>>(ReceiptLineCodec.FromLine("garbage|line"));
        Assert.IsType<Failure<BookingReceipt>>(ReceiptLineCodec.FromLine(line.Replace("1234.5", "lots")));
    }

    [Fact]
    public void ReceiptLineCodec_KeepsOwnerAndTravelDate_AndStillReadsOldLines()
    {
        var original = Receipt("RF-OWN", 99m) with { Owner = "passenger", TravelDate = new DateOnly(2026, 12, 24) };

        var parsed = Assert.IsType<Success<BookingReceipt>>(ReceiptLineCodec.FromLine(ReceiptLineCodec.ToLine(original))).Data;
        Assert.Equal("passenger", parsed.Owner);
        Assert.Equal(new DateOnly(2026, 12, 24), parsed.TravelDate);

        // A line saved before the two fields existed: 11 fields, no owner, no travel date.
        var oldLine = string.Join('|', ReceiptLineCodec.ToLine(original).Split('|').Take(11));
        var old = Assert.IsType<Success<BookingReceipt>>(ReceiptLineCodec.FromLine(oldLine)).Data;
        Assert.Equal("", old.Owner);
        Assert.Null(old.TravelDate);
    }

    [Theory]
    [InlineData(TicketFormat.PlainText, "RAILFLOW E-TICKET")]
    [InlineData(TicketFormat.Csv, "booking_id,passenger")]
    [InlineData(TicketFormat.Json, "\"BookingId\": \"RF-9\"")]
    public void TicketExportFactory_ReturnsAFormatterForEachFormat(TicketFormat format, string expectedFragment)
    {
        var export = TicketExportFactory.Create(format);

        Assert.Contains(expectedFragment, export(Receipt("RF-9", 10m)));
    }

    [Fact]
    public void BookingQueries_BuildReports_AndHandleEmptyData()
    {
        var receipts = new[]
        {
            Receipt("RF-1", 100m, "Single"),
            Receipt("RF-2", 300m, "Holiday", origin: "BERLIN"),
            Receipt("RF-3", 50m, "Single"),
        };

        Assert.Equal(450m, receipts.TotalRevenue());
        Assert.Equal(150m, receipts.ForOrigin("london").TotalRevenue());
        Assert.Equal(150m, Assert.IsType<Something<decimal>>(receipts.AverageTicketPrice()).InnerValue);
        Assert.Equal("Holiday", receipts.RevenueByOffering()[0].OfferingTitle);
        Assert.Equal(new[] { "RF-2", "RF-1" }, receipts.MostExpensive(2).Select(r => r.BookingId));

        Assert.IsAssignableFrom<Nothing<decimal>>(Array.Empty<BookingReceipt>().AverageTicketPrice());
    }

    [Fact]
    public void MergeDistinct_CollapsesTheSameBookingFromTwoStores()
    {
        var fromText = new[] { Receipt("RF-1", 10m), Receipt("RF-2", 20m) };
        var fromJson = new[] { Receipt("RF-2", 20m), Receipt("RF-3", 30m) };

        Assert.Equal(3, BookingQueries.MergeDistinct(fromText, fromJson).Count);
    }

    [Fact]
    public void Workflow_TravelDateInThePast_IsRefused_BeforeAnyCharge()
    {
        var gateway = new FakeGateway();
        var yesterday = DateOnly.FromDateTime(IssuedAt).AddDays(-1);
        var draft = new BookingDraft(PriceLondonParis(), 1, "Aigerim Sadykova", "aigerim@example.com", TravelDate: yesterday);

        var trace = BookingWorkflow.Run(draft, "", gateway.Charge, IssuedAt);

        Assert.Equal("The travel date is in the past.", Assert.IsType<Failure<ValidatedBooking>>(trace.Validated).Reason);
        Assert.Empty(gateway.Charges);
    }

    [Fact]
    public void Workflow_TravelDateToday_IsAccepted()
    {
        var draft = new BookingDraft(PriceLondonParis(), 1, "Aigerim Sadykova", "aigerim@example.com",
            TravelDate: DateOnly.FromDateTime(IssuedAt));

        Assert.IsType<Success<BookingReceipt>>(BookingWorkflow.Run(draft, "", new FakeGateway().Charge, IssuedAt).Receipt);
    }
}
