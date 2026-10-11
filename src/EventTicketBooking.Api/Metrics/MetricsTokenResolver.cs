using System.IO;
using Microsoft.Extensions.Configuration;

namespace EventTicketBooking.Api.Metrics;

public static class MetricsTokenResolver
{
    public const string DefaultSecretPath = "/run/secrets/metrics_scrape_token";

    public static string? ResolveScrapeToken(IConfiguration configuration, string? defaultSecretPath = null)
    {
        // a. Metrics:ScrapeToken nếu được cấu hình trực tiếp
        var directToken = (configuration["Metrics:ScrapeToken"] ?? configuration["METRICS_SCRAPE_TOKEN"])?.Trim();
        if (!string.IsNullOrWhiteSpace(directToken))
        {
            return directToken;
        }

        // b. Metrics:ScrapeTokenFile nếu có
        var filePath = (configuration["Metrics:ScrapeTokenFile"] ?? configuration["METRICS_SCRAPE_TOKEN_FILE"])?.Trim();
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            try
            {
                var content = File.ReadAllText(filePath).Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return content;
                }
            }
            catch
            {
                // Không log token hay lỗi chứa thông tin nhạy cảm
            }
        }

        // c. path mặc định /run/secrets/metrics_scrape_token nếu file tồn tại
        var fallbackPath = defaultSecretPath ?? DefaultSecretPath;
        if (File.Exists(fallbackPath))
        {
            try
            {
                var content = File.ReadAllText(fallbackPath).Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return content;
                }
            }
            catch
            {
                // Không log token
            }
        }

        return null;
    }
}
