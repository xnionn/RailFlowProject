using System.Globalization;
using RailFlow.Core.Domain;
using RailFlow.Core.Functional;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using RailFlow.Infrastructure.Security;
using RailFlow.Infrastructure.Services;
using RailFlow.Infrastructure.Storage;
using RailFlow.Web.Components;
using RailFlow.Web.Hubs;

var builder = WebApplication.CreateBuilder(args);

// One display culture for every visitor, so prices, distances and times always look the same
// (2,058.56 and 08:05), whatever language the server or the browser is set to.
var displayCulture = CultureInfo.GetCultureInfo("en-GB");
CultureInfo.DefaultThreadCurrentCulture = displayCulture;
CultureInfo.DefaultThreadCurrentUICulture = displayCulture;

// Imperative shell configuration: where the network CSV files are read from and where bookings are stored.
// Both can be overridden in configuration (appsettings, environment variables such as Storage__BookingDirectory).
var networkDataDirectory = builder.Configuration["Storage:NetworkDataDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "Data");
var bookingStorageDirectory = builder.Configuration["Storage:BookingDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");

// Add Blazor Interactive Server & SignalR
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR();

// Sign-in: cookie authentication over the demo accounts in appsettings.json (passwords stored as PBKDF2 hashes).
builder.Services.AddSingleton(_ => new DemoUserStore(
    builder.Configuration.GetSection("DemoUsers").Get<List<DemoUserEntry>>() ?? []));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.Cookie.Name = "RailFlow.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        // API clients get status codes instead of a redirect to the login page.
        options.Events.OnRedirectToLogin = context => RespondOrRedirect(context, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => RespondOrRedirect(context, StatusCodes.Status403Forbidden);
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// Keys that encrypt the sign-in cookie and antiforgery tokens are kept next to the bookings,
// so users stay signed in after a restart or a container re-deploy.
builder.Services.AddDataProtection()
    .SetApplicationName("RailFlow")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(bookingStorageDirectory, "keys")));

builder.Services.AddHealthChecks();

// Register Functional Core & Imperative Shell Services
builder.Services.AddSingleton<ChaosAndTrafficController>();
builder.Services.AddSingleton(_ => NetworkDataLoader.LoadOrEmbedded(networkDataDirectory));
builder.Services.AddSingleton<RailMappingSystem>();
builder.Services.AddSingleton<IMappingSystem>(sp => sp.GetRequiredService<RailMappingSystem>());
builder.Services.AddSingleton<RailRoutePlanner>();
builder.Services.AddSingleton<IRoutePlanner>(sp => sp.GetRequiredService<RailRoutePlanner>());
builder.Services.AddSingleton<ITrafficMonitor, RailTrafficMonitor>();
builder.Services.AddSingleton<IPricingCalculator, RailPricingCalculator>();
builder.Services.AddSingleton<RailwayPipelineOrchestrator>();

// Booking side: payment gateway (the only side effect) and the journal factory with the storage directory captured (Week 6)
builder.Services.AddSingleton<SimulatedPaymentGateway>();
builder.Services.AddSingleton<Func<JournalFormat, IBookingJournal>>(_ => BookingJournalFactory.ForDirectory(bookingStorageDirectory));

// Scoped Immutable State Container with Time-Travel Undo/Redo per user circuit
builder.Services.AddScoped<ImmutableAppStateContainer>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Friendly HTML error pages for the browser only; API clients get the bare status code (401, 403, 404...).
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture(displayCulture),
    SupportedCultures = [displayCulture],
    SupportedUICultures = [displayCulture]
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

// Liveness probe for Docker / hosting platforms.
app.MapHealthChecks("/healthz");

// Map SignalR Real-Time Telemetry Hub
app.MapHub<TrafficHub>("/hubs/traffic");

// Every traffic alert (from the Blazor UI or from a hub client) is pushed to all connected hub clients.
var trafficHub = app.Services.GetRequiredService<IHubContext<TrafficHub>>();
app.Services.GetRequiredService<ChaosAndTrafficController>().OnTrafficAlertBroadcast +=
    message => _ = trafficHub.Clients.All.SendAsync("ReceiveAdvisory", message);

// Minimal API health & functional pipeline endpoint for external integration
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

// Sign-out is a POST with an antiforgery token (the [FromForm] parameter turns validation on), so other sites cannot log users out.
app.MapPost("/account/logout", async (HttpContext context, [FromForm] string? returnUrl) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/");
});

// All stored bookings from both journals; duplicates collapse because receipts are equal by BookingId.
// Contains passenger names and e-mails, so only dispatchers may read it.
app.MapGet("/api/bookings", (Func<JournalFormat, IBookingJournal> journalFactory) =>
{
    var loads = Enum.GetValues<JournalFormat>().Select(f => journalFactory(f).LoadAll()).ToList();
    var failure = loads.OfType<Failure<JournalContents>>().FirstOrDefault();
    if (failure is not null)
        return Results.Problem(detail: failure.Reason, title: "Booking storage unavailable");

    var receipts = BookingQueries.MergeDistinct(
        loads.OfType<Success<JournalContents>>().Select(s => s.Data.Receipts).ToArray());
    return Results.Ok(receipts);
}).RequireAuthorization(policy => policy.RequireRole(RoleNames.Dispatcher));

// One booking exported through the ticket factory: ?format=PlainText | Csv | Json
// Dispatchers only, like /api/bookings: a ticket shows the passenger's name and e-mail.
// Passengers download their own ticket from the Bookings tab, which builds the file in the page.
app.MapGet("/api/bookings/{bookingId}/ticket", (
    string bookingId,
    TicketFormat? format,
    Func<JournalFormat, IBookingJournal> journalFactory) =>
{
    var chosen = format ?? TicketFormat.PlainText;
    var receipt = Enum.GetValues<JournalFormat>()
        .Select(f => journalFactory(f).LoadAll())
        .OfType<Success<JournalContents>>()
        .SelectMany(s => s.Data.Receipts)
        .FirstOrDefault(r => string.Equals(r.BookingId, bookingId, StringComparison.OrdinalIgnoreCase));

    if (receipt is null)
        return Results.NotFound(new { status = "Nothing", message = $"Booking '{bookingId}' was not found." });

    var (contentType, _) = TicketExportFactory.Describe(chosen);
    return Results.Text(TicketExportFactory.Create(chosen)(receipt), contentType);
}).RequireAuthorization(policy => policy.RequireRole(RoleNames.Dispatcher));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static Task RespondOrRedirect(RedirectContext<CookieAuthenticationOptions> context, int apiStatusCode)
{
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
    {
        context.Response.StatusCode = apiStatusCode;
        return Task.CompletedTask;
    }

    context.Response.Redirect(context.RedirectUri);
    return Task.CompletedTask;
}

/// <summary>
/// Makes the entry point visible to WebApplicationFactory in the integration tests.
/// </summary>
public partial class Program;
