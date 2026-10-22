using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class RegistrationOtpRateLimitTests : IDisposable
    {
        private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        private readonly IConfiguration _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "RateLimit_Secret_Test_Key_At_Least_32_Bytes_123456789",
            ["Jwt:Issuer"] = "EventTicketBooking.Api",
            ["Jwt:Audience"] = "EventTicketBooking.Client"
        }).Build();

        public void Dispose() => _db.Dispose();

        private sealed class TestClock : TimeProvider
        {
            private DateTimeOffset _now = DateTimeOffset.UtcNow;
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
        }

        [Fact]
        public async Task OtpLimiter_AllowsExactly5ResendsPerHour_AndBlocksOn6th()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var clock = new TestClock();
            var options = Microsoft.Extensions.Options.Options.Create(new RegistrationOtpRateLimitOptions
            {
                MaxResendAttemptsPerHour = 5,
                WindowSeconds = 3600
            });
            var limiter = new RegistrationOtpRateLimiter(null, cache, options, clock);
            var email = "customer@example.com";

            // 5 lần gửi lại đầu tiên thành công
            for (int i = 1; i <= 5; i++)
            {
                var result = await limiter.CheckAndRecordResendAsync(email);
                Assert.True(result.Allowed, $"Lần {i} phải được cho phép.");
                Assert.Equal(5 - i, result.RemainingAttempts);
            }

            // Lần gửi lại thứ 6 bị chặn (429)
            var blockedResult = await limiter.CheckAndRecordResendAsync(email);
            Assert.False(blockedResult.Allowed, "Lần thứ 6 phải bị chặn do vượt quá 5 lần/giờ.");
            Assert.Equal(0, blockedResult.RemainingAttempts);
            Assert.True(blockedResult.RetryAfterSeconds > 0);
            Assert.True(blockedResult.RetryAfterMinutes > 0);
        }

        [Fact]
        public async Task OtpLimiter_NormalizesEmail_CaseInsensitive()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var clock = new TestClock();
            var limiter = new RegistrationOtpRateLimiter(null, cache, null, clock);

            for (int i = 0; i < 5; i++)
            {
                var res = await limiter.CheckAndRecordResendAsync(i % 2 == 0 ? "USER@EXAMPLE.COM" : "user@example.com");
                Assert.True(res.Allowed);
            }

            // 6th attempt with mixed case is blocked
            var blocked = await limiter.CheckAndRecordResendAsync("User@Example.Com");
            Assert.False(blocked.Allowed);
        }

        [Fact]
        public async Task OtpLimiter_DifferentEmails_HaveIndependentLimits()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var clock = new TestClock();
            var limiter = new RegistrationOtpRateLimiter(null, cache, null, clock);

            for (int i = 0; i < 5; i++)
            {
                await limiter.CheckAndRecordResendAsync("email_a@example.com");
            }
            var blockedA = await limiter.CheckAndRecordResendAsync("email_a@example.com");
            Assert.False(blockedA.Allowed);

            // email_b vẫn được gửi
            var resultB = await limiter.CheckAndRecordResendAsync("email_b@example.com");
            Assert.True(resultB.Allowed);
            Assert.Equal(4, resultB.RemainingAttempts);
        }

        [Fact]
        public async Task OtpLimiter_ResetsAfterOneHour()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var clock = new TestClock();
            var limiter = new RegistrationOtpRateLimiter(null, cache, null, clock);
            var email = "timer_test@example.com";

            for (int i = 0; i < 5; i++)
            {
                await limiter.CheckAndRecordResendAsync(email);
            }
            Assert.False((await limiter.CheckAndRecordResendAsync(email)).Allowed);

            // Tua thời gian qua 1 giờ (3601 giây)
            clock.Advance(3601);

            // Sau 1 giờ, được phép gửi lại với chu kỳ mới
            var newResult = await limiter.CheckAndRecordResendAsync(email);
            Assert.True(newResult.Allowed);
            Assert.Equal(4, newResult.RemainingAttempts);
        }

        [Fact]
        public async Task OtpLimiter_ResetAsync_ClearsRateLimit()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var clock = new TestClock();
            var limiter = new RegistrationOtpRateLimiter(null, cache, null, clock);
            var email = "reset_test@example.com";

            for (int i = 0; i < 5; i++)
            {
                await limiter.CheckAndRecordResendAsync(email);
            }
            Assert.False((await limiter.CheckAndRecordResendAsync(email)).Allowed);

            // Gọi Reset (ví dụ kích hoạt thành công)
            await limiter.ResetAsync(email);

            // Được phép gửi lại
            var afterReset = await limiter.CheckAndRecordResendAsync(email);
            Assert.True(afterReset.Allowed);
            Assert.Equal(4, afterReset.RemainingAttempts);
        }

        [Fact]
        public async Task AuthController_ResendOtp_Returns429After5AttemptsInOneHour()
        {
            var user = new User
            {
                Email = "controller_test@example.com",
                FullName = "Tester",
                Username = "tester1",
                PasswordHash = "hash123",
                IsActive = false,
                VerificationCode = "123456",
                VerificationCodeExpiresAt = DateTime.UtcNow.AddMinutes(15)
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            using var cache = new MemoryCache(new MemoryCacheOptions());
            var clock = new TestClock();
            var limiter = new RegistrationOtpRateLimiter(null, cache, null, clock);
            var emailMock = new Mock<IEmailService>();

            var controller = new AuthController(
                Mock.Of<IAuthService>(),
                _db,
                Mock.Of<IPasswordHasher>(),
                emailMock.Object,
                new TokenService(_config),
                _config,
                Mock.Of<IHttpClientFactory>(),
                NullLogger<AuthController>.Instance,
                limiter)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            // 5 lần gửi lại đầu tiên thành công (HTTP 200)
            for (int i = 1; i <= 5; i++)
            {
                var response = await controller.ResendOtp(new ResendOtpDto { Email = user.Email });
                var okResult = Assert.IsType<OkObjectResult>(response);
                Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);
            }

            // Lần thứ 6 trả về HTTP 429 TooManyRequests
            var blockedResponse = await controller.ResendOtp(new ResendOtpDto { Email = user.Email });
            var objectResult = Assert.IsType<ObjectResult>(blockedResponse);
            Assert.Equal(StatusCodes.Status429TooManyRequests, objectResult.StatusCode);

            // Header Retry-After được thiết lập
            Assert.True(controller.Response.Headers.ContainsKey("Retry-After"));

            // Đảm bảo email service chỉ gửi đúng 5 lần
            emailMock.Verify(e => e.SendOtpEmailAsync(user.Email, It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(5));
        }

        [Fact]
        public async Task AuthController_ResendConfirmationLink_AlsoEnforces5PerRequirement()
        {
            var user = new User
            {
                Email = "link_test@example.com",
                FullName = "Link Tester",
                Username = "linktester",
                PasswordHash = "hash123",
                IsActive = false,
                VerificationCode = "initial_token",
                VerificationCodeExpiresAt = DateTime.UtcNow.AddHours(24)
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            using var cache = new MemoryCache(new MemoryCacheOptions());
            var clock = new TestClock();
            var limiter = new RegistrationOtpRateLimiter(null, cache, null, clock);

            var controller = new AuthController(
                Mock.Of<IAuthService>(),
                _db,
                Mock.Of<IPasswordHasher>(),
                Mock.Of<IEmailService>(),
                new TokenService(_config),
                _config,
                Mock.Of<IHttpClientFactory>(),
                NullLogger<AuthController>.Instance,
                limiter)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            for (int i = 1; i <= 5; i++)
            {
                var response = await controller.ResendConfirmationLink(new ResendOtpDto { Email = user.Email });
                var okResult = Assert.IsType<OkObjectResult>(response);
                Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);
            }

            var blocked = await controller.ResendConfirmationLink(new ResendOtpDto { Email = user.Email });
            var blockedResult = Assert.IsType<ObjectResult>(blocked);
            Assert.Equal(StatusCodes.Status429TooManyRequests, blockedResult.StatusCode);
        }
    }
}
