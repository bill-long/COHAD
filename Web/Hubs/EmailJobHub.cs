using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Web.Hubs
{
    [Authorize(Policy = "EmailSender")]
    public class EmailJobHub : Hub
    {
        public const string EmailSendersGroupName = "EmailSenders";

        public override async Task OnConnectedAsync()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, EmailSendersGroupName);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(System.Exception exception)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, EmailSendersGroupName);
            await base.OnDisconnectedAsync(exception);
        }
    }
}
