using System;
using System.Collections.Generic;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Services.Interfaces
{
    public interface ITicketService
    {
        /// <summary>
        /// Sinh mã vé ngẫu nhiên bảo mật bằng CSPRNG, không tuần tự, không suy đoán được (S-25).
        /// </summary>
        string GenerateTicketCode();

        /// <summary>
        /// Sinh danh sách vé điện tử cho từng OrderItem trong đơn hàng chưa có vé (S-25).
        /// </summary>
        List<Ticket> GenerateTicketsForOrder(Order order);

        /// <summary>
        /// Sinh chuỗi mã QR có chữ ký số cho vé và suất diễn (Story S-26).
        /// </summary>
        string GenerateQrPayload(string ticketCode, Guid showtimeId);
    }
}
