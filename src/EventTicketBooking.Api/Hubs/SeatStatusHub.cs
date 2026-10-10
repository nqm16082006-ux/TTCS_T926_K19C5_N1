using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;
using System;

namespace EventTicketBooking.Api.Hubs
{
    public class SeatStatusHub : Hub
    {
        public async Task JoinShowtimeGroup(string showtimeId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, showtimeId);
        }

        public async Task LeaveShowtimeGroup(string showtimeId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, showtimeId);
        }
    }
}
