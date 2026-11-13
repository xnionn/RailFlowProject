using System.Net;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace RailFlow.Tests;

/// <summary>
/// Connects a real SignalR client to /hubs/traffic of the in-memory app.
/// Checks that listening is open to everyone but changing engineering works needs a dispatcher sign-in.
/// </summary>
public sealed class TrafficHubTests(RailFlowWebFactory factory) : IClassFixture<RailFlowWebFactory>, IAsyncLifetime
{
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
            await connection.DisposeAsync();
    }

    private async Task<string?> AuthCookieAsync(string userName, string password)
    {
        var client = factory.CreateBrowserClient();
        var response = await WebApiIntegrationTests.SubmitLoginAsync(client, userName, password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return WebApiIntegrationTests.SetCookies(response)
            .Select(c => c.Split(';')[0])
            .FirstOrDefault(c => c.StartsWith("RailFlow.Auth=", StringComparison.Ordinal));
    }

    private async Task<HubConnection> ConnectAsync(string? cookie = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/traffic"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                if (cookie is not null)
                    options.Headers["Cookie"] = cookie;
            })
            .Build();

        _connections.Add(connection);
        await connection.StartAsync();
        return connection;
    }

    [Fact]
    public async Task Guest_CanConnect_ButCannotToggleEngineeringWorks()
    {
        var guest = await ConnectAsync();

        Assert.Equal(HubConnectionState.Connected, guest.State);
        var error = await Assert.ThrowsAsync<HubException>(() => guest.InvokeAsync("ToggleEngineeringWorks", "PRAGUE"));
        Assert.Contains("unauthorized", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Passenger_CannotBroadcastAdvisories()
    {
        var passenger = await ConnectAsync(await AuthCookieAsync("passenger", "passenger-demo"));

        await Assert.ThrowsAsync<HubException>(() => passenger.InvokeAsync("BroadcastAdvisory", "Fake delay"));
    }

    [Fact]
    public async Task Dispatcher_TogglesWorks_AndEveryClientIsNotified()
    {
        var listener = await ConnectAsync();
        var update = new TaskCompletionSource<(string Station, bool Active)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var advisory = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.On<string, bool>("ReceiveTrafficUpdate", (station, active) => update.TrySetResult((station, active)));
        listener.On<string>("ReceiveAdvisory", message => advisory.TrySetResult(message));

        var dispatcher = await ConnectAsync(await AuthCookieAsync("dispatcher", "dispatcher-demo"));
        await dispatcher.InvokeAsync("ToggleEngineeringWorks", "PRAGUE");

        var (station, active) = await update.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("PRAGUE", station);
        Assert.True(active);

        // The actor name comes from the auth cookie, not from anything the client sent.
        var message = await advisory.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("Dispatcher Demo Dispatcher", message);
        Assert.Contains("Prague", message);

        // Put the shared state back.
        await dispatcher.InvokeAsync("ToggleEngineeringWorks", "PRAGUE");
    }
}
