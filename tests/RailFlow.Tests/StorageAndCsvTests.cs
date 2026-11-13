using RailFlow.Core.Domain;
using RailFlow.Core.Functional;
using RailFlow.Infrastructure.Services;
using RailFlow.Infrastructure.Storage;

namespace RailFlow.Tests;

/// <summary>
/// Integration tests against the real file system (each test uses its own temporary folder):
/// Week 4 (files and serialization behind one interface), Week 6 (factory) and Week 10 (CSV import at the boundary).
/// </summary>
public sealed class StorageAndCsvTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "railflow-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(JournalFormat.TextLog)]
    [InlineData(JournalFormat.Json)]
    public void Journal_MissingFile_IsAnEmptyHistory_NotAnError(JournalFormat format)
    {
        var journal = BookingJournalFactory.Create(format, _directory);

        var loaded = Assert.IsType<Success<JournalContents>>(journal.LoadAll()).Data;

        Assert.Empty(loaded.Receipts);
        Assert.False(File.Exists(journal.FilePath));
    }

    [Theory]
    [InlineData(JournalFormat.TextLog)]
    [InlineData(JournalFormat.Json)]
    public void Journal_Append_AddsWithoutReplacing_AndNeverDuplicates(JournalFormat format)
    {
        IBookingJournal journal = BookingJournalFactory.Create(format, _directory);
        var first = BookingWorkflowTests.Receipt("RF-1", 10m);
        var second = BookingWorkflowTests.Receipt("RF-2", 20m);

        Assert.Equal(1, Assert.IsType<Success<int>>(journal.Append([first])).Data);
        Assert.Equal(1, Assert.IsType<Success<int>>(journal.Append([first, second, second])).Data);
        Assert.Equal(0, Assert.IsType<Success<int>>(journal.Append([first])).Data);

        // A brand-new instance reads what the previous one wrote.
        var reloaded = Assert.IsType<Success<JournalContents>>(BookingJournalFactory.Create(format, _directory).LoadAll()).Data;
        Assert.Equal(new[] { "RF-1", "RF-2" }, reloaded.Receipts.Select(r => r.BookingId).Order());
    }

    [Fact]
    public void TextJournal_SkipsCorruptLines_InsteadOfFailingTheWholeFile()
    {
        var journal = new TextBookingJournal(Path.Combine(_directory, "bookings.log"));
        journal.Append([BookingWorkflowTests.Receipt("RF-1", 10m)]);
        File.AppendAllLines(journal.FilePath, ["this line was damaged"]);

        var loaded = Assert.IsType<Success<JournalContents>>(journal.LoadAll()).Data;

        Assert.Single(loaded.Receipts);
        Assert.Single(loaded.SkippedLines);
    }

    [Fact]
    public void JsonJournal_InvalidJson_IsReportedAsFailure_NotThrown()
    {
        Directory.CreateDirectory(_directory);
        var journal = new JsonBookingJournal(Path.Combine(_directory, "bookings.json"));
        File.WriteAllText(journal.FilePath, "{ not json");

        var failure = Assert.IsType<Failure<JournalContents>>(journal.LoadAll());

        Assert.Contains("bookings.json", failure.Reason);
    }

    [Fact]
    public void NetworkCsv_ParsesValidLines_AndCollectsRejectionsWithLineNumbers()
    {
        var lines = new[]
        {
            "# comment",
            "code,latitude,longitude",
            "LONDON,51.53,-0.12",
            "ATLANTIS,abc,0",
            "",
            "PARIS,148.88,2.35",
            "ZURICH,47.38,8.54,EXTRA"
        };

        var report = NetworkCsv.ParseAll(lines, NetworkCsv.StationsHeader, "test", NetworkCsv.ParseStation);

        Assert.Equal("LONDON", Assert.Single(report.Accepted).StationCode);
        Assert.Equal(new[] { 4, 6, 7 }, report.Rejected.Select(r => r.LineNumber));
        Assert.Contains("Column latitude: 'abc' is not a number", report.Rejected[0].Reason);
        Assert.Contains("outside the range", report.Rejected[1].Reason);
    }

    [Fact]
    public void NetworkDataset_RejectsConnectionsToUnknownStations()
    {
        var stations = NetworkCsv.ParseAll(["A-ONE,1,1", "B-TWO,2,2"], NetworkCsv.StationsHeader, "s", NetworkCsv.ParseStation);
        var connections = NetworkCsv.ParseAll(
            ["A-ONE,B-TWO,10,5,Line 1", "A-ONE,NOWHERE,10,5,Line 2"], NetworkCsv.ConnectionsHeader, "c", NetworkCsv.ParseConnection);

        var dataset = NetworkDataset.Build(stations, connections, "test");

        Assert.Single(dataset.Connections);
        Assert.Contains(dataset.Rejected, r => r.Reason.Contains("unknown station"));
    }

    [Fact]
    public void NetworkDataLoader_ReadsCsvFiles_AndRoutesAcrossTheLoadedNetwork()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllLines(Path.Combine(_directory, NetworkDataLoader.StationsFileName),
            ["code,latitude,longitude", "ASTANA,51.17,71.43", "ALMATY,43.24,76.89", "SHYMKENT,42.32,69.59"]);
        File.WriteAllLines(Path.Combine(_directory, NetworkDataLoader.ConnectionsFileName),
            ["from,to,distance_km,minutes,line", "ASTANA,ALMATY,1210,840,Talgo", "ALMATY,SHYMKENT,690,600,Intercity"]);

        var dataset = Assert.IsType<Success<NetworkDataset>>(NetworkDataLoader.LoadFromDirectory(_directory)).Data;
        var chaos = new ChaosAndTrafficController();
        var orchestrator = new RailwayPipelineOrchestrator(
            new RailMappingSystem(chaos, dataset),
            new RailRoutePlanner(chaos, dataset),
            new RailTrafficMonitor(chaos),
            new RailPricingCalculator(chaos));

        var report = orchestrator.ExecutePipeline("ASTANA", "SHYMKENT", new StandardDayTrip("Single", "Direct", false), 1);

        var itinerary = Assert.IsType<Something<PricedItinerary>>(report.Outcome).InnerValue;
        Assert.Equal(1900.0, itinerary.TrafficRoute.Route.TotalDistanceKm);
        Assert.Equal(2, itinerary.TrafficRoute.Route.Segments.Count);
    }

    [Fact]
    public void NetworkDataLoader_FallsBackToBuiltInNetwork_WhenFilesAreMissing()
    {
        var dataset = NetworkDataLoader.LoadOrEmbedded(Path.Combine(_directory, "does-not-exist"));

        Assert.Equal(12, dataset.Stations.Count);
        Assert.Contains("fallback", dataset.Source);
        Assert.Contains(dataset.Rejected, r => r.Reason.Contains("was not found"));
    }

    [Fact]
    public void EndToEnd_PriceBookAndStore_ThenReadBackThroughTheOtherFormat()
    {
        var chaos = new ChaosAndTrafficController();
        var gateway = new SimulatedPaymentGateway(chaos);
        var journalFor = BookingJournalFactory.ForDirectory(_directory);

        var stored = BookingWorkflow
            .Run(new BookingDraft(BookingWorkflowTests.PriceLondonParis(), 1, "Alimzhan", "alimzhan@example.com"),
                "RAILFLOW20", gateway.Charge, DateTime.UtcNow)
            .Receipt
            .Bind(receipt => journalFor(JournalFormat.Json).Append([receipt]).Map(_ => receipt));

        var receipt = Assert.IsType<Success<BookingReceipt>>(stored).Data;

        // Copy JSON -> text log, then read the text log back.
        var fromJson = Assert.IsType<Success<JournalContents>>(journalFor(JournalFormat.Json).LoadAll()).Data.Receipts;
        journalFor(JournalFormat.TextLog).Append(fromJson);
        var fromText = Assert.IsType<Success<JournalContents>>(journalFor(JournalFormat.TextLog).LoadAll()).Data.Receipts;

        Assert.Equal(receipt, Assert.Single(fromText));
        Assert.Equal(83.46m, receipt.AmountPaid); // 101.20 - 20% + 2.50
    }
}
