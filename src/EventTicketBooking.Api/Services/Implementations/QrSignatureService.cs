using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Triển khai dịch vụ ký số và xác thực mã QR cho vé điện tử (Story S-26).
    /// Thuật toán: ECDSA với đường cong NIST P-256 (secp256r1) và băm SHA-256.
    /// Tuân thủ:
    /// - AC1: Nội dung gồm mã vé, mã suất, chữ ký. Sửa 1 ký tự bị từ chối.
    /// - AC2: Quản lý phiên bản khoá (Key Versioning) cho phép Key Rotation mà vé cũ vẫn hợp lệ.
    /// - AC3: QR thiếu chữ ký hoặc tự chế bị từ chối.
    /// - NFR: Khoá riêng chỉ nằm trên máy chủ; máy quét chỉ cần khoá công khai để xác thực.
    /// </summary>
    public class QrSignatureService : IQrSignatureService, IDisposable
    {
        private readonly ILogger<QrSignatureService> _logger;
        private string _activeKeyVersion;
        private readonly ConcurrentDictionary<string, ECDsa> _privateKeys = new();
        private readonly ConcurrentDictionary<string, ECDsa> _publicKeys = new();
        private readonly ConcurrentDictionary<string, string> _publicKeysBase64 = new();

        public QrSignatureService(
            IOptions<TicketQrOptions>? options = null,
            ILogger<QrSignatureService>? logger = null)
        {
            _logger = logger ?? NullLogger<QrSignatureService>.Instance;
            var opts = options?.Value ?? new TicketQrOptions();
            _activeKeyVersion = !string.IsNullOrWhiteSpace(opts.ActiveKeyVersion) ? opts.ActiveKeyVersion : "v1";

            // Nạp cấu hình khoá
            if (opts.Keys != null && opts.Keys.Any())
            {
                foreach (var (version, keyConfig) in opts.Keys)
                {
                    LoadKey(version, keyConfig.PrivateKey, keyConfig.PublicKey);
                }
            }

            // Đảm bảo có ít nhất 1 phiên bản khoá mặc định nếu config rỗng
            if (_publicKeys.IsEmpty)
            {
                var defaultOpts = new TicketQrOptions();
                foreach (var (version, keyConfig) in defaultOpts.Keys)
                {
                    LoadKey(version, keyConfig.PrivateKey, keyConfig.PublicKey);
                }
            }
        }

        /// <summary>
        /// Tạo một instance chỉ chứa Public Keys dùng riêng cho máy quét (tuân thủ NFR).
        /// </summary>
        public static QrSignatureService CreateVerifierOnly(IReadOnlyDictionary<string, string> publicKeysSpkiBase64)
        {
            var options = new TicketQrOptions
            {
                ActiveKeyVersion = publicKeysSpkiBase64.Keys.FirstOrDefault() ?? "v1",
                Keys = new Dictionary<string, TicketKeyConfig>()
            };

            foreach (var (ver, pub) in publicKeysSpkiBase64)
            {
                options.Keys[ver] = new TicketKeyConfig
                {
                    PrivateKey = null,
                    PublicKey = pub
                };
            }

            return new QrSignatureService(Microsoft.Extensions.Options.Options.Create(options));
        }

        private void LoadKey(string version, string? privateKeyPkcs8, string publicKeySpki)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(publicKeySpki))
                {
                    var pubBytes = Convert.FromBase64String(publicKeySpki);
                    var pubEcdsa = ECDsa.Create();
                    pubEcdsa.ImportSubjectPublicKeyInfo(pubBytes, out _);
                    _publicKeys[version] = pubEcdsa;
                    _publicKeysBase64[version] = publicKeySpki;
                }

                if (!string.IsNullOrWhiteSpace(privateKeyPkcs8))
                {
                    var privBytes = Convert.FromBase64String(privateKeyPkcs8);
                    var privEcdsa = ECDsa.Create();
                    privEcdsa.ImportPkcs8PrivateKey(privBytes, out _);
                    _privateKeys[version] = privEcdsa;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi nạp khoá ký mã QR cho phiên bản {Version}", version);
            }
        }

        public string GetActiveKeyVersion() => _activeKeyVersion;

        public void SetActiveKeyVersion(string version)
        {
            if (!_publicKeys.ContainsKey(version))
            {
                throw new InvalidOperationException($"Không thể kích hoạt phiên bản khoá '{version}' vì chưa được đăng ký trong hệ thống.");
            }
            _activeKeyVersion = version;
            _logger.LogInformation("Đã chuyển phiên bản khoá ký QR sang {Version} (Key Rotation)", version);
        }

        public void RegisterKey(string version, string? privateKeyPkcs8, string publicKeySpki)
        {
            LoadKey(version, privateKeyPkcs8, publicKeySpki);
        }

        public IReadOnlyDictionary<string, string> GetPublicKeys()
        {
            return _publicKeysBase64;
        }

        public string SignTicket(string ticketCode, Guid showtimeId)
        {
            return SignTicket(ticketCode, showtimeId, _activeKeyVersion);
        }

        public string SignTicket(string ticketCode, Guid showtimeId, string keyVersion)
        {
            if (string.IsNullOrWhiteSpace(ticketCode))
                throw new ArgumentException("Mã vé không được để trống.", nameof(ticketCode));

            if (showtimeId == Guid.Empty)
                throw new ArgumentException("Mã suất diễn không hợp lệ.", nameof(showtimeId));

            if (!_privateKeys.TryGetValue(keyVersion, out var privEcdsa))
            {
                throw new InvalidOperationException($"Máy chủ không có khoá riêng (Private Key) cho phiên bản '{keyVersion}' để ký số.");
            }

            // Chuẩn hoá dữ liệu cần ký (Canonical data)
            var canonicalData = BuildCanonicalData(ticketCode, showtimeId, keyVersion);
            var dataBytes = Encoding.UTF8.GetBytes(canonicalData);

            // Ký dữ liệu bằng ECDSA-SHA256
            var signatureBytes = privEcdsa.SignData(dataBytes, HashAlgorithmName.SHA256);
            var signatureBase64Url = ToBase64Url(signatureBytes);

            // Định dạng QR Payload chuẩn: TICKET|{keyVersion}|{ticketCode}|{showtimeId}|{signature}
            return $"TICKET|{keyVersion}|{ticketCode}|{showtimeId}|{signatureBase64Url}";
        }

        public QrVerificationResult VerifyTicket(string qrPayload)
        {
            if (string.IsNullOrWhiteSpace(qrPayload))
            {
                return QrVerificationResult.Failure("EMPTY_PAYLOAD", "Nội dung mã QR rỗng.");
            }

            var trimmed = qrPayload.Trim();

            // 1. Kiểm tra cấu trúc QR Payload
            string? keyVersion = null;
            string? ticketCode = null;
            Guid? showtimeId = null;
            string? signature = null;

            if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
            {
                // Thử phân tích JSON
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("ticketCode", out var tcProp)) ticketCode = tcProp.GetString();
                    if (root.TryGetProperty("showtimeId", out var stProp) && Guid.TryParse(stProp.GetString(), out var stGuid)) showtimeId = stGuid;
                    if (root.TryGetProperty("keyVersion", out var kvProp)) keyVersion = kvProp.GetString();
                    if (root.TryGetProperty("sig", out var sigProp) || root.TryGetProperty("signature", out sigProp)) signature = sigProp.GetString();
                }
                catch
                {
                    return QrVerificationResult.Failure("INVALID_FORMAT", "Mã QR có định dạng JSON không hợp lệ.");
                }
            }
            else if (trimmed.Contains('|'))
            {
                var parts = trimmed.Split('|');
                if (parts.Length >= 5 && parts[0].Equals("TICKET", StringComparison.OrdinalIgnoreCase))
                {
                    keyVersion = parts[1];
                    ticketCode = parts[2];
                    if (Guid.TryParse(parts[3], out var stGuid)) showtimeId = stGuid;
                    signature = parts[4];
                }
                else if (parts.Length == 4 && parts[0].Equals("TICKET", StringComparison.OrdinalIgnoreCase))
                {
                    // TICKET|keyVersion|ticketCode|showtimeId nhưng thiếu signature (AC3)
                    keyVersion = parts[1];
                    ticketCode = parts[2];
                    if (Guid.TryParse(parts[3], out var stGuid)) showtimeId = stGuid;
                    signature = null;
                }
                else if (parts.Length >= 4 && !parts[0].Equals("TICKET", StringComparison.OrdinalIgnoreCase))
                {
                    keyVersion = parts[0];
                    ticketCode = parts[1];
                    if (Guid.TryParse(parts[2], out var stGuid)) showtimeId = stGuid;
                    signature = parts[3];
                }
                else
                {
                    // Các định dạng phân cách nhưng không đủ trường
                    ticketCode = parts[0];
                }
            }
            else
            {
                // Chuỗi đơn thuần (ví dụ chỉ có ticketCode "TK-XXXX-...")
                ticketCode = trimmed;
            }

            // AC3: Giả sử một người tự tạo QR với mã vé hợp lệ nhưng không có chữ ký -> từ chối
            if (string.IsNullOrWhiteSpace(signature))
            {
                return QrVerificationResult.Failure(
                    "MISSING_SIGNATURE",
                    "Mã QR không có chữ ký số hợp lệ hoặc bị thiếu chữ ký.",
                    keyVersion,
                    ticketCode,
                    showtimeId
                );
            }

            if (string.IsNullOrWhiteSpace(ticketCode) || !showtimeId.HasValue || showtimeId.Value == Guid.Empty)
            {
                return QrVerificationResult.Failure(
                    "INVALID_FORMAT",
                    "Mã QR thiếu mã vé hoặc mã suất diễn hợp lệ.",
                    keyVersion,
                    ticketCode,
                    showtimeId
                );
            }

            if (string.IsNullOrWhiteSpace(keyVersion))
            {
                return QrVerificationResult.Failure(
                    "MISSING_KEY_VERSION",
                    "Mã QR không chứa thông tin phiên bản khoá (Key Version).",
                    null,
                    ticketCode,
                    showtimeId
                );
            }

            // AC2: Tìm khoá công khai tương ứng với keyVersion ghi trong mã QR
            if (!_publicKeys.TryGetValue(keyVersion, out var pubEcdsa))
            {
                return QrVerificationResult.Failure(
                    "UNKNOWN_KEY_VERSION",
                    $"Phiên bản khoá '{keyVersion}' không tồn tại hoặc chưa được hỗ trợ trên thiết bị quét.",
                    keyVersion,
                    ticketCode,
                    showtimeId
                );
            }

            // Giải mã chữ ký số
            byte[] signatureBytes;
            try
            {
                signatureBytes = FromBase64Url(signature);
            }
            catch
            {
                return QrVerificationResult.Failure(
                    "INVALID_SIGNATURE",
                    "Định dạng chữ ký số không hợp lệ.",
                    keyVersion,
                    ticketCode,
                    showtimeId
                );
            }

            // AC1: Xác thực chữ ký đối với dữ liệu chuẩn tắc (mã vé + mã suất + phiên bản khoá)
            var canonicalData = BuildCanonicalData(ticketCode, showtimeId.Value, keyVersion);
            var dataBytes = Encoding.UTF8.GetBytes(canonicalData);

            bool isSignatureValid = false;
            try
            {
                isSignatureValid = pubEcdsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi trong quá trình xác thực chữ ký ECDSA cho vé {TicketCode}", ticketCode);
                isSignatureValid = false;
            }

            if (!isSignatureValid)
            {
                return QrVerificationResult.Failure(
                    "INVALID_SIGNATURE",
                    "Chữ ký số không khớp hoặc mã QR đã bị chỉnh sửa.",
                    keyVersion,
                    ticketCode,
                    showtimeId
                );
            }

            return QrVerificationResult.Success(ticketCode, showtimeId.Value, keyVersion);
        }

        private static string BuildCanonicalData(string ticketCode, Guid showtimeId, string keyVersion)
        {
            return $"{ticketCode.Trim()}:{showtimeId}:{keyVersion.Trim()}";
        }

        private static string ToBase64Url(byte[] input)
        {
            return Convert.ToBase64String(input)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static byte[] FromBase64Url(string base64Url)
        {
            var base64 = base64Url.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            return Convert.FromBase64String(base64);
        }

        public void Dispose()
        {
            foreach (var kvp in _privateKeys)
            {
                kvp.Value.Dispose();
            }
            foreach (var kvp in _publicKeys)
            {
                kvp.Value.Dispose();
            }
            _privateKeys.Clear();
            _publicKeys.Clear();
            GC.SuppressFinalize(this);
        }
    }
}
