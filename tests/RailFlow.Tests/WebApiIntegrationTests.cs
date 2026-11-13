using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RailFlow.Core.Domain;
using RailFlow.Core.Functional;
using RailFlow.Infrastructure.Storage;

namespace RailFlow.Tests;

/// <summary>
/// Starts the real web app in memory (same Program.cs, middleware, authentication and endpoints) and talks to it over HTTP.
/// Bookings are stored in a temporary folder that is deleted afterwards, so the tests never write into the repository.
/// </summary>
public sealed class RailFlowWebFactory : WebApplicationFactory<Program>
{
    public string BookingDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "railflow-web-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Storage:BookingDirectory", BookingDirectory);
    }

    /// <summary>A client that keeps cookies but does not follow redirects, so tests can look at the Location header.</summary>
    public HttpClient CreateBrowserClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(BookingDirectory))
            Directory.Delete(BookingDirectory, recursive: true);
    }
}

public sealed class WebApiIntegrationTests(RailFlowWebFactory factory) : IClassFixture<RailFlowWebFactory>
{
    private const string AuthCookie = "RailFlow.Auth";

    // ----- helpers -----

    private static string HiddenField(string html, string name)
    {
        var input = Regex.Match(html, $"<input[^>]*name=\"{Regex.Escape(name)}\"[^>]*>");
        Assert.True(input.Success, $"Hidden field '{name}' was not found on the page.");
        var value = Regex.Match(input.Value, "value=\"([^\"]*)\"");
        Assert.True(value.Success, $"Hidden field '{name}' has no value.");
        return WebUtility.HtmlDecode(value.Groups[1].Value);
    }

    /// <summary>
    /// Does what a browser does: loads the login page (which sets the antiforgery cookie), copies the hidden fields
    /// of the "login" form and posts them back together with the user name and password.
    /// </summary>
    internal static async Task<HttpResponseMessage> SubmitLoginAsync(
        HttpClient client, string userName, string password, string? returnUrl = null)
    {
        var page = await client.GetStringAsync("/login");

        var form = new Dictionary<string, string>
        {
            ["_handler"] = HiddenField(page, "_handler"),
            ["__RequestVerificationToken"] = HiddenField(page, "__RequestVerificationToken"),
            ["Input.UserName"] = userName,
            ["Input.Password"] = password,
            ["Input.ReturnUrl"] = returnUrl ?? ""
        };

        return await client.PostAsync("/login", new FormUrlEncodedContent(form));
    }

