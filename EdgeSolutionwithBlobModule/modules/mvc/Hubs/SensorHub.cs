using mvc.Models;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace mvc.Hubs
{
    public class SensorHub : Hub
    {
        public Task Broadcast(string sender, MessageBody message)
        {
            return Clients
                .AllExcept(new[] { Context.ConnectionId })
                .SendAsync("Broadcast", sender, message);
        }
    }
}
