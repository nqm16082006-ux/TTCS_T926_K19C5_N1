using System.Collections.Generic;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Services.Interfaces
{
    public interface ITicketService
    {
        /// <summary>
        /// Sinh mã vé ngẫu nhiên bảo mật bằng CSPRNG, không tuần tự, không suy đoán được.
        /// </summary>
        string GenerateTicketCode();

        /// <summary>
        /// Sinh danh sách vé điện tử cho từng OrderItem trong đơn hàng chưa có vé.
        /// </summary>
        List<Ticket> GenerateTicketsForOrder(Order order);
    }
}