    private async Task<HttpClient> SignedInClientAsync(string userName, string password)
    {
        var client = factory.CreateBrowserClient();
        var response = await SubmitLoginAsync(client, userName, password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    internal static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    private BookingReceipt StoreReceipt(string bookingId, JournalFormat format = JournalFormat.Json)
    {
        var receipt = BookingWorkflowTests.Receipt(bookingId, 93.58m, "Standard Express");
        var journal = BookingJournalFactory.ForDirectory(factory.BookingDirectory)(format);
        Assert.IsType<Success<int>>(journal.Append([receipt]));
        return receipt;
    }

    private static string NewBookingId() => "RF-IT" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    // ----- pages -----

    [Fact]
    public async Task HealthEndpoint_ReportsHealthy()
    {
        var response = await factory.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HomePage_IsServedAsHtml()
    {
        var response = await factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("RailFlow", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LoginPage_IsServed_WithAntiforgeryTokenAndFormName()
    {
        var response = await factory.CreateClient().GetAsync("/login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Sign in", html);
        Assert.Equal("login", HiddenField(html, "_handler"));
        Assert.NotEmpty(HiddenField(html, "__RequestVerificationToken"));
    }

    // ----- route quote API -----

    [Fact]
    public async Task RouteQuote_KnownStations_ReturnsAPricedItinerary()
    {
        var response = await factory.CreateClient().GetAsync("/api/route-quote?from=LONDON&to=PARIS&passengers=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Something", body.GetProperty("status").GetString());
        Assert.Equal("LONDON", body.GetProperty("origin").GetString());
        Assert.Equal("PARIS", body.GetProperty("destination").GetString());
        Assert.True(body.GetProperty("finalPrice").GetDecimal() > 0m);
        Assert.True(body.GetProperty("distanceKm").GetDouble() > 0);
    }

    [Fact]
    public async Task RouteQuote_UnknownStation_Returns404WithNothingStatus()
    {
        var response = await factory.CreateClient().GetAsync("/api/route-quote?from=LONDON&to=ATLANTIS&passengers=1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nothing", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RouteQuote_StationCodesAreCaseInsensitive_AndBadPassengerCountFallsBackToOne()
    {
        var client = factory.CreateClient();

        var single = await client.GetFromJsonAsync<JsonElement>("/api/route-quote?from=LONDON&to=PARIS&passengers=1");
        var lowerCase = await client.GetFromJsonAsync<JsonElement>("/api/route-quote?from=london&to=paris&passengers=-5");

        Assert.Equal("LONDON", lowerCase.GetProperty("origin").GetString());
        Assert.Equal(single.GetProperty("finalPrice").GetDecimal(), lowerCase.GetProperty("finalPrice").GetDecimal());
    }

    [Fact]
    public async Task RouteQuote_MissingParameters_IsABadRequest()
    {
        var response = await factory.CreateClient().GetAsync("/api/route-quote?from=LONDON");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ----- sign-in and roles -----

    [Fact]
    public async Task Bookings_Anonymous_Is401_NotARedirectToLogin()
    {
        var response = await factory.CreateBrowserClient().GetAsync("/api/bookings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        // API errors must not be replaced by the HTML "not found" page.
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Login_AsDispatcher_SetsAuthCookie_AndRedirectsHome()
    {
        var client = factory.CreateBrowserClient();

        var response = await SubmitLoginAsync(client, "dispatcher", "dispatcher-demo");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", new Uri(client.BaseAddress!, response.Headers.Location!).AbsolutePath);
        var authCookie = Assert.Single(SetCookies(response), c => c.StartsWith(AuthCookie + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bookings_AsDispatcher_Returns200WithStoredReceipts()
    {
        var stored = StoreReceipt(NewBookingId(), JournalFormat.TextLog);
        var client = await SignedInClientAsync("dispatcher", "dispatcher-demo");

        var response = await client.GetAsync("/api/bookings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipts = await response.Content.ReadFromJsonAsync<List<BookingReceipt>>();
        Assert.NotNull(receipts);
        Assert.Contains(receipts, r => r.BookingId == stored.BookingId && r.AmountPaid == stored.AmountPaid);
    }

    [Fact]
    public async Task Bookings_AsPassenger_Is403()
    {
        var client = await SignedInClientAsync("passenger", "passenger-demo");

        var response = await client.GetAsync("/api/bookings");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("dispatcher", "wrong-password")]
    [InlineData("nobody", "dispatcher-demo")]
    public async Task Login_WithBadCredentials_ShowsTheErrorAndSetsNoAuthCookie(string userName, string password)
    {
        var client = factory.CreateBrowserClient();

        var response = await SubmitLoginAsync(client, userName, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Unknown user name or wrong password.", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(SetCookies(response), c => c.StartsWith(AuthCookie + "=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/bookings")).StatusCode);
    }

    [Fact]
    public async Task Login_WithoutAntiforgeryToken_IsRejected()
    {
        var client = factory.CreateBrowserClient();
        await client.GetAsync("/login");

        var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["Input.UserName"] = "dispatcher",
            ["Input.Password"] = "dispatcher-demo"
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(SetCookies(response), c => c.StartsWith(AuthCookie + "=", StringComparison.Ordinal));
    }

    // The return address is copied from a hidden field, so it must never send the user to another site.
    [Theory]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example/phish", "/")]
    [InlineData("", "/")]
    [InlineData("/not-found", "/not-found")]
    public async Task Login_ReturnUrl_OnlyAllowsLocalPaths(string returnUrl, string expectedPath)
    {
        var client = factory.CreateBrowserClient();

        var response = await SubmitLoginAsync(client, "passenger", "passenger-demo", returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var target = new Uri(client.BaseAddress!, response.Headers.Location!);
        Assert.Equal(client.BaseAddress!.Authority, target.Authority);
        Assert.Equal(expectedPath, target.AbsolutePath);
    }

    [Fact]
    public async Task Logout_WithoutAntiforgeryToken_Is400()
    {
        var client = await SignedInClientAsync("dispatcher", "dispatcher-demo");

        var response = await client.PostAsync("/account/logout",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["returnUrl"] = "/" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Still signed in: the forged request did not log the user out.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/bookings")).StatusCode);
    }

    // ----- ticket export, end to end: file on disk -> HTTP response -----

    // Tickets contain the passenger's name and e-mail, so the API serves them to dispatchers only.
    private Task<HttpClient> DispatcherClientAsync() => SignedInClientAsync("dispatcher", "dispatcher-demo");

    [Fact]
    public async Task Ticket_WithoutDispatcherSignIn_IsRefused()
    {
        var receipt = StoreReceipt(NewBookingId());
        var url = $"/api/bookings/{receipt.BookingId}/ticket";

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateBrowserClient().GetAsync(url)).StatusCode);
        var passenger = await SignedInClientAsync("passenger", "passenger-demo");
        Assert.Equal(HttpStatusCode.Forbidden, (await passenger.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Ticket_UnknownBooking_Is404()
    {
        var response = await (await DispatcherClientAsync()).GetAsync("/api/bookings/RF-DOES-NOT-EXIST/ticket");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nothing", body.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("PlainText", "text/plain")]
    [InlineData("Csv", "text/csv")]
    [InlineData("Json", "application/json")]
    public async Task Ticket_StoredBooking_IsExportedInTheRequestedFormat(string format, string expectedMediaType)
    {
        var receipt = StoreReceipt(NewBookingId());

        var response = await (await DispatcherClientAsync()).GetAsync($"/api/bookings/{receipt.BookingId}/ticket?format={format}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedMediaType, response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains(receipt.BookingId, text);
        Assert.Contains("93.58", text);
    }

    [Fact]
    public async Task Ticket_UnsupportedFormat_IsABadRequest()
    {
        var receipt = StoreReceipt(NewBookingId());

        var response = await (await DispatcherClientAsync()).GetAsync($"/api/bookings/{receipt.BookingId}/ticket?format=Pdf");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Ticket_WithoutFormat_DefaultsToPlainText_AndIdIsCaseInsensitive()
    {
        var receipt = StoreReceipt(NewBookingId(), JournalFormat.TextLog);

        var response = await (await DispatcherClientAsync()).GetAsync($"/api/bookings/{receipt.BookingId.ToLowerInvariant()}/ticket");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("RAILFLOW E-TICKET", text);
        Assert.Contains("LONDON -> PARIS", text);
    }

    [Fact]
    public async Task Ticket_JsonFormat_RoundTripsToTheSameReceipt()
    {
        var receipt = StoreReceipt(NewBookingId());

        var response = await (await DispatcherClientAsync()).GetAsync($"/api/bookings/{receipt.BookingId}/ticket?format=Json");
        var parsed = await response.Content.ReadFromJsonAsync<BookingReceipt>();

        Assert.NotNull(parsed);
        Assert.Equal(receipt, parsed);
        Assert.Equal(receipt.PassengerName, parsed.PassengerName);
        Assert.Equal(receipt.IssuedAtUtc, parsed.IssuedAtUtc);
    }

    [Fact]
    public void Storage_AppJournalsPointAtTheTemporaryFolder_NotIntoTheProject()
    {
        var journalFactory = factory.Services.GetRequiredService<Func<JournalFormat, IBookingJournal>>();

        foreach (var format in Enum.GetValues<JournalFormat>())
        {
            var directory = Path.GetDirectoryName(journalFactory(format).FilePath);
            Assert.Equal(Path.GetFullPath(factory.BookingDirectory), Path.GetFullPath(directory!));
        }
    }
}
