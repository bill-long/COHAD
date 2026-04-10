using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Web.Hubs
{
    [Authorize(Policy = "Administrator")]
    public class HeldMessageNotificationsHub : Hub
    {
        public const string AdminGroupName = "HeldMessageAdmins";

        public override async Task OnConnectedAsync()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroupName);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(System.Exception exception)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, AdminGroupName);
            await base.OnDisconnectedAsync(exception);
        }
    }
}
