namespace EventTicketBooking.Api.DTOs.Payment
{
    public enum PaymentStatus
    {
        Pending,
        Success,
        Failed,
        Cancelled
    }

    public class PaymentItemDto
    {
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; } = 1;
        public int Price { get; set; }
    }

    public class PaymentCreationRequest
    {
        public string OrderId { get; set; } = string.Empty;
        public long OrderCode { get; set; }
        public int Amount { get; set; }
        public string Description { get; set; } = string.Empty;
        public string ReturnUrl { get; set; } = string.Empty;
        public string CancelUrl { get; set; } = string.Empty;
        public List<PaymentItemDto> Items { get; set; } = new();
    }

    public class PaymentCreationResult
    {
        public bool Success { get; set; }
        public string? PaymentUrl { get; set; }
        public string? TransactionId { get; set; }
        public long? OrderCode { get; set; }
        public string? QrCode { get; set; }
        public string? ErrorMessage { get; set; }

        public static PaymentCreationResult CreateSuccess(string paymentUrl, string? transactionId = null, string? qrCode = null, long? orderCode = null)
        {
            return new PaymentCreationResult
            {
                Success = true,
                PaymentUrl = paymentUrl,
                TransactionId = transactionId,
                QrCode = qrCode,
                OrderCode = orderCode
            };
        }

        public static PaymentCreationResult CreateFailure(string errorMessage)
        {
            return new PaymentCreationResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }
    }

    public class WebhookParseResult
    {
        public bool IsValid { get; set; }
        public string? OrderId { get; set; }
        public long? OrderCode { get; set; }
        public int Amount { get; set; }
        public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
        public string? TransactionId { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTimeOffset? PaidAt { get; set; }

        public static WebhookParseResult CreateSuccess(long? orderCode, int amount, string? transactionId, DateTimeOffset? paidAt, string? orderId = null)
        {
            return new WebhookParseResult
            {
                IsValid = true,
                OrderCode = orderCode,
                OrderId = orderId,
                Amount = amount,
                Status = PaymentStatus.Success,
                TransactionId = transactionId,
                PaidAt = paidAt
            };
        }

        public static WebhookParseResult CreateFailure(string errorMessage)
        {
            return new WebhookParseResult
            {
                IsValid = false,
                Status = PaymentStatus.Failed,
                ErrorMessage = errorMessage
            };
        }
    }
}
