using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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

        /// <summary>Story S-45: lý do hoàn tiền thất bại (chỉ dành cho Auditor/Admin, ẩn với Customer để tránh lộ gateway error/credentials).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RefundFailureReason { get; set; }

        /// <summary>Story S-45: thời điểm cập nhật cuối của yêu cầu hoàn tiền.</summary>
        public DateTimeOffset? RefundUpdatedAt { get; set; }

        /// <summary>Story S-48: ID của bản ghi yêu cầu hoàn tiền (RefundRequest) để khách hàng/admin có thể tra cứu/sao chép.</summary>
        public Guid? RefundRequestId { get; set; }

        /// <summary>Story S-48: thời điểm tạo yêu cầu hoàn tiền.</summary>
        public DateTimeOffset? RefundCreatedAt { get; set; }

        /// <summary>Story S-48: số lần đã retry hoàn tiền thất bại (0..3) - chỉ dành cho Auditor/Admin.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? RefundRetryCount { get; set; }

        /// <summary>Story S-48: số lần đã thử hoàn tiền qua cổng thanh toán (AttemptCount) - chỉ dành cho Auditor/Admin.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? RefundAttemptCount { get; set; }

        /// <summary>Story S-48: thời điểm thử hoàn tiền gần nhất - chỉ dành cho Auditor/Admin.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? RefundLastAttemptedAt { get; set; }

        /// <summary>Story S-48: thời điểm dự kiến tự động thử lại kế tiếp (nếu ở trạng thái RetryScheduled) - chỉ dành cho Auditor/Admin.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? RefundNextRetryAt { get; set; }

        /// <summary>Story S-48: mã lỗi trả về từ cổng thanh toán - chỉ dành cho Auditor/Admin.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RefundErrorCode { get; set; }

        /// <summary>Story S-48: thông điệp lỗi chi tiết từ cổng thanh toán - chỉ dành cho Auditor/Admin.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RefundErrorMessage { get; set; }
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
