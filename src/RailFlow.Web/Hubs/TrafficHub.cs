using Microsoft.AspNetCore.SignalR;
using RailFlow.Infrastructure.Services;

namespace RailFlow.Web.Hubs;

/// <summary>
/// SignalR Hub for broadcasting real-time rail traffic & roadworks advisories across clients (Criterion 9).
/// </summary>
public sealed class TrafficHub(ChaosAndTrafficController chaosController) : Hub
{
    public async Task ToggleStationRoadworks(string stationCode, string role)
    {
        chaosController.ToggleRoadworks(stationCode, role);
        await Clients.All.SendAsync("ReceiveTrafficUpdate", stationCode, chaosController.HasRoadworksAt(stationCode));
    }

    public async Task BroadcastAdvisory(string advisoryText)
    {
        chaosController.BroadcastCustomAdvisory(advisoryText);
        await Clients.All.SendAsync("ReceiveAdvisory", advisoryText);
    }
}
