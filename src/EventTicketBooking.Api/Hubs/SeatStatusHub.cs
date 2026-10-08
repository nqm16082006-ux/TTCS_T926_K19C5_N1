using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace EventTicketBooking.Api.Hubs
{
    public class SeatStatusHub : Hub
    {
        public async Task JoinShowtimeGroup(string showtimeId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"Showtime_{showtimeId}");
        }

        public async Task LeaveShowtimeGroup(string showtimeId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"Showtime_{showtimeId}");
        }
    }
}
