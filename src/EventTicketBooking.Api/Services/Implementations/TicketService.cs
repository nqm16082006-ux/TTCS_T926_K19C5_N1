using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;

namespace EventTicketBooking.Api.Services.Implementations
{
    public class TicketService : ITicketService
    {
        // Bộ 32 ký tự alphanumeric loại bỏ các ký tự gây nhầm lẫn: 0/O, 1/I/L
        private static readonly char[] Base32Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ".ToCharArray();
        private readonly IQrSignatureService _qrSignatureService;

        public TicketService(IQrSignatureService? qrSignatureService = null)
        {
            _qrSignatureService = qrSignatureService ?? new QrSignatureService();
        }

        public string GenerateTicketCode()
        {
            // NFR S-25: Mã vé sinh ngẫu nhiên đủ dài, không dùng số thứ tự tăng dần.
            // 16 ký tự Base32 sinh bởi CSPRNG cung cấp 2^80 không gian trạng thái, không thể suy đoán được vé khác.
            var token = RandomNumberGenerator.GetString(Base32Alphabet, 16);
            return $"TK-{token[..4]}-{token[4..8]}-{token[8..12]}-{token[12..]}";
        }

        public List<Ticket> GenerateTicketsForOrder(Order order)
        {
            var tickets = new List<Ticket>();
            if (order?.OrderItems == null || !order.OrderItems.Any())
            {
                return tickets;
            }

            foreach (var item in order.OrderItems)
            {
                if (item.Ticket == null)
                {
                    var ticket = new Ticket
                    {
                        Id = Guid.NewGuid(),
                        OrderItemId = item.Id,
                        TicketCode = GenerateTicketCode(),
                        CreatedAt = DateTimeOffset.UtcNow,
                        OrderItem = item
                    };
                    item.Ticket = ticket;
                    tickets.Add(ticket);
                }
            }

            return tickets;
        }

        public string GenerateQrPayload(string ticketCode, Guid showtimeId)
        {
            return _qrSignatureService.SignTicket(ticketCode, showtimeId);
        }
    }
}
