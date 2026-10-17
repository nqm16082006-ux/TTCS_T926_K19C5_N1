using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Monitoring;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class AdminMonitoringTests
    {
        [Fact]
        public void AdminMonitoringController_HasRequireRoleAdminAttribute()
        {
            var attrs = typeof(AdminMonitoringController).GetCustomAttributes(typeof(RequireRoleAttribute), true);
            Assert.NotEmpty(attrs);
            var requireRole = (RequireRoleAttribute)attrs[0];
            Assert.Contains("Admin", requireRole.Roles);
            Assert.Single(requireRole.Roles);
        }

        [Fact]
        public async Task PrometheusMetricsService_WhenPrometheusReturnsData_ParsesAllMetricsCorrectly()
        {
            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
                {
                    var query = req.RequestUri?.Query ?? "";
                    string json;

                    if (query.Contains("http_requests_received_total%5B1m%5D"))
                    {
                        // Total request rate: 20 req/s
                        json = "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{},\"value\":[1700000000,\"20.0\"]}]}}";
                    }
                    else if (query.Contains("code%3D~%225..%22"))
                    {
                        // 5xx error rate: 1 req/s -> 1 / 20 = 5.0%
                        json = "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{},\"value\":[1700000000,\"1.0\"]}]}}";
                    }
                    else if (query.Contains("histogram_quantile"))
                    {
                        // p95 latency: 0.150 seconds -> 150.0 ms
                        json = "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{},\"value\":[1700000000,\"0.150\"]}]}}";
                    }
                    else if (query.Contains("seat_hold_conflicts_total"))
                    {
                        // Seat conflicts: 7
                        json = "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{},\"value\":[1700000000,\"7\"]}]}}";
                    }
                    else if (query.Contains("payment_webhooks_rejected_total"))
                    {
                        // Rejected webhooks: 2
                        json = "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{},\"value\":[1700000000,\"2\"]}]}}";
                    }
                    else
                    {
                        json = "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}";
                    }

                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                });

            var httpClient = new HttpClient(handlerMock.Object);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:PrometheusBaseUrl"] = "http://127.0.0.1:9090"
            }).Build();

            var service = new PrometheusMetricsService(httpClient, config, NullLogger<PrometheusMetricsService>.Instance);
            var result = await service.GetMonitoringSummaryAsync();

            Assert.True(result.PrometheusAvailable);
            Assert.Equal(20.0, result.RequestRate);
            Assert.Equal(5.0, result.ErrorRatePercent);
            Assert.Equal(150.0, result.P95ResponseTimeMs);
            Assert.Equal(7.0, result.SeatConflicts);
            Assert.Equal(2.0, result.RejectedWebhooks);
            Assert.Null(result.Message);
        }

        [Fact]
        public async Task PrometheusMetricsService_WhenPrometheusIsDown_ReturnsGracefulUnavailableState()
        {
            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("Connection refused"));

            var httpClient = new HttpClient(handlerMock.Object);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:PrometheusBaseUrl"] = "http://127.0.0.1:9090"
            }).Build();

            var service = new PrometheusMetricsService(httpClient, config, NullLogger<PrometheusMetricsService>.Instance);
            var result = await service.GetMonitoringSummaryAsync();

            Assert.False(result.PrometheusAvailable);
            Assert.Null(result.RequestRate);
            Assert.Null(result.ErrorRatePercent);
            Assert.Null(result.P95ResponseTimeMs);
            Assert.Null(result.SeatConflicts);
            Assert.Null(result.RejectedWebhooks);
            Assert.NotNull(result.Message);
            Assert.Contains("Không thể kết nối", result.Message);
        }

        [Fact]
        public async Task GetAlerts_FiltersByStatusAndSeverity_ReturnsMatchingAlertsInOrder()
        {
            var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new AppDbContext(dbOptions);

            var now = DateTimeOffset.UtcNow;
            db.SystemAlerts.AddRange(
                new SystemAlert
                {
                    Id = Guid.NewGuid(),
                    Fingerprint = "fp-1",
                    AlertName = "Http5xxErrorRateWarning",
                    Severity = "warning",
                    Summary = "Lỗi 5xx tăng nhẹ",
                    Status = "firing",
                    CreatedAt = now.AddMinutes(-10),
                    LastUpdatedAt = now.AddMinutes(-5)
                },
                new SystemAlert
                {
                    Id = Guid.NewGuid(),
                    Fingerprint = "fp-2",
                    AlertName = "Http5xxErrorRateCritical",
                    Severity = "critical",
                    Summary = "Lỗi 5xx nghiêm trọng",
                    Status = "firing",
                    CreatedAt = now.AddMinutes(-8),
                    LastUpdatedAt = now.AddMinutes(-1)
                },
                new SystemAlert
                {
                    Id = Guid.NewGuid(),
                    Fingerprint = "fp-3",
                    AlertName = "HttpLatencyP95Critical",
                    Severity = "critical",
                    Summary = "Độ trễ p95 cao",
                    Status = "resolved",
                    CreatedAt = now.AddMinutes(-20),
                    LastUpdatedAt = now.AddMinutes(-15)
                }
            );
            await db.SaveChangesAsync();

            var mockMetrics = new Mock<IPrometheusMetricsService>();
            var controller = new AdminMonitoringController(mockMetrics.Object, db);

            // Filter: status = firing, severity = critical
            var actionResult = await controller.GetAlerts(status: "firing", severity: "critical");
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var apiResponse = Assert.IsType<ApiResponse<List<SystemAlertResponseDto>>>(okResult.Value);

            Assert.True(apiResponse.Success);
            Assert.NotNull(apiResponse.Data);
            Assert.Single(apiResponse.Data);
            Assert.Equal("fp-2", apiResponse.Data[0].Fingerprint);
            Assert.Equal("critical", apiResponse.Data[0].Severity);
            Assert.Equal("firing", apiResponse.Data[0].Status);
        }

        [Fact]
        public async Task GetAlerts_InvalidStatus_ReturnsBadRequest()
        {
            var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new AppDbContext(dbOptions);
            var mockMetrics = new Mock<IPrometheusMetricsService>();
            var controller = new AdminMonitoringController(mockMetrics.Object, db);

            var actionResult = await controller.GetAlerts(status: "invalid_status");
            var badResult = Assert.IsType<BadRequestObjectResult>(actionResult);
            var apiResponse = Assert.IsType<ApiResponse<object>>(badResult.Value);
            Assert.False(apiResponse.Success);
        }

        [Fact]
        public async Task GetAlerts_SearchTerm_FiltersByAlertNameOrSummary()
        {
            var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new AppDbContext(dbOptions);

            var now = DateTimeOffset.UtcNow;
            db.SystemAlerts.AddRange(
                new SystemAlert
                {
                    Id = Guid.NewGuid(),
                    Fingerprint = "fp-a",
                    AlertName = "SeatHoldConflictsRateWarning",
                    Severity = "warning",
                    Summary = "Tranh chấp giữ ghế tăng cao",
                    Status = "firing",
                    CreatedAt = now.AddMinutes(-5),
                    LastUpdatedAt = now.AddMinutes(-2)
                },
                new SystemAlert
                {
                    Id = Guid.NewGuid(),
                    Fingerprint = "fp-b",
                    AlertName = "PaymentWebhooksRejectedCritical",
                    Severity = "critical",
                    Summary = "Webhook PayOS bị từ chối",
                    Status = "firing",
                    CreatedAt = now.AddMinutes(-4),
                    LastUpdatedAt = now.AddMinutes(-1)
                }
            );
            await db.SaveChangesAsync();

            var mockMetrics = new Mock<IPrometheusMetricsService>();
            var controller = new AdminMonitoringController(mockMetrics.Object, db);

            var actionResult = await controller.GetAlerts(search: "SeatHold");
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var apiResponse = Assert.IsType<ApiResponse<List<SystemAlertResponseDto>>>(okResult.Value);

            Assert.True(apiResponse.Success);
            Assert.NotNull(apiResponse.Data);
            Assert.Single(apiResponse.Data);
            Assert.Equal("SeatHoldConflictsRateWarning", apiResponse.Data[0].AlertName);
        }

        [Fact]
        public async Task PrometheusMetricsService_WhenPrometheusReturnsRangeData_ParsesTimeSeriesCorrectly()
        {
            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
                {
                    var uri = req.RequestUri?.ToString() ?? "";
                    if (uri.Contains("/-/ready"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK);
                    }

                    if (uri.Contains("query_range"))
                    {
                        var json = @"{
                            ""status"": ""success"",
                            ""data"": {
                                ""resultType"": ""matrix"",
                                ""result"": [
                                    {
                                        ""metric"": {},
                                        ""values"": [
                                            [ 1700000000, ""15.5"" ],
                                            [ 1700000015, ""18.2"" ]
                                        ]
                                    }
                                ]
                            }
                        }";
                        return new HttpResponseMessage
                        {
                            StatusCode = HttpStatusCode.OK,
                            Content = new StringContent(json, Encoding.UTF8, "application/json")
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

            var httpClient = new HttpClient(handlerMock.Object);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:PrometheusBaseUrl"] = "http://127.0.0.1:9090"
            }).Build();

            var service = new PrometheusMetricsService(httpClient, config, NullLogger<PrometheusMetricsService>.Instance);
            var result = await service.GetMonitoringTimeSeriesAsync(minutes: 15, stepSeconds: 15);

            Assert.True(result.PrometheusAvailable);
            Assert.Equal(2, result.RequestRateSeries.Count);
            Assert.Equal(15.5, result.RequestRateSeries[0].Value);
            Assert.Equal(18.2, result.RequestRateSeries[1].Value);
        }

        [Fact]
        public async Task PrometheusMetricsService_WhenPrometheusDown_ReturnsGracefulTimeSeriesWithoutException()
        {
            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("Connection refused"));

            var httpClient = new HttpClient(handlerMock.Object);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:PrometheusBaseUrl"] = "http://127.0.0.1:9090"
            }).Build();

            var service = new PrometheusMetricsService(httpClient, config, NullLogger<PrometheusMetricsService>.Instance);
            var result = await service.GetMonitoringTimeSeriesAsync(minutes: 15, stepSeconds: 15);

            Assert.False(result.PrometheusAvailable);
            Assert.Empty(result.RequestRateSeries);
            Assert.Empty(result.ErrorRateSeries);
            Assert.Empty(result.P95LatencySeries);
            Assert.NotNull(result.Message);
        }

        [Fact]
        public async Task AdminMonitoringController_GetTimeSeries_Returns200WithTimeSeriesDto()
        {
            var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new AppDbContext(dbOptions);
            var mockMetrics = new Mock<IPrometheusMetricsService>();
            mockMetrics.Setup(m => m.GetMonitoringTimeSeriesAsync(15, 15, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MonitoringTimeSeriesDto
                {
                    PrometheusAvailable = true,
                    GeneratedAt = DateTimeOffset.UtcNow,
                    RequestRateSeries = new List<MetricDataPointDto>
                    {
                        new() { Timestamp = 1700000000, Value = 12.0 }
                    }
                });

            var controller = new AdminMonitoringController(mockMetrics.Object, db);
            var actionResult = await controller.GetTimeSeries(15, 15);

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var apiResponse = Assert.IsType<ApiResponse<MonitoringTimeSeriesDto>>(okResult.Value);

            Assert.True(apiResponse.Success);
            Assert.NotNull(apiResponse.Data);
            Assert.True(apiResponse.Data.PrometheusAvailable);
            Assert.Single(apiResponse.Data.RequestRateSeries);
        }

        [Fact]
        public async Task PrometheusMetricsService_WhenPrometheusReturnsZeroForConflictsAndWebhooks_PreservesZeroCorrectly()
        {
            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
                {
                    var query = req.RequestUri?.Query ?? "";
                    string json;

                    if (query.Contains("seat_hold_conflicts_total"))
                    {
                        // Prometheus evaluates (sum(increase(...)) or vector(0)) -> 0
                        json = "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{},\"value\":[1700000000,\"0\"]}]}}";
                    }
                    else if (query.Contains("payment_webhooks_rejected_total"))
                    {
                        // Prometheus evaluates (sum(increase(...)) or vector(0)) -> 0
                        json = "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{},\"value\":[1700000000,\"0\"]}]}}";
                    }
                    else if (query.Contains("http_requests_received_total%5B1m%5D"))
                    {
                        // Request rate is 10.0 req/s
                        json = "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{},\"value\":[1700000000,\"10.0\"]}]}}";
                    }
                    else
                    {
                        // 5xx errors: empty vector -> 0 errors
                        json = "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}";
                    }

                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                });

            var httpClient = new HttpClient(handlerMock.Object);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:PrometheusBaseUrl"] = "http://127.0.0.1:9090"
            }).Build();

            var service = new PrometheusMetricsService(httpClient, config, NullLogger<PrometheusMetricsService>.Instance);
            var result = await service.GetMonitoringSummaryAsync();

            Assert.True(result.PrometheusAvailable);
            Assert.Equal(10.0, result.RequestRate);
            Assert.Equal(0.0, result.ErrorRatePercent);
            Assert.Equal(0.0, result.SeatConflicts);
            Assert.Equal(0.0, result.RejectedWebhooks);
            Assert.Null(result.P95ResponseTimeMs); // No latency samples -> null
        }
    }
}
