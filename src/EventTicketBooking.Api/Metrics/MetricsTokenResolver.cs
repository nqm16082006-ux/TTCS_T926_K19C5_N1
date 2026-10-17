using System.IO;
using Microsoft.Extensions.Configuration;

namespace EventTicketBooking.Api.Metrics;

public static class MetricsTokenResolver
{
    public const string DefaultSecretPath = "/run/secrets/metrics_scrape_token";

    public static string? ResolveScrapeToken(IConfiguration configuration, string? defaultSecretPath = null)
    {
        // a. Metrics:ScrapeToken nếu được cấu hình trực tiếp
        var rawDirect = configuration["Metrics:ScrapeToken"] ?? configuration["METRICS_SCRAPE_TOKEN"];
        if (rawDirect != null)
        {
            var directTrimmed = rawDirect.Trim();
            return !string.IsNullOrWhiteSpace(directTrimmed) ? directTrimmed : null;
        }

        // b. Metrics:ScrapeTokenFile nếu có
        var rawFilePath = configuration["Metrics:ScrapeTokenFile"] ?? configuration["METRICS_SCRAPE_TOKEN_FILE"];
        if (rawFilePath != null)
        {
            var filePath = rawFilePath.Trim();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            var resolvedFile = ResolveFilePath(filePath);
            if (resolvedFile != null && File.Exists(resolvedFile))
            {
                try
                {
                    var content = File.ReadAllText(resolvedFile).Trim();
                    return !string.IsNullOrWhiteSpace(content) ? content : null;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        // c. Nếu có defaultSecretPath tùy biến do caller truyền (thường trong unit test)
        if (defaultSecretPath != null)
        {
            if (File.Exists(defaultSecretPath))
            {
                try
                {
                    var content = File.ReadAllText(defaultSecretPath).Trim();
                    return !string.IsNullOrWhiteSpace(content) ? content : null;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        // d. Tự động tìm kiếm file secret cục bộ (monitoring/secrets/metrics_scrape_token)
        // khi chạy local phát triển qua dotnet run (hỗ trợ cả cwd là repo root hoặc src/EventTicketBooking.Api)
        var localCandidate = FindLocalSecretFile(Path.Combine("monitoring", "secrets", "metrics_scrape_token"));
        if (localCandidate != null && File.Exists(localCandidate))
        {
            try
            {
                var content = File.ReadAllText(localCandidate).Trim();
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

        // e. Path mặc định trong container (/run/secrets/metrics_scrape_token)
        if (File.Exists(DefaultSecretPath))
        {
            try
            {
                var content = File.ReadAllText(DefaultSecretPath).Trim();
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

    private static string? ResolveFilePath(string filePath)
    {
        if (File.Exists(filePath))
        {
            return filePath;
        }

        if (Path.IsPathRooted(filePath))
        {
            return null;
        }

        var candidate = FindLocalSecretFile(filePath);
        return candidate;
    }

    private static string? FindLocalSecretFile(string relativeSubPath)
    {
        var baseDirs = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var baseDir in baseDirs)
        {
            if (string.IsNullOrWhiteSpace(baseDir)) continue;

            var dir = new DirectoryInfo(baseDir);
            while (dir != null)
            {
                var fullPath = Path.Combine(dir.FullName, relativeSubPath);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }

                dir = dir.Parent;
            }
        }

        return null;
    }
}
