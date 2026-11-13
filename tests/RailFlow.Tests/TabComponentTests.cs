using System.Collections.Immutable;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using RailFlow.Core.Domain;
using RailFlow.Core.Functional;
using RailFlow.Infrastructure.Services;
using RailFlow.Infrastructure.Storage;
using RailFlow.Web.Components.Shared;
using RailFlow.Web.Components.Tabs;

namespace RailFlow.Tests;

/// <summary>
/// bUnit tests for the tab components of the Home page and the small pieces they are built from.
/// The real services from RailFlow.Infrastructure are registered (embedded network, a fresh dispatcher
/// controller per test), and bookings go to a temporary folder that is deleted afterwards.
/// </summary>
public sealed class TabComponentTests : BunitContext
{
    private readonly string _bookingDirectory =
        Path.Combine(Path.GetTempPath(), "railflow-tab-tests-" + Guid.NewGuid().ToString("N"));

    public TabComponentTests()
    {
        Services.AddSingleton<OperationsControls>();
        Services.AddSingleton(EmbeddedNetwork.Dataset);
        Services.AddSingleton(sp => new RailMappingSystem(sp.GetRequiredService<OperationsControls>(), EmbeddedNetwork.Dataset));
        Services.AddSingleton<IMappingSystem>(sp => sp.GetRequiredService<RailMappingSystem>());
        Services.AddSingleton(sp => new RailRoutePlanner(sp.GetRequiredService<OperationsControls>(), EmbeddedNetwork.Dataset));
        Services.AddSingleton<IRoutePlanner>(sp => sp.GetRequiredService<RailRoutePlanner>());
        Services.AddSingleton<ITrafficMonitor, RailTrafficMonitor>();
        Services.AddSingleton<IPricingCalculator, RailPricingCalculator>();
        Services.AddSingleton<RailwayPipelineOrchestrator>();
        Services.AddSingleton<SimulatedPaymentGateway>();
        Services.AddSingleton(BookingJournalFactory.ForDirectory(_bookingDirectory));
        Services.AddScoped<ImmutableAppStateContainer>();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_bookingDirectory))
            Directory.Delete(_bookingDirectory, recursive: true);
    }

    private OperationsControls Operations => Services.GetRequiredService<OperationsControls>();

    // ----- BenchmarkCard -----

    [Fact]
    public void BenchmarkCard_FirstVisit_RunsTheBenchmarkOnce_AndHandsTheResultsBack()
    {
        ImmutableList<BenchmarkEntry>? handedBack = null;

        var cut = Render<BenchmarkCard>(p => p
            .Add(c => c.EntriesChanged, (ImmutableList<BenchmarkEntry> entries) => handedBack = entries));

        Assert.Equal(6, cut.FindAll("tbody tr").Count);
        Assert.NotNull(handedBack);
        Assert.Equal(6, handedBack.Count);
    }

    [Fact]
    public void BenchmarkCard_WithEarlierResults_DoesNotRunAgainUntilTheButtonIsClicked()
    {
        var earlier = ImmutableList.Create(new BenchmarkEntry("Cached run", "Loop", new ComputedResult(42), 1.5, 1, "from last visit"));
        var calls = 0;

        var cut = Render<BenchmarkCard>(p => p
            .Add(c => c.Entries, earlier)
            .Add(c => c.EntriesChanged, (ImmutableList<BenchmarkEntry> _) => calls++));

        Assert.Single(cut.FindAll("tbody tr"));
        Assert.Contains("Cached run", cut.Markup);
        Assert.Equal(0, calls);

        cut.Find("button").Click();

        Assert.Equal(6, cut.FindAll("tbody tr").Count);
        Assert.Equal(1, calls);
    }

    // ----- DispatcherTab -----

    [Fact]
    public void DispatcherTab_Passenger_SeesTheNoticeAndNoControls()
    {
        var cut = Render<DispatcherTab>(p => p
            .Add(c => c.Feed, ImmutableList<string>.Empty)
            .AddCascadingValue(UserRole.Passenger));

        Assert.Contains("Dispatchers only", cut.Find(".alert").TextContent);
        Assert.Empty(cut.FindAll("input[type=checkbox]"));
    }

    [Fact]
    public void DispatcherTab_WithoutACascadedRole_IsLockedLikeForAPassenger()
    {
        var cut = Render<DispatcherTab>(p => p.Add(c => c.Feed, ImmutableList<string>.Empty));

        Assert.Contains("Dispatchers only", cut.Markup);
        Assert.Empty(cut.FindAll("input[type=checkbox]"));
    }

    [Fact]
    public void DispatcherTab_Dispatcher_SeesOneCheckboxPerStationAndFiveFailureSwitches()
    {
        var stations = Services.GetRequiredService<IMappingSystem>().GetAllStations().Count;

        var cut = Render<DispatcherTab>(p => p
            .Add(c => c.Feed, ImmutableList<string>.Empty)
            .AddCascadingValue(UserRole.Dispatcher));

        Assert.DoesNotContain("Dispatchers only", cut.Markup);
        Assert.Equal(stations, cut.FindAll(".check-grid input[type=checkbox]").Count);
        Assert.Equal(5, cut.FindAll(".check-list input[type=checkbox]").Count);
        Assert.True(cut.FindAll(".check-grid label.check").Single(l => l.TextContent.Trim() == "Lyon")
            .QuerySelector("input")!.HasAttribute("checked"));
    }

    [Fact]
    public async Task DispatcherTab_ToggleByAPassenger_ChangesNothingOnTheServer()
    {
        var broadcasts = new List<string>();
        Operations.OnTrafficAlertBroadcast += broadcasts.Add;

        var cut = Render<DispatcherTab>(p => p
            .Add(c => c.Feed, ImmutableList<string>.Empty)
            .AddCascadingValue(UserRole.Passenger));

        // The checkboxes are not there for a passenger, so call the handler directly, as a forged event would.
        await cut.InvokeAsync(() => cut.Instance.ToggleEngineeringWorks("PARIS"));

        Assert.False(Operations.HasEngineeringWorksAt("PARIS"));
        Assert.Empty(broadcasts);
    }

    [Fact]
    public void DispatcherTab_ToggleByADispatcher_StartsWorksAndTellsEveryone()
    {
        var broadcasts = new List<string>();
        Operations.OnTrafficAlertBroadcast += broadcasts.Add;

        var cut = Render<DispatcherTab>(p => p
            .Add(c => c.Feed, ImmutableList<string>.Empty)
            .Add(c => c.SignedInName, "Dana")
            .AddCascadingValue(UserRole.Dispatcher));

        cut.FindAll(".check-grid label.check").Single(l => l.TextContent.Trim() == "Paris")
            .QuerySelector("input")!.Change(true);

        Assert.True(Operations.HasEngineeringWorksAt("PARIS"));
        Assert.Contains("Dispatcher Dana", Assert.Single(broadcasts));
    }

    [Fact]
    public void DispatcherTab_Feed_ShowsTheTimeSeparatelyFromTheText()
    {
        var cut = Render<DispatcherTab>(p => p
            .Add(c => c.Feed, ImmutableList.Create("[08:15:00] Works started at LYON.", "no timestamp")));

        var items = cut.FindAll("ul.activity li");
        Assert.Equal("08:15:00", items[0].QuerySelector("time")!.TextContent);
        Assert.EndsWith("Works started at LYON.", items[0].TextContent);
        Assert.Equal("", items[1].QuerySelector("time")!.TextContent);
        Assert.EndsWith("no timestamp", items[1].TextContent);
    }

    // ----- StageList -----

    [Fact]
    public void StageList_NumbersTheStagesAndColoursThemByStatus()
    {
        var cut = Render<StageList>(p => p.Add(c => c.Stages, ImmutableList.Create(
            new StageView("Check details", StageStatus.Done, "Ada, 2 passengers"),
            new StageView("Take payment", StageStatus.Failed, "Card declined."),
            new StageView("Save booking", StageStatus.Skipped, "Not run"))));

        var rows = cut.FindAll("ol.steps.vertical li.step");
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "1", "2", "3" }, cut.FindAll(".step-num").Select(n => n.TextContent));
        Assert.Contains("ok", rows[0].ClassList);
        Assert.Contains("err", rows[1].ClassList);
        Assert.DoesNotContain("ok", rows[2].ClassList);
        Assert.DoesNotContain("err", rows[2].ClassList);
        Assert.Equal("Card declined.", rows[1].QuerySelector(".step-detail")!.TextContent);
        Assert.Equal("Failed", rows[1].QuerySelector(".tag")!.TextContent);
    }

    [Fact]
    public void StageList_EmptyList_RendersNothing()
    {
        var cut = Render<StageList>(p => p.Add(c => c.Stages, ImmutableList<StageView>.Empty));

        Assert.Equal("", cut.Markup.Trim());
    }

    // ----- BookingsTab -----

    private static BookingReceipt StoredReceipt(string id, string name, int minutesAgo) =>
        new(id, "TX-" + id[3..], name, "someone@example.com", "LONDON", "PARIS", "Single ticket", 1, 120m, "€",
            DateTime.UtcNow.AddMinutes(-minutesAgo));

    private void SeedJournal(params BookingReceipt[] receipts) =>
        Assert.IsType<Success<int>>(BookingJournalFactory.Create(JournalFormat.Json, _bookingDirectory).Append(receipts));

    private PipelineExecutionReport PricedLondonParis() =>
        Services.GetRequiredService<RailwayPipelineOrchestrator>()
            .ExecutePipeline("LONDON", "PARIS", new StandardDayTrip("Single ticket", "One-way rail fare", false), 1);

    [Fact]
    public void BookingsTab_SignedInPassenger_SeesTheBookingsOfTheirAccount_InANewSession()
    {
        SeedJournal(
            StoredReceipt("RF-MINE00001", "Ada Lovelace", 5) with { Owner = "passenger", TravelDate = new DateOnly(2026, 12, 24) },
            StoredReceipt("RF-OTHER0001", "Grace Hopper", 3) with { Owner = "someone-else" });

        // A fresh state, as after a reload: nothing was booked in this tab.
        var cut = Render<BookingsTab>(p => p
            .Add(c => c.State, new BookingsTabState())
            .Add(c => c.Owner, "passenger")
            .AddCascadingValue(UserRole.Passenger));

        var row = Assert.Single(cut.FindAll("tbody tr"));
        Assert.Contains("Ada Lovelace", row.TextContent);
        Assert.Contains("24 Dec 2026", row.TextContent);
        Assert.DoesNotContain("Grace Hopper", cut.Markup);
    }

    [Fact]
    public void BookingsTab_Passenger_DoesNotSeeOtherPeoplesBookings_OrTheRevenueReport()
    {
        SeedJournal(StoredReceipt("RF-ABCDEF123", "Grace Hopper", 5));

        var cut = Render<BookingsTab>(p => p
            .Add(c => c.State, new BookingsTabState())
            .AddCascadingValue(UserRole.Passenger));

        Assert.Empty(cut.FindAll("tbody tr"));
        Assert.DoesNotContain("Grace Hopper", cut.Markup);
        Assert.DoesNotContain("RF-ABCDEF123", cut.Markup);
        Assert.DoesNotContain("Revenue", cut.Markup);
        Assert.Contains("You have not booked anything yet.", cut.Markup);
        Assert.Contains("No booking selected.", cut.Markup);
    }

    [Fact]
    public void BookingsTab_ShowsTheAmountToPay_BeforePayment_AndUpdatesItForAPromoCode()
    {
        var report = PricedLondonParis();
        var fare = Assert.IsType<Something<PricedItinerary>>(report.Outcome).InnerValue!.FinalPrice;

        var cut = Render<BookingsTab>(p => p
            .Add(c => c.State, new BookingsTabState())
            .Add(c => c.Report, report)
            .Add(c => c.PricedPassengers, 1)
            .AddCascadingValue(UserRole.Passenger));

        string ToPay() => cut.Find(".breakdown tr.total td:last-child").TextContent;
        Assert.Equal(UiTextMoney(fare + BookingWorkflow.ServiceFee), ToPay());

        cut.Find("#promoCode").Input("STUDENT10");
        Assert.Equal(UiTextMoney(BookingWorkflow.RoundToCents(fare * 0.9m) + BookingWorkflow.ServiceFee), ToPay());

        cut.Find("#promoCode").Input("NOPE");
        Assert.Contains("Unknown promo code 'NOPE'.", cut.Markup);
        Assert.Empty(cut.FindAll(".breakdown"));
    }

    private static string UiTextMoney(decimal amount) => RailFlow.Web.UiText.Money(amount);

    [Fact]
    public void BookingsTab_Dispatcher_SeesEverything_AndSelectsRowsWithTheKeyboard()
    {
        SeedJournal(StoredReceipt("RF-NEWER00001", "Grace Hopper", 1), StoredReceipt("RF-OLDER00002", "Alan Turing", 30));

        var cut = Render<BookingsTab>(p => p
            .Add(c => c.State, new BookingsTabState())
            .AddCascadingValue(UserRole.Dispatcher));

        var rows = cut.FindAll("tbody tr");
        Assert.Contains("Grace Hopper", rows[0].TextContent);
        Assert.Contains("RF-OLDER00002", rows[1].TextContent);
        Assert.Equal("true", rows[0].GetAttribute("aria-selected")); // the newest visible booking is preselected

        rows[1].KeyDown(new KeyboardEventArgs { Key = " " });

        rows = cut.FindAll("tbody tr");
        Assert.Equal("false", rows[0].GetAttribute("aria-selected"));
        Assert.Equal("true", rows[1].GetAttribute("aria-selected"));
        Assert.Contains("RF-OLDER00002", cut.Find("pre.output").TextContent);
        Assert.StartsWith("data:", cut.Find("a[download]").GetAttribute("href"));
    }

    [Fact]
    public void BookingsTab_Booking_ChargesOnce_ShowsConfirmationAndTicket_AndGatesStagesBehindCourseNotes()
    {
        SeedJournal(StoredReceipt("RF-ABCDEF123", "Grace Hopper", 5));
        var report = PricedLondonParis();
        var state = new BookingsTabState();
        var gateway = Services.GetRequiredService<SimulatedPaymentGateway>();
        var travelDate = DateOnly.FromDateTime(DateTime.Today.AddDays(2));
        var departure = new TimeOnly(9, 15);

        var cut = Render<BookingsTab>(p => p
            .Add(c => c.State, state)
            .Add(c => c.Report, report)
            .Add(c => c.PricedPassengers, 1)
            .Add(c => c.TravelDate, travelDate)
            .Add(c => c.DepartureTime, departure)
            .Add(c => c.Origin, "LONDON")
            .AddCascadingValue(UserRole.Passenger));

        cut.Find("#passengerName").Change("Ada Lovelace");
        cut.Find("#passengerEmail").Change("ada@example.com");
        cut.FindAll("button").Single(b => b.TextContent == "Confirm and pay").Click();

        Assert.Equal(1, gateway.SuccessfulCharges);
        // After purchase, the editable form is replaced by the confirmation card and "Plan another trip" CTA.
        Assert.Contains("Booking confirmed", cut.Find("h2").TextContent);
        Assert.Empty(cut.FindAll("#passengerName"));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent == "Plan another trip");
        Assert.True(cut.FindAll("button").Single(b => b.TextContent == "Booked").HasAttribute("disabled"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent == "Confirm and pay");

        // Technical 4-stage list is hidden on success unless Course notes are enabled.
        Assert.Empty(cut.FindAll("ol.steps.vertical"));

        var ownId = Assert.Single(state.OwnBookingIds);
        Assert.Contains(ownId, cut.Find(".ticket-pass").TextContent);
        Assert.Contains("09:15", cut.Find(".ticket-pass").TextContent);
        Assert.Contains(ownId, cut.Find("pre.output").TextContent);
        Assert.Contains("Ada Lovelace", cut.Markup);
        Assert.DoesNotContain("Grace Hopper", cut.Markup);

        // Enabling Course notes reveals the four-stage workflow list.
        var withNotes = Render<BookingsTab>(p => p
            .Add(c => c.State, state)
            .Add(c => c.Report, report)
            .Add(c => c.PricedPassengers, 1)
            .Add(c => c.TravelDate, travelDate)
            .Add(c => c.DepartureTime, departure)
            .Add(c => c.Origin, "LONDON")
            .AddCascadingValue(UserRole.Passenger)
            .AddCascadingValue(new ViewOptions(ShowCourseNotes: true)));
        Assert.Equal(new[] { "Done", "Done", "Done", "Done" }, withNotes.FindAll("ol.steps.vertical .tag").Select(t => t.TextContent));
    }

    [Fact]
    public void BookingsTab_BookedTicket_RemainsIndependentOfSubsequentPlannerEdits()
    {
        var state = new BookingsTabState();
        var travelDate = DateOnly.FromDateTime(DateTime.Today.AddDays(3));
        var departure = new TimeOnly(8, 30);

        var cut = Render<BookingsTab>(p => p
            .Add(c => c.State, state)
            .Add(c => c.Report, PricedLondonParis())
            .Add(c => c.PricedPassengers, 2)
            .Add(c => c.TravelDate, travelDate)
            .Add(c => c.DepartureTime, departure)
            .Add(c => c.Origin, "LONDON")
            .AddCascadingValue(UserRole.Passenger));

        cut.Find("#passengerName").Change("Ada Lovelace");
        cut.Find("#passengerEmail").Change("ada@example.com");
        cut.FindAll("button").Single(b => b.TextContent == "Confirm and pay").Click();

        // Simulate later planner edits (different route, date, departure, passenger count).
        var berlinVienna = Services.GetRequiredService<RailwayPipelineOrchestrator>()
            .ExecutePipeline("BERLIN", "VIENNA", new StandardDayTrip("Single ticket", "One-way rail fare", false), 4);

        cut.Render(p => p
            .Add(c => c.Report, berlinVienna)
            .Add(c => c.PricedPassengers, 4)
            .Add(c => c.TravelDate, travelDate.AddDays(10))
            .Add(c => c.DepartureTime, new TimeOnly(16, 45))
            .Add(c => c.SearchId, 2)
            .Add(c => c.Origin, "BERLIN"));

        var ticket = cut.Find(".ticket-pass").TextContent;
        Assert.Contains("London", ticket);
        Assert.Contains("Paris", ticket);
        Assert.Contains("08:30", ticket);
        Assert.Contains("10:45", ticket);
        Assert.Contains("2 passengers", ticket);
        Assert.DoesNotContain("Berlin", ticket);
    }

    [Fact]
    public void PlanTab_StaleSearchGuard_MarksEditedForm_AndBlocksBookingUntilSearchedAgain()
    {
        var inputs = new PlannerInputs
        {
            Origin = "LONDON",
            Destination = "PARIS",
            Passengers = 1,
            OfferingKey = "DayTripSingle",
            TravelDate = DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
            Departure = new TimeOnly(8, 0)
        };
        var snapshot = inputs.ToSnapshot(DateTime.UtcNow);
        var searched = 0;

        var cut = Render<PlanTab>(p => p
            .Add(c => c.Inputs, inputs)
            .Add(c => c.Report, PricedLondonParis())
            .Add(c => c.PricedPassengers, 1)
            .Add(c => c.PricedSnapshot, snapshot)
            .Add(c => c.OnSearch, () => searched++));

        var bookButton = cut.FindAll("button").Single(b => b.TextContent == "Book this trip");
        Assert.False(bookButton.HasAttribute("disabled"));
        Assert.DoesNotContain("Outdated quote", cut.Markup);

        // Edit destination from Paris to Milan without running search.
        cut.Find("#destination").Change("Milan");

        Assert.Contains("Outdated quote", cut.Markup);
        Assert.Contains("Form edited since last search", cut.Markup);
        Assert.True(cut.FindAll("button").Single(b => b.TextContent == "Book this trip").HasAttribute("disabled"));

        // Clicking "Update route and price" triggers a fresh search.
        cut.FindAll("button").Single(b => b.TextContent == "Update route and price").Click();
        Assert.Equal(1, searched);
    }

    [Fact]
    public void BookingsTab_StaleSearch_PromptsReturnToPlanner_AndRefusesPayment()
    {
        var gateway = Services.GetRequiredService<SimulatedPaymentGateway>();
        var planned = 0;

        var cut = Render<BookingsTab>(p => p
            .Add(c => c.State, new BookingsTabState { PassengerName = "Ada Lovelace", PassengerEmail = "ada@example.com" })
            .Add(c => c.Report, PricedLondonParis())
            .Add(c => c.PricedPassengers, 1)
            .Add(c => c.IsStaleSearch, true)
            .Add(c => c.OnPlanTrip, () => planned++)
            .AddCascadingValue(UserRole.Passenger));

        Assert.Contains("Trip form changed since your last search", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent == "Confirm and pay");
        Assert.Equal(0, gateway.SuccessfulCharges);

        cut.FindAll("button").Single(b => b.TextContent == "Return to planner").Click();
        Assert.Equal(1, planned);
    }

    [Fact]
    public void BookingsTab_SameSearchPricedAgain_StaysBooked_ButANewSearchCanBeBooked()
    {
        var state = new BookingsTabState();
        var gateway = Services.GetRequiredService<SimulatedPaymentGateway>();

        var cut = Render<BookingsTab>(p => p
            .Add(c => c.State, state)
            .Add(c => c.Report, PricedLondonParis())
            .Add(c => c.PricedPassengers, 1)
            .Add(c => c.SearchId, 7)
            .Add(c => c.Origin, "LONDON")
            .AddCascadingValue(UserRole.Passenger));

        cut.Find("#passengerName").Change("Ada Lovelace");
        cut.Find("#passengerEmail").Change("ada@example.com");
        cut.FindAll("button").Single(b => b.TextContent == "Confirm and pay").Click();

        // Engineering works changed somewhere: Home prices the same search again, which is a new report object.
        cut.Render(p => p.Add(c => c.Report, PricedLondonParis()));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent == "Booked");
        Assert.Equal(1, gateway.SuccessfulCharges);

        cut.Render(p => p.Add(c => c.SearchId, 8));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent == "Confirm and pay");
    }

    // ----- NetworkTab, TimetableTab and LabTab -----

    [Fact]
    public void NetworkTab_ListsEveryConnection_MarksEngineeringWorks_AndSupportsFiltering()
    {
        var edges = Services.GetRequiredService<RailRoutePlanner>().GraphEngine.Edges;
        var withWorks = edges.Count(e => Operations.HasEngineeringWorksAt(e.FromStation) || Operations.HasEngineeringWorksAt(e.ToStation));

        var cut = Render<NetworkTab>();

        Assert.Equal(edges.Count, cut.FindAll("tbody tr").Count);
        Assert.Equal(withWorks, cut.FindAll("tbody .tag.warn").Count);
        Assert.True(withWorks > 0);

        cut.Find("#networkFilter").Input("Eurostar");
        Assert.All(cut.FindAll("tbody tr"), row => Assert.Contains("Eurostar", row.TextContent));

        cut.Find("#networkFilter").Input("NoSuchStation");
        Assert.Contains("No connections match your filter.", cut.Find(".empty-state").TextContent);
        cut.FindAll("button").Single(b => b.TextContent == "Reset filter").Click();
        Assert.Equal(edges.Count, cut.FindAll("tbody tr").Count);
    }

    private static readonly PlannedRoute ParisLyon = new("PARIS", "LYON",
        ImmutableList.Create(new RouteSegment("PARIS", "LYON", 400, 120, "TGV")), 400, 120);

    [Fact]
    public void TimetableTab_WithoutAPricedTrip_AsksForASearch()
    {
        var cut = Render<TimetableTab>(p => p.Add(c => c.Route, (PlannedRoute?)null));

        Assert.Contains("to see its timetable", cut.Find(".empty-state").TextContent);
        Assert.Empty(cut.FindAll("table"));
    }

    [Fact]
    public void TimetableTab_FollowsTheRouteAndDepartureTime_AndCollapsesProgressUnlessCourseNotesAreOn()
    {
        var cut = Render<TimetableTab>(p => p
            .Add(c => c.Route, ParisLyon)
            .Add(c => c.Departure, new TimeOnly(9, 30)));

        var stops = cut.FindAll(".card").First().QuerySelectorAll("tbody tr");
        Assert.Equal(2, stops.Length);
        Assert.Contains("Paris", stops[0].TextContent);
        Assert.Contains("11:30", stops[1].TextContent);

        // Journey progress is collapsed by default and opens when Course notes are switched on.
        Assert.False(cut.Find("details.progress-details").HasAttribute("open"));

        // The journey progress ends at the arrival time shown in the timetable.
        Assert.Contains("11:30", cut.FindAll(".card")[1].QuerySelectorAll("tbody tr").Last().TextContent);

        cut.Render(p => p.Add(c => c.Departure, new TimeOnly(10, 0)));
        Assert.Contains("12:00", cut.FindAll(".card").First().QuerySelectorAll("tbody tr")[1].TextContent);

        var withNotes = Render<TimetableTab>(p => p
            .Add(c => c.Route, ParisLyon)
            .Add(c => c.Departure, new TimeOnly(10, 0))
            .AddCascadingValue(new ViewOptions(ShowCourseNotes: true)));
        Assert.True(withNotes.Find("details.progress-details").HasAttribute("open"));
    }

    [Fact]
    public void LabTab_StationCheck_MarksRejectedLinesWithTheirReason()
    {
        var state = new LabTabState { BenchmarkEntries = ImmutableList.Create(new BenchmarkEntry("Cached", "Loop", new ComputedResult(1), 1, 1, "")) };

        var cut = Render<LabTab>(p => p
            .Add(c => c.State, state)
            .Add(c => c.Origin, "LONDON")
            .Add(c => c.Destination, "ZURICH"));

        var rejected = cut.FindAll("tr.rejected");
        Assert.Equal(2, rejected.Count);
        Assert.Contains("Column latitude: 'abc' is not a number.", rejected[0].TextContent);
        Assert.Contains("Expected 3 fields but found 4.", rejected[1].TextContent);
        Assert.Equal(2, state.StationValidation.Accepted.Count);
    }

    [Fact]
    public void LabTab_RepeatedSearch_IsServedFromTheCache()
    {
        var state = new LabTabState { BenchmarkEntries = ImmutableList.Create(new BenchmarkEntry("Cached", "Loop", new ComputedResult(1), 1, 1, "")) };

        var cut = Render<LabTab>(p => p
            .Add(c => c.State, state)
            .Add(c => c.Origin, "LONDON")
            .Add(c => c.Destination, "ZURICH"));

        cut.FindAll("button").Single(b => b.TextContent == "Repeat search 50 times").Click();

        Assert.StartsWith("50 searches for London → Zurich", state.CacheMessage);
        var stats = Services.GetRequiredService<RailRoutePlanner>().GraphEngine.GetCacheStats();
        Assert.True(stats.CacheHits >= 49);
    }

    // ----- Course notes -----

    [Fact]
    public void HowItWorks_IsHiddenUnlessCourseNotesAreSwitchedOn()
    {
        var hidden = Render<HowItWorks>(p => p.AddChildContent("Uses Bind."));
        Assert.Equal("", hidden.Markup.Trim());

        var shown = Render<HowItWorks>(p => p
            .AddChildContent("Uses Bind.")
            .AddCascadingValue(new ViewOptions(ShowCourseNotes: true)));
        Assert.Equal("How it works", shown.Find("details.how summary").TextContent);
        Assert.Contains("Uses Bind.", shown.Markup);
    }

    [Fact]
    public void PlanTab_ShowsTheSearchTraceAndEdgeCases_OnlyWithCourseNotes()
    {
        var report = PricedLondonParis();

        var normal = Render<PlanTab>(p => p
            .Add(c => c.Inputs, new PlannerInputs { Origin = "LONDON", Destination = "PARIS", Passengers = 1 })
            .Add(c => c.Report, report)
            .Add(c => c.PricedPassengers, 1));

        Assert.Contains("London → Paris", normal.Find(".trip-route").TextContent);
        Assert.DoesNotContain("How the search ran", normal.Markup);
        Assert.DoesNotContain("unknown station", normal.Markup);

        var withNotes = Render<PlanTab>(p => p
            .Add(c => c.Inputs, new PlannerInputs { Origin = "LONDON", Destination = "PARIS", Passengers = 1 })
            .Add(c => c.Report, report)
            .Add(c => c.PricedPassengers, 1)
            .AddCascadingValue(new ViewOptions(ShowCourseNotes: true)));

        Assert.Contains("How the search ran", withNotes.Markup);
        Assert.Contains("unknown station", withNotes.Markup);
    }

    [Fact]
    public void PlanTab_StationFields_ShowNames_AndStoreCodes()
    {
        var inputs = new PlannerInputs { Origin = "LONDON", Destination = "PARIS", Passengers = 1 };
        var cut = Render<PlanTab>(p => p
            .Add(c => c.Inputs, inputs)
            .Add(c => c.Report, PricedLondonParis())
            .Add(c => c.PricedPassengers, 1));

        Assert.Equal("London", cut.Find("#origin").GetAttribute("value"));
        Assert.Equal("Paris", cut.Find("#destination").GetAttribute("value"));

        cut.Find("#destination").Change("  zurich ");
        Assert.Equal("ZURICH", inputs.Destination);
    }

    [Fact]
    public void BookingsTab_DamagedBookingFile_StopsBeforePayment()
    {
        Directory.CreateDirectory(_bookingDirectory);
        File.WriteAllText(Path.Combine(_bookingDirectory, "bookings.json"), "{ not json");
        var gateway = Services.GetRequiredService<SimulatedPaymentGateway>();

        var cut = Render<BookingsTab>(p => p
            .Add(c => c.State, new BookingsTabState())
            .Add(c => c.Report, PricedLondonParis())
            .Add(c => c.PricedPassengers, 1)
            .AddCascadingValue(UserRole.Passenger));

        cut.Find("#passengerName").Change("Ada Lovelace");
        cut.Find("#passengerEmail").Change("ada@example.com");
        cut.FindAll("button").Single(b => b.TextContent == "Confirm and pay").Click();

        Assert.Equal(0, gateway.SuccessfulCharges);
        Assert.Contains("You were not charged.", cut.Find("ol.steps.vertical").TextContent);
        Assert.DoesNotContain("System.", cut.Markup);   // a plain sentence, not a .NET type name
    }

    [Fact]
    public void TimetableTab_JourneyPastMidnight_MarksTheNextDay()
    {
        var cut = Render<TimetableTab>(p => p
            .Add(c => c.Route, ParisLyon)
            .Add(c => c.Departure, new TimeOnly(23, 30)));

        var lastProgressRow = cut.FindAll(".card")[1].QuerySelectorAll("tbody tr").Last();
        Assert.Contains("01:30 +1d", lastProgressRow.TextContent);
    }

    [Fact]
    public void BookingsTab_JournalLoadSucceeds_ButAppendFailsAfterPayment_ShowsPersistentRecoveryError_NeverFakeConfirmation_AndRetriesWithoutRecharging()
    {
        var allowAppend = false;
        var realJournal = BookingJournalFactory.Create(JournalFormat.Json, _bookingDirectory);
        var flakyJournal = new AppendControllableJournal(realJournal, () => allowAppend);
        Services.AddSingleton<Func<JournalFormat, IBookingJournal>>(_ => _ => flakyJournal);

        var state = new BookingsTabState();
        var gateway = Services.GetRequiredService<SimulatedPaymentGateway>();
        var report = PricedLondonParis();

        // Course notes OFF by default: the error and stage failure must still be visible.
        var cut = Render<BookingsTab>(p => p
            .Add(c => c.State, state)
            .Add(c => c.Report, report)
            .Add(c => c.PricedPassengers, 1)
            .Add(c => c.SearchId, 10)
            .Add(c => c.Origin, "LONDON")
            .AddCascadingValue(UserRole.Passenger));

        cut.Find("#passengerName").Change("Ada Lovelace");
        cut.Find("#passengerEmail").Change("ada@example.com");
        cut.FindAll("button").Single(b => b.TextContent == "Confirm and pay").Click();

        Assert.Equal(1, gateway.SuccessfulCharges);
        Assert.NotNull(state.UnsavedPaidReceipt);
        var txId = state.UnsavedPaidReceipt!.TransactionId;

        // Must NOT show "Booking confirmed" or "ticket is ready", and must NOT show a fake saved ticket.
        Assert.Equal("Payment taken — booking not saved", cut.Find("h2").TextContent);
        Assert.DoesNotContain("Booking confirmed", cut.Markup);
        Assert.DoesNotContain("Your ticket is ready", cut.Markup);
        Assert.Contains("Payment succeeded, but the booking could not be saved", cut.Find(".booking-unsaved .alert.err").TextContent);
        Assert.Contains(txId, cut.Find(".booking-unsaved").TextContent);
        Assert.NotEmpty(cut.FindAll("ol.steps.vertical li.step.err"));
        Assert.Empty(cut.FindAll(".ticket-pass"));

        // Navigating away/back or repricing the same search preserves the error and does not allow double charging.
        var remounted = Render<BookingsTab>(p => p
            .Add(c => c.State, state)
            .Add(c => c.Report, PricedLondonParis())
            .Add(c => c.PricedPassengers, 1)
            .Add(c => c.SearchId, 10)
            .Add(c => c.Origin, "LONDON")
            .AddCascadingValue(UserRole.Passenger));

        Assert.Equal("Payment taken — booking not saved", remounted.Find("h2").TextContent);
        Assert.Contains(txId, remounted.Find(".booking-unsaved").TextContent);
        Assert.DoesNotContain(remounted.FindAll("button"), b => b.TextContent == "Confirm and pay");
        Assert.Equal(1, gateway.SuccessfulCharges);

        // A dirty form or failed repricing must not hide recovery for an already-taken payment.
        var stale = Render<BookingsTab>(p => p
            .Add(c => c.State, state)
            .Add(c => c.SearchId, 10)
            .Add(c => c.IsStaleSearch, true));
        Assert.Contains(txId, stale.Find(".booking-unsaved").TextContent);
        Assert.Contains(stale.FindAll("button"), b => b.TextContent == "Retry saving booking");

        // Once storage recovers, clicking "Retry saving booking" saves the existing receipt without charging again.
        allowAppend = true;
        remounted.FindAll("button").Single(b => b.TextContent == "Retry saving booking").Click();

        Assert.Equal(1, gateway.SuccessfulCharges);
        Assert.Equal("Booking confirmed", remounted.Find("h2").TextContent);
        Assert.Contains(state.BookedReceipt!.BookingId, remounted.Find(".booking-confirmed").TextContent);
        Assert.Contains(state.BookedReceipt.BookingId, remounted.Find(".ticket-pass").TextContent);
    }

    [Fact]
    public void BookingsTab_ConfirmationUsesImmutableBookedReceipt_WhenOlderHistoryRowIsSelected_OrAfterOperationsReprice()
    {
        var older = StoredReceipt("RF-OLDHIST01", "Ada Lovelace", 60) with
        {
            Owner = "passenger",
            Origin = "BERLIN",
            Destination = "VIENNA",
            OfferingTitle = "Grand tour",
            Passengers = 4,
            AmountPaid = 999.99m,
            TravelDate = new DateOnly(2026, 12, 1),
            DepartureTime = new TimeOnly(14, 0)
        };
        SeedJournal(older);

        var state = new BookingsTabState();
        var travelDate = DateOnly.FromDateTime(DateTime.Today.AddDays(4));
        var departure = new TimeOnly(10, 30);

        var cut = Render<BookingsTab>(p => p
            .Add(c => c.State, state)
            .Add(c => c.Report, PricedLondonParis())
            .Add(c => c.PricedPassengers, 2)
            .Add(c => c.SearchId, 5)
            .Add(c => c.TravelDate, travelDate)
            .Add(c => c.DepartureTime, departure)
            .Add(c => c.Owner, "passenger")
            .Add(c => c.Origin, "LONDON")
            .AddCascadingValue(UserRole.Passenger));

        cut.Find("#passengerName").Change("Ada Lovelace");
        cut.Find("#passengerEmail").Change("ada@example.com");
        cut.FindAll("button").Single(b => b.TextContent == "Confirm and pay").Click();

        var newBookingId = state.BookedReceipt!.BookingId;
        var paidAmount = RailFlow.Web.UiText.Money(state.BookedReceipt.AmountPaid, state.BookedReceipt.Currency);
        Assert.NotEqual("RF-OLDHIST01", newBookingId);

        // Select the older booking in the history table.
        var oldRow = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("RF-OLDHIST01"));
        oldRow.Click();

        // Ticket preview updates to the selected history booking, while confirmation keeps the new booking's ID, route and paid amount.
        Assert.Contains("RF-OLDHIST01", cut.Find(".ticket-pass").TextContent);
        var confirmedBanner = cut.Find(".booking-confirmed").TextContent;
        Assert.Contains(newBookingId, confirmedBanner);
        Assert.DoesNotContain("RF-OLDHIST01", confirmedBanner);
        Assert.Contains("London → Paris", confirmedBanner);
        Assert.Contains(paidAmount, confirmedBanner);

        // Simulate operations price change and re-render with updated report/parameters.
        Operations.ToggleEngineeringWorks("PARIS", "Dispatcher");
        var repriced = Services.GetRequiredService<RailwayPipelineOrchestrator>()
            .ExecutePipeline("LONDON", "PARIS", new RailHoliday("Grand tour", "5 nights", new HotelNights(5), HotelTier.Luxury), 5);

        cut.Render(p => p
            .Add(c => c.Report, repriced)
            .Add(c => c.PricedPassengers, 5));

        confirmedBanner = cut.Find(".booking-confirmed").TextContent;
        Assert.Contains(newBookingId, confirmedBanner);
        Assert.Contains("2 passengers", confirmedBanner);
        Assert.Contains("Single ticket", confirmedBanner);
        Assert.Contains(paidAmount, confirmedBanner);
    }

    private sealed class AppendControllableJournal(IBookingJournal inner, Func<bool> allowAppend) : IBookingJournal
    {
        public JournalFormat Format => inner.Format;

        public string FilePath => inner.FilePath;

        public Result<JournalContents> LoadAll() => inner.LoadAll();

        public Result<int> Append(IEnumerable<BookingReceipt> receipts) =>
            allowAppend()
                ? inner.Append(receipts)
                : Result.Fail<int>("Disk write denied while saving bookings.json.");
    }
}
