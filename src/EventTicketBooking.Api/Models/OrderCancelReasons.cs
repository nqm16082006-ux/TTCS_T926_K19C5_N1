using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Lý do huỷ đơn (Order.CancelReason) — phân biệt hai loại huỷ cùng trạng thái Cancelled.
    /// Story S-45: CUSTOMER_REFUND = khách chủ động huỷ sau thanh toán (có yêu cầu hoàn tiền).
    /// Luồng thanh toán: PAYMENT_CANCELLED = huỷ trong lúc thanh toán, chưa phát sinh tiền hoàn.
    /// Đơn Cancelled cũ (tồn tại trước S-45) có CancelReason = null → coi như huỷ giao dịch.
    /// </summary>
    public static class OrderCancelReasons
    {
        public const string CustomerRefund = "CUSTOMER_REFUND";
        public const string PaymentCancelled = "PAYMENT_CANCELLED";
    }
}
