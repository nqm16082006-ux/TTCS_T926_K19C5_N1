using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventTicketBooking.Api.Services.Implementations.Payment
{
    /// <summary>
    /// Adapter triển khai cổng thanh toán PayOS theo IPaymentGateway (Task T-40).
    /// Toàn bộ logic giao tiếp HTTP, mã hóa HMAC-SHA256 và cấu trúc payload của PayOS
    /// được cô lập hoàn toàn bên trong Adapter này.
    /// </summary>
    public class PayOSPaymentGateway : IPaymentGateway
    {
        private readonly HttpClient _httpClient;
        private readonly PayOSOptions _options;
        private readonly ILogger<PayOSPaymentGateway> _logger;

        public string ProviderName => "PayOS";

        public PayOSPaymentGateway(
            HttpClient httpClient,
            IOptions<PayOSOptions> options,
            ILogger<PayOSPaymentGateway> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<PaymentCreationResult> CreatePaymentAsync(PaymentCreationRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                if (request == null)
                    return PaymentCreationResult.CreateFailure("Yêu cầu thanh toán không hợp lệ.");

                if (request.Amount <= 0)
                    return PaymentCreationResult.CreateFailure("Số tiền thanh toán phải lớn hơn 0.");

                // Chuẩn hóa dữ liệu PayOS
                long orderCode = request.OrderCode > 0 ? request.OrderCode : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string returnUrl = !string.IsNullOrEmpty(request.ReturnUrl) ? request.ReturnUrl : _options.ReturnUrl;
                string cancelUrl = !string.IsNullOrEmpty(request.CancelUrl) ? request.CancelUrl : _options.CancelUrl;
                string description = string.IsNullOrWhiteSpace(request.Description)
                    ? $"Don hang {orderCode}"
                    : (request.Description.Length > 25 ? request.Description[..25] : request.Description);

                // Tính chữ ký PayOS cho yêu cầu tạo link thanh toán:
                // Sắp xếp theo thứ tự a-z: amount, cancelUrl, description, orderCode, returnUrl
                string rawSignatureData = $"amount={request.Amount}&cancelUrl={cancelUrl}&description={description}&orderCode={orderCode}&returnUrl={returnUrl}";
                string signature = ComputeHmacSha256(rawSignatureData, _options.ChecksumKey);

                // Tạo payload của PayOS
                var payload = new
                {
                    orderCode = orderCode,
                    amount = request.Amount,
                    description = description,
                    cancelUrl = cancelUrl,
                    returnUrl = returnUrl,
                    signature = signature,
                    items = request.Items?.Select(i => new
                    {
                        name = i.Name,
                        quantity = i.Quantity,
                        price = i.Price
                    }).ToList()
                };

                // Kiểm tra xem đã có API Key cấu hình chưa
                if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.ClientId))
                {
                    // Chế độ mô phỏng khi chưa điền thông tin cổng thật trong môi trường dev/local
                    _logger.LogInformation("PayOS options chưa được cấu hình ClientId/ApiKey. Tự động trả về link thanh toán mô phỏng.");
                    string simulatedUrl = $"{_options.BaseUrl}/payment-link/{orderCode}?signature={signature}";
                    return PaymentCreationResult.CreateSuccess(simulatedUrl, $"TXN_{orderCode}", null, orderCode);
                }

                // Gửi request thực tế sang PayOS
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/v2/payment-requests");
                httpRequest.Headers.Add("x-client-id", _options.ClientId);
                httpRequest.Headers.Add("x-api-key", _options.ApiKey);
                httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Lỗi gọi PayOS API: {StatusCode}, Response: {Content}", response.StatusCode, responseContent);
                    return PaymentCreationResult.CreateFailure($"Lỗi cổng thanh toán: {response.StatusCode}");
                }

                using var doc = JsonDocument.Parse(responseContent);
                var root = doc.RootElement;
                string? code = root.TryGetProperty("code", out var c) ? c.GetString() : null;

                if (code == "00" && root.TryGetProperty("data", out var dataEl))
                {
                    string? checkoutUrl = dataEl.TryGetProperty("checkoutUrl", out var cu) ? cu.GetString() : null;
                    string? qrCode = dataEl.TryGetProperty("qrCode", out var qr) ? qr.GetString() : null;
                    string? paymentLinkId = dataEl.TryGetProperty("paymentLinkId", out var plId) ? plId.GetString() : null;

                    return PaymentCreationResult.CreateSuccess(checkoutUrl ?? string.Empty, paymentLinkId ?? $"TXN_{orderCode}", qrCode, orderCode);
                }

                string? desc = root.TryGetProperty("desc", out var d) ? d.GetString() : "Không thể tạo link thanh toán";
                return PaymentCreationResult.CreateFailure(desc ?? "Lỗi phản hồi từ PayOS.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ngoại lệ khi tạo thanh toán với PayOS");
                return PaymentCreationResult.CreateFailure($"Lỗi hệ thống khi kết nối cổng thanh toán: {ex.Message}");
            }
        }

        public bool VerifyWebhookSignature(string webhookPayload, string signature, string? webhookSecret = null)
        {
            if (string.IsNullOrWhiteSpace(webhookPayload) || string.IsNullOrWhiteSpace(signature))
                return false;

            try
            {
                string secret = !string.IsNullOrEmpty(webhookSecret) ? webhookSecret : _options.ChecksumKey;
                if (string.IsNullOrEmpty(secret))
                {
                    _logger.LogWarning("Không tìm thấy ChecksumKey để kiểm tra chữ ký webhook.");
                    return false;
                }

                using var doc = JsonDocument.Parse(webhookPayload);
                var root = doc.RootElement;

                // Nếu webhook có trường "data" theo chuẩn PayOS
                if (root.TryGetProperty("data", out var dataEl))
                {
                    // Lấy tất cả thuộc tính trong data và sắp xếp theo key
                    var sortedDict = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    foreach (var prop in dataEl.EnumerateObject())
                    {
                        sortedDict[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Null => string.Empty,
                            JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                            _ => prop.Value.GetRawText()
                        };
                    }

                    // Format: key1=value1&key2=value2...
                    var parts = sortedDict.Select(kv => $"{kv.Key}={kv.Value}");
                    string dataSignString = string.Join("&", parts);

                    string expectedSignature = ComputeHmacSha256(dataSignString, secret);
                    return string.Equals(expectedSignature, signature, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    // Trường hợp payload là chuỗi data trực tiếp
                    string expectedSignature = ComputeHmacSha256(webhookPayload, secret);
                    return string.Equals(expectedSignature, signature, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi kiểm tra chữ ký webhook");
                return false;
            }
        }

        public WebhookParseResult ParseWebhookData(string webhookPayload)
        {
            if (string.IsNullOrWhiteSpace(webhookPayload))
                return WebhookParseResult.CreateFailure("Webhook payload rỗng.");

            try
            {
                using var doc = JsonDocument.Parse(webhookPayload);
                var root = doc.RootElement;

                // Cấu trúc chuẩn PayOS: { "code": "00", "desc": "success", "data": { ... } }
                JsonElement dataElement = root.TryGetProperty("data", out var d) ? d : root;

                long? orderCode = null;
                if (dataElement.TryGetProperty("orderCode", out var ocProp))
                {
                    if (ocProp.ValueKind == JsonValueKind.Number && ocProp.TryGetInt64(out var ocVal))
                        orderCode = ocVal;
                    else if (long.TryParse(ocProp.GetString(), out var ocParsed))
                        orderCode = ocParsed;
                }

                int amount = 0;
                if (dataElement.TryGetProperty("amount", out var amtProp))
                {
                    if (amtProp.ValueKind == JsonValueKind.Number && amtProp.TryGetInt32(out var aVal))
                        amount = aVal;
                    else if (int.TryParse(amtProp.GetString(), out var aParsed))
                        amount = aParsed;
                }

                string? transactionId = null;
                if (dataElement.TryGetProperty("reference", out var refProp))
                    transactionId = refProp.GetString();
                else if (dataElement.TryGetProperty("paymentLinkId", out var plProp))
                    transactionId = plProp.GetString();

                DateTimeOffset? paidAt = null;
                if (dataElement.TryGetProperty("transactionDateTime", out var timeProp))
                {
                    if (DateTimeOffset.TryParse(timeProp.GetString(), out var parsedTime))
                        paidAt = parsedTime;
                }

                // Mã trạng thái ("00" là thành công theo chuẩn PayOS)
                string? code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
                if (code == null && dataElement.TryGetProperty("code", out var dc))
                    code = dc.GetString();

                var status = (code == "00" || code == "SUCCESS") ? PaymentStatus.Success : PaymentStatus.Failed;

                return new WebhookParseResult
                {
                    IsValid = true,
                    OrderCode = orderCode,
                    Amount = amount,
                    Status = status,
                    TransactionId = transactionId,
                    PaidAt = paidAt ?? DateTimeOffset.UtcNow
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi phân tích dữ liệu webhook PayOS");
                return WebhookParseResult.CreateFailure($"Không thể đọc dữ liệu webhook: {ex.Message}");
            }
        }

        public async Task<PaymentResultDto?> QueryPaymentStatusAsync(long orderCode, CancellationToken cancellationToken = default)
        {
            try
            {
                if (orderCode <= 0)
                    return null;

                if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.ClientId))
                {
                    _logger.LogInformation("PayOS options chưa được cấu hình ClientId/ApiKey. Bỏ qua truy vấn trực tiếp cổng.");
                    return null;
                }

                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{_options.BaseUrl.TrimEnd('/')}/v2/payment-requests/{orderCode}");
                httpRequest.Headers.Add("x-client-id", _options.ClientId);
                httpRequest.Headers.Add("x-api-key", _options.ApiKey);

                var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Không thể truy vấn trạng thái từ PayOS: {StatusCode}, Response: {Content}", response.StatusCode, responseContent);
                    return null;
                }

                using var doc = JsonDocument.Parse(responseContent);
                var root = doc.RootElement;
                string? code = root.TryGetProperty("code", out var c) ? c.GetString() : null;

                if (code == "00" && root.TryGetProperty("data", out var dataEl))
                {
                    string? statusStr = dataEl.TryGetProperty("status", out var s) ? s.GetString() : null;
                    int amount = dataEl.TryGetProperty("amount", out var a) ? a.GetInt32() : 0;
                    int amountPaid = dataEl.TryGetProperty("amountPaid", out var ap) ? ap.GetInt32() : 0;
                    string? id = dataEl.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

                    var status = statusStr switch
                    {
                        "PAID" => PaymentStatus.Success,
                        "CANCELLED" => PaymentStatus.Cancelled,
                        "PENDING" => PaymentStatus.Pending,
                        _ => PaymentStatus.Failed
                    };

                    return new PaymentResultDto
                    {
                        Success = status == PaymentStatus.Success,
                        OrderCode = orderCode,
                        Amount = amountPaid > 0 ? amountPaid : amount,
                        Status = status,
                        TransactionId = id,
                        PaidAt = DateTimeOffset.UtcNow
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ngoại lệ khi truy vấn trạng thái thanh toán từ PayOS cho OrderCode {OrderCode}", orderCode);
                return null;
            }
        }

        private static string ComputeHmacSha256(string data, string secretKey)
        {
            if (string.IsNullOrEmpty(secretKey))
                return string.Empty;

            byte[] keyBytes = Encoding.UTF8.GetBytes(secretKey);
            byte[] dataBytes = Encoding.UTF8.GetBytes(data);

            using var hmac = new HMACSHA256(keyBytes);
            byte[] hash = hmac.ComputeHash(dataBytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
