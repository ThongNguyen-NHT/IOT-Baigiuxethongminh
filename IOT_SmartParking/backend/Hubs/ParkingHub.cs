using Microsoft.AspNetCore.SignalR;

namespace SmartParking.Api.Hubs;

public class ParkingHub : Hub
{
    public async Task Ping()
    {
        await Clients.Caller.SendAsync("Pong", DateTime.UtcNow);
    }
}
