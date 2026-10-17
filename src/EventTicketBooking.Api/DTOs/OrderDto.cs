using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    public class OrderDto
    {
        public Guid Id { get; set; }
        public Guid ShowtimeId { get; set; }
        public Guid? EventId { get; set; }
        public string? EventTitle { get; set; }
        public string? EventLocation { get; set; }
        public string? EventImageUrl { get; set; }
        public DateTime? ShowtimeStartTime { get; set; }
        public DateTime? ShowtimeEndTime { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerEmail { get; set; }
        public string Status { get; set; } = null!;
        public int TotalAmount { get; set; }
        public int OriginalAmount { get; set; }
        public int DiscountAmount { get; set; }
        public string? DiscountCode { get; set; }
        public Guid? DiscountCodeId { get; set; }
        public int SeatCount { get; set; }
        public List<string> SeatNames { get; set; } = new();
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public bool HasFailedPayment { get; set; }
        public bool IsPaymentProcessing { get; set; }
        public List<OrderItemDto> Items { get; set; } = new();

        /// <summary>Story S-45: lý do huỷ đơn (null nếu chưa huỷ).</summary>
        public string? CancelReason { get; set; }

        /// <summary>Story S-45: trạng thái yêu cầu hoàn tiền (null nếu chưa có yêu cầu).</summary>
        public string? RefundStatus { get; set; }

        /// <summary>Story S-45: số tiền hoàn (VND).</summary>
        public int? RefundAmount { get; set; }

        /// <summary>Story S-45: lý do hoàn tiền thất bại.</summary>
        public string? RefundFailureReason { get; set; }

        /// <summary>Story S-45: thời điểm cập nhật cuối của yêu cầu hoàn tiền.</summary>
        public DateTimeOffset? RefundUpdatedAt { get; set; }

        /// <summary>Story S-48: số lần đã retry hoàn tiền thất bại (0..3).</summary>
        public int RefundRetryCount { get; set; }
    }

    public class OrderItemDto
    {
        public Guid Id { get; set; }
        public Guid SeatId { get; set; }
        public int Price { get; set; }
        public string? SeatName { get; set; }
        public string? CategoryName { get; set; }
    }

    public class OrderStatusResponseDto
    {
        public string Status { get; set; } = string.Empty;
    }
}
