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

    /// <summary>
    /// DTO chuẩn hóa kết quả thanh toán từ bất kỳ nguồn nào (webhook, return query, gateway direct query) (Task T-42).
    /// </summary>
    public class PaymentResultDto
    {
        public bool Success { get; set; }
        public string? OrderId { get; set; }
        public long? OrderCode { get; set; }
        public int Amount { get; set; }
        public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
        public string? TransactionId { get; set; }
        public DateTimeOffset? PaidAt { get; set; }
        public string? ErrorMessage { get; set; }

        public static PaymentResultDto CreateSuccess(long? orderCode, int amount, string? transactionId, DateTimeOffset? paidAt, string? orderId = null)
        {
            return new PaymentResultDto
            {
                Success = true,
                OrderCode = orderCode,
                OrderId = orderId,
                Amount = amount,
                Status = PaymentStatus.Success,
                TransactionId = transactionId,
                PaidAt = paidAt ?? DateTimeOffset.UtcNow
            };
        }

        public static PaymentResultDto CreateFailure(string errorMessage, PaymentStatus status = PaymentStatus.Failed, long? orderCode = null, string? orderId = null)
        {
            return new PaymentResultDto
            {
                Success = false,
                ErrorMessage = errorMessage,
                Status = status,
                OrderCode = orderCode,
                OrderId = orderId
            };
        }

        public static PaymentResultDto FromWebhookParseResult(WebhookParseResult webhook)
        {
            return new PaymentResultDto
            {
                Success = webhook.IsValid && webhook.Status == PaymentStatus.Success,
                OrderId = webhook.OrderId,
                OrderCode = webhook.OrderCode,
                Amount = webhook.Amount,
                Status = webhook.Status,
                TransactionId = webhook.TransactionId,
                PaidAt = webhook.PaidAt,
                ErrorMessage = webhook.ErrorMessage
            };
        }
    }

    /// <summary>
    /// DTO chứa tham số truy vấn khi khách hàng redirect quay về hoặc kiểm tra trạng thái (Task T-42).
    /// Các tham số từ client chỉ mang tính tham khảo để tra cứu, server luôn đối soát độc lập với gateway.
    /// </summary>
    public class PaymentReturnQueryDto
    {
        public Guid? OrderId { get; set; }
        public long? OrderCode { get; set; }
        public string? Status { get; set; }
        public bool? Cancel { get; set; }
    }

    /// <summary>
    /// Kết quả xử lý payment handler chung (Task T-42).
    /// </summary>
    public class PaymentExecutionResult
    {
        public bool Success { get; set; }
        public Guid? OrderId { get; set; }
        public long? OrderCode { get; set; }
        public string? OrderStatus { get; set; }
        public string? Message { get; set; }
        public bool IsAlreadyProcessed { get; set; }

        public static PaymentExecutionResult CreateSuccess(Guid orderId, long? orderCode, string message = "Xử lý kết quả thanh toán thành công.", bool isAlreadyProcessed = false)
        {
            return new PaymentExecutionResult
            {
                Success = true,
                OrderId = orderId,
                OrderCode = orderCode,
                OrderStatus = "Paid",
                Message = message,
                IsAlreadyProcessed = isAlreadyProcessed
            };
        }

        public static PaymentExecutionResult CreateFailure(string message, Guid? orderId = null, long? orderCode = null, string? status = null)
        {
            return new PaymentExecutionResult
            {
                Success = false,
                OrderId = orderId,
                OrderCode = orderCode,
                OrderStatus = status,
                Message = message
            };
        }
    }
}
