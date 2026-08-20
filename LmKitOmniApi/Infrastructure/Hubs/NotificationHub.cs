using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LmKitOmniApi.Infrastructure.Hubs;

[Authorize]
public class NotificationHub : Hub
{
    public async Task SendNotification(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 2_000)
            throw new HubException("Notification message is invalid.");

        var sender = Context.UserIdentifier ?? throw new HubException("Authenticated user id is required.");
        await Clients.Caller.SendAsync("ReceiveNotification", sender, message);
    }
}
