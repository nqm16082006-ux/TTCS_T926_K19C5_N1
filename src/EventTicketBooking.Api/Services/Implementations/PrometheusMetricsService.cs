using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Monitoring;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventTicketBooking.Api.Services.Implementations
{
    public class PrometheusMetricsService : IPrometheusMetricsService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<PrometheusMetricsService> _logger;

        public PrometheusMetricsService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<PrometheusMetricsService>? logger = null)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger ?? NullLogger<PrometheusMetricsService>.Instance;
        }

        public async Task<MonitoringSummaryDto> GetMonitoringSummaryAsync(CancellationToken cancellationToken = default)
        {
            var baseUrl = GetBaseUrl();

            var summary = new MonitoringSummaryDto
            {
                GeneratedAt = DateTimeOffset.UtcNow
            };

            try
            {
                // 1. Kiểm tra readiness Prometheus endpoint (3s timeout)
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

                var readyUrl = $"{baseUrl}/-/ready";
                using var readyResponse = await _httpClient.GetAsync(readyUrl, timeoutCts.Token);
                if (!readyResponse.IsSuccessStatusCode)
                {
                    summary.PrometheusAvailable = false;
                    summary.Message = $"Prometheus trả về mã {readyResponse.StatusCode}. Vui lòng kiểm tra lại dịch vụ Prometheus.";
                    return summary;
                }

                // 2. Query song song các chỉ số từ Prometheus
                var totalReqTask = QueryScalarAsync(baseUrl, "sum(rate(http_requests_received_total[1m]))", timeoutCts.Token);
                var errReqTask = QueryScalarAsync(baseUrl, "sum(rate(http_requests_received_total{code=~\"5..\"}[1m]))", timeoutCts.Token);
                var p95Task = QueryScalarAsync(baseUrl, "histogram_quantile(0.95, sum(rate(http_request_duration_seconds_bucket[1m])) by (le))", timeoutCts.Token);
                var seatConflictsTask = QueryScalarAsync(baseUrl, "(sum(increase(seat_hold_conflicts_total[5m])) or vector(0))", timeoutCts.Token);
                var webhooksTask = QueryScalarAsync(baseUrl, "(sum(increase(payment_webhooks_rejected_total[5m])) or vector(0))", timeoutCts.Token);

                await Task.WhenAll(totalReqTask, errReqTask, p95Task, seatConflictsTask, webhooksTask);

                var totalReqRate = await totalReqTask;
                var errReqRate = await errReqTask;
                var p95Seconds = await p95Task;
                var seatConflicts = await seatConflictsTask;
                var webhooksRejected = await webhooksTask;

                summary.PrometheusAvailable = true;
                summary.RequestRate = totalReqRate.HasValue ? Math.Round(totalReqRate.Value, 2) : null;

                if (totalReqRate.HasValue && totalReqRate.Value > 0)
                {
                    var errRate = (errReqRate ?? 0) / totalReqRate.Value * 100.0;
                    summary.ErrorRatePercent = Math.Round(errRate, 2);
                }
                else if (totalReqRate.HasValue)
                {
                    summary.ErrorRatePercent = 0.0;
                }

                if (p95Seconds.HasValue && !double.IsNaN(p95Seconds.Value) && !double.IsInfinity(p95Seconds.Value))
                {
                    summary.P95ResponseTimeMs = Math.Round(p95Seconds.Value * 1000.0, 1);
                }
                else
                {
                    summary.P95ResponseTimeMs = null;
                }

                summary.SeatConflicts = seatConflicts.HasValue ? Math.Round(seatConflicts.Value, 0) : null;
                summary.RejectedWebhooks = webhooksRejected.HasValue ? Math.Round(webhooksRejected.Value, 0) : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prometheus metrics scrape unavailable at {BaseUrl}", baseUrl);
                summary.PrometheusAvailable = false;
                summary.RequestRate = null;
                summary.ErrorRatePercent = null;
                summary.P95ResponseTimeMs = null;
                summary.SeatConflicts = null;
                summary.RejectedWebhooks = null;
                summary.Message = "Không thể kết nối đến Prometheus local (" + baseUrl + "). Vui lòng khởi động monitoring stack.";
            }

            return summary;
        }

        public async Task<MonitoringTimeSeriesDto> GetMonitoringTimeSeriesAsync(
            int minutes = 15,
            int stepSeconds = 15,
            CancellationToken cancellationToken = default)
        {
            if (minutes < 1) minutes = 1;
            if (minutes > 60) minutes = 60;
            if (stepSeconds < 5) stepSeconds = 5;
            if (stepSeconds > 60) stepSeconds = 60;

            var baseUrl = GetBaseUrl();
            var now = DateTimeOffset.UtcNow;
            var endEpoch = now.ToUnixTimeSeconds();
            var startEpoch = now.AddMinutes(-minutes).ToUnixTimeSeconds();

            var result = new MonitoringTimeSeriesDto
            {
                GeneratedAt = now
            };

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));

                var readyUrl = $"{baseUrl}/-/ready";
                using var readyResponse = await _httpClient.GetAsync(readyUrl, timeoutCts.Token);
                if (!readyResponse.IsSuccessStatusCode)
                {
                    result.PrometheusAvailable = false;
                    result.Message = $"Prometheus không khả dụng ({readyResponse.StatusCode}).";
                    return result;
                }

                var reqRangeTask = QueryMatrixAsync(baseUrl, "sum(rate(http_requests_received_total[1m]))", startEpoch, endEpoch, stepSeconds, timeoutCts.Token);
                var errRangeTask = QueryMatrixAsync(baseUrl, "((sum(rate(http_requests_received_total{code=~\"5..\"}[1m])) or vector(0)) / sum(rate(http_requests_received_total[1m]))) * 100", startEpoch, endEpoch, stepSeconds, timeoutCts.Token);
                var p95RangeTask = QueryMatrixAsync(baseUrl, "histogram_quantile(0.95, sum(rate(http_request_duration_seconds_bucket[1m])) by (le)) * 1000", startEpoch, endEpoch, stepSeconds, timeoutCts.Token);

                await Task.WhenAll(reqRangeTask, errRangeTask, p95RangeTask);

                result.PrometheusAvailable = true;
                result.RequestRateSeries = await reqRangeTask;
                result.ErrorRateSeries = await errRangeTask;
                result.P95LatencySeries = await p95RangeTask;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prometheus time series query unavailable at {BaseUrl}", baseUrl);
                result.PrometheusAvailable = false;
                result.Message = "Không thể kết nối đến Prometheus local (" + baseUrl + ").";
            }

            return result;
        }

        private string GetBaseUrl()
        {
            return (_configuration["Monitoring:PrometheusBaseUrl"]
                    ?? _configuration["MONITORING_PROMETHEUS_BASE_URL"]
                    ?? "http://127.0.0.1:9090").Trim().TrimEnd('/');
        }

        private async Task<double?> QueryScalarAsync(string baseUrl, string promql, CancellationToken ct)
        {
            try
            {
                var url = $"{baseUrl}/api/v1/query?query={Uri.EscapeDataString(promql)}";
                using var response = await _httpClient.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var jsonStr = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var statusProp) &&
                    statusProp.GetString() == "success" &&
                    root.TryGetProperty("data", out var dataProp) &&
                    dataProp.TryGetProperty("result", out var resultProp) &&
                    resultProp.ValueKind == JsonValueKind.Array &&
                    resultProp.GetArrayLength() > 0)
                {
                    var firstResult = resultProp[0];
                    if (firstResult.TryGetProperty("value", out var valueProp) &&
                        valueProp.ValueKind == JsonValueKind.Array &&
                        valueProp.GetArrayLength() >= 2)
                    {
                        var rawVal = valueProp[1].GetString();
                        if (!string.IsNullOrWhiteSpace(rawVal) &&
                            double.TryParse(rawVal, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
                        {
                            if (!double.IsNaN(parsed) && !double.IsInfinity(parsed))
                            {
                                return parsed;
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to query PromQL: {Query}", promql);
                return null;
            }
        }

        private async Task<List<MetricDataPointDto>> QueryMatrixAsync(
            string baseUrl,
            string promql,
            long startEpoch,
            long endEpoch,
            int stepSeconds,
            CancellationToken ct)
        {
            var points = new List<MetricDataPointDto>();
            try
            {
                var url = $"{baseUrl}/api/v1/query_range?query={Uri.EscapeDataString(promql)}&start={startEpoch}&end={endEpoch}&step={stepSeconds}s";
                using var response = await _httpClient.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                {
                    return points;
                }

                var jsonStr = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var statusProp) &&
                    statusProp.GetString() == "success" &&
                    root.TryGetProperty("data", out var dataProp) &&
                    dataProp.TryGetProperty("result", out var resultProp) &&
                    resultProp.ValueKind == JsonValueKind.Array &&
                    resultProp.GetArrayLength() > 0)
                {
                    var firstResult = resultProp[0];
                    if (firstResult.TryGetProperty("values", out var valuesProp) &&
                        valuesProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in valuesProp.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
                            {
                                var epoch = item[0].GetDouble();
                                var rawVal = item[1].GetString();
                                double? pointVal = null;

                                if (!string.IsNullOrWhiteSpace(rawVal) &&
                                    double.TryParse(rawVal, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
                                {
                                    if (!double.IsNaN(parsed) && !double.IsInfinity(parsed))
                                    {
                                        pointVal = Math.Round(parsed, 2);
                                    }
                                }

                                points.Add(new MetricDataPointDto
                                {
                                    Timestamp = epoch,
                                    Value = pointVal
                                });
                            }
                        }
                    }
                }

                return points;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to query range PromQL: {Query}", promql);
                return points;
            }
        }
    }
}
