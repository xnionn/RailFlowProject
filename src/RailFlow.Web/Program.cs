using RailFlow.Core.Domain;
using RailFlow.Core.Functional;
using RailFlow.Infrastructure.Services;
using RailFlow.Web.Components;
using RailFlow.Web.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Add Blazor Interactive Server & SignalR (Criteria 2, 4, 9, 11, 18)
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR();

// Register Functional Core & Imperative Shell Services (Clean Architecture / SOLID - Criterion 1)
builder.Services.AddSingleton<ChaosAndTrafficController>();
builder.Services.AddSingleton<RailMappingSystem>();
builder.Services.AddSingleton<IMappingSystem>(sp => sp.GetRequiredService<RailMappingSystem>());
builder.Services.AddSingleton<RailRoutePlanner>();
builder.Services.AddSingleton<IRoutePlanner>(sp => sp.GetRequiredService<RailRoutePlanner>());
builder.Services.AddSingleton<ITrafficMonitor, RailTrafficMonitor>();
builder.Services.AddSingleton<IPricingCalculator, RailPricingCalculator>();
builder.Services.AddSingleton<RailwayPipelineOrchestrator>();

// Scoped Immutable State Container with Time-Travel Undo/Redo per user circuit (Criterion 4)
builder.Services.AddScoped<ImmutableAppStateContainer>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();

// Map SignalR Real-Time Telemetry Hub (Criterion 9)
app.MapHub<TrafficHub>("/hubs/traffic");

// Minimal API health & functional pipeline endpoint for external integration (Criterion 16 & 18)
app.MapGet("/api/route-quote", (
    string from,
    string to,
    int passengers,
    RailwayPipelineOrchestrator orchestrator) =>
{
    var offering = new StandardDayTrip("Standard Express", "Direct point-to-point rail ticket", false);
    var report = orchestrator.ExecutePipeline(from, to, offering, passengers <= 0 ? 1 : passengers);
    return report.Outcome switch
    {
        Something<PricedItinerary> { InnerValue: var itinerary } => Results.Ok(new
        {
            status = "Something",
            origin = itinerary.TrafficRoute.Route.Origin,
            destination = itinerary.TrafficRoute.Route.Destination,
            distanceKm = itinerary.TrafficRoute.Route.TotalDistanceKm,
            finalPrice = itinerary.FinalPrice,
            currency = itinerary.Currency,
            hasRoadworks = itinerary.TrafficRoute.HasRoadworksOrTrackMaintenance
        }),
        Nothing<PricedItinerary> => Results.NotFound(new { status = "Nothing", message = "No route or station found." }),
        Error<PricedItinerary> { ErrorDetails: var ex } => Results.Problem(detail: ex.Message, title: "Pipeline Error<T>"),
        _ => Results.StatusCode(500)
    };
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
