using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Yêu cầu hoàn tiền phát sinh khi khách huỷ đơn đã thanh toán (Story S-45).
    /// Mỗi đơn hàng tối đa một bản ghi hoàn tiền đang được quản lý (unique OrderId).
    /// Trạng thái lưu dạng string để đồng bộ với cách lưu của PaymentTransaction.Status.
    /// </summary>
    public class Refund
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Đơn hàng gắn với yêu cầu hoàn tiền (duy nhất).</summary>
        public Guid OrderId { get; set; }

        /// <summary>Số tiền hoàn (VND nguyên, làm tròn xuống theo chính sách demo).</summary>
        public int Amount { get; set; }

        /// <summary>Requested, Succeeded, Failed, ManualPending.</summary>
        public string Status { get; set; } = "Requested";

        /// <summary>Lý do hoàn tiền thất bại (khi Status = Failed).</summary>
        public string? FailureReason { get; set; }

        /// <summary>Người xác nhận xử lý thủ công (Auditor/Admin) - dùng cho ManualPending.</summary>
        public Guid? ConfirmedByUserId { get; set; }

        /// <summary>Tên người xác nhận thủ công (lưu thô để không phụ thuộc truy vấn User).</summary>
        public string? ConfirmedByName { get; set; }

        /// <summary>Thời điểm xác nhận thủ công.</summary>
        public DateTimeOffset? ConfirmedAt { get; set; }

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Order Order { get; set; } = null!;
    }
}
