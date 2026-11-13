using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RailFlow.Infrastructure.Security;
using RailFlow.Infrastructure.Services;

namespace RailFlow.Web.Hubs;

/// <summary>
/// SignalR hub for real-time engineering-works advisories. Anyone may connect and listen;
/// only signed-in dispatchers may change anything. The role is read from the auth cookie, not from the caller.
/// </summary>
public sealed class TrafficHub(ChaosAndTrafficController chaosController) : Hub
{
    [Authorize(Roles = RoleNames.Dispatcher)]
    public async Task ToggleStationRoadworks(string stationCode)
    {
        chaosController.ToggleRoadworks(stationCode, ActorName());
        await Clients.All.SendAsync("ReceiveTrafficUpdate", stationCode, chaosController.HasRoadworksAt(stationCode));
    }

    /// <summary>
    /// Raises the advisory on the shared controller; Program.cs forwards every alert to all hub clients
    /// as "ReceiveAdvisory", and Blazor circuits receive it through the controller event.
    /// </summary>
    [Authorize(Roles = RoleNames.Dispatcher)]
    public Task BroadcastAdvisory(string advisoryText)
    {
        chaosController.BroadcastCustomAdvisory($"{ActorName()}: {advisoryText}");
        return Task.CompletedTask;
    }

    private string ActorName() =>
        $"Dispatcher {Context.User?.FindFirst(RoleNames.DisplayNameClaim)?.Value ?? Context.User?.Identity?.Name ?? "unknown"}";
}
