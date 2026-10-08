using System.Collections.Generic;

namespace EventTicketBooking.Api.Options
{
    /// <summary>
    /// Cấu hình khoá ký số cho mã QR vé điện tử (Story S-26).
    /// Hỗ trợ Key Rotation (nhiều phiên bản khoá v1, v2,...) và lưu trữ khoá công khai/bí mật.
    /// </summary>
    public class TicketQrOptions
    {
        public const string SectionName = "TicketQrSettings";

        /// <summary>
        /// Phiên bản khoá đang kích hoạt dùng để ký vé mới (vd: "v1", "v2").
        /// </summary>
        public string ActiveKeyVersion { get; set; } = "v1";

        /// <summary>
        /// Danh sách cấu hình khoá theo từng phiên bản.
        /// </summary>
        public Dictionary<string, TicketKeyConfig> Keys { get; set; } = new()
        {
            ["v1"] = new TicketKeyConfig
            {
                PrivateKey = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg8pVjOkQcPRG9W2980k+OiW4zoHG9ljgjBoM2gbglAhOhRANCAASEq83y5nK6ZeZz59LpDG0XvZ4TnYuH82L3B2ngBHMtzlsm/Ge2Onat8TsHbM/nUKs/Md/p2xnf+GnsdgJRzYkS",
                PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhKvN8uZyumXmc+fS6QxtF72eE52Lh/Ni9wdp4ARzLc5bJvxntjp2rfE7B2zP51CrPzHf6dsZ3/hp7HYCUc2JEg=="
            },
            ["v2"] = new TicketKeyConfig
            {
                PrivateKey = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgCtluUw+SBCQ7hvaRwMZ/im9nChLzB7S2Tzu1D8jVLJuhRANCAATN4oVHHhjDJV1eL7WNSW2o5iSwiOlZQrcXfR5fEyAXOu9hd/5eQ8yq43HKLZHTwg9pNdOrjc04HFTGIJpOsp07",
                PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEzeKFRx4YwyVdXi+1jUltqOYksIjpWUK3F30eXxMgFzrvYXf+XkPMquNxyi2R08IPaTXTq43NOBxUxiCaTrKdOw=="
            }
        };
    }

    public class TicketKeyConfig
    {
        /// <summary>
        /// Khoá bí mật (Private Key) dạng PKCS#8 Base64 (chỉ lưu trên máy chủ để ký vé).
        /// </summary>
        public string? PrivateKey { get; set; }

        /// <summary>
        /// Khoá công khai (Public Key) dạng SubjectPublicKeyInfo Base64 (dùng để xác thực vé).
        /// </summary>
        public string PublicKey { get; set; } = string.Empty;
    }
}
