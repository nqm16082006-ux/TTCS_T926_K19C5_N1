using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace EventTicketBooking.Api.Hubs
{
    /// <summary>
    /// Story S-52: SignalR Hub phát số liệu soát vé thời gian thực tới Ban tổ chức và Quản trị viên.
    /// Cho phép Client đăng ký lắng nghe theo từng nhóm suất diễn (ShowtimeId).
    /// </summary>
    public class CheckinLiveHub : Hub
    {
        public static string GetGroupName(Guid showtimeId) => $"showtime_{showtimeId}";

        public async Task JoinShowtimeGroup(string showtimeId)
        {
            if (Guid.TryParse(showtimeId, out var id))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupName(id));
            }
        }

        public async Task LeaveShowtimeGroup(string showtimeId)
        {
            if (Guid.TryParse(showtimeId, out var id))
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroupName(id));
            }
        }
    }
}
