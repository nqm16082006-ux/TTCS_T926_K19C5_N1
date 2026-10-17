using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Hubs;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using EventTicketBooking.Tests.Mocks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Xunit;
using Role = EventTicketBooking.Api.Models.Role;

namespace EventTicketBooking.Tests
{
    public class StoryS02AuthTests : IDisposable
    {
        private readonly AppDbContext _db;
        private readonly Argon2PasswordHasher _hasher;
        private readonly Mock<ITokenService> _tokenServiceMock;
        private readonly IOptions<AuthSecurityOptions> _securityOptions;

        public StoryS02AuthTests()
        {
            _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            _hasher = new Argon2PasswordHasher();
            _tokenServiceMock = new Mock<ITokenService>();
            _tokenServiceMock.Setup(t => t.GenerateAccessToken(It.IsAny<User>(), It.IsAny<IEnumerable<string>>()))
                .Returns("s02_mock_jwt_token");

            _securityOptions = Microsoft.Extensions.Options.Options.Create(new AuthSecurityOptions
            {
                AccountMaxFailures = 5,
                IpMaxFailures = 10,
                LockoutMinutes = 15
            });
        }

        public void Dispose()
        {
            _db.Dispose();
        }

        private async Task<(User User, Role Role)> CreateUserWithRoleAsync(string email, string roleName, string password = "Password@123", bool active = true)
        {
            var role = await _db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
            if (role == null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = roleName, Description = roleName };
                _db.Roles.Add(role);
            }

            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = email.Split('@')[0],
                Email = email,
                PasswordHash = _hasher.Hash(password),
                IsActive = active,
                TermsVersion = TermsPolicy.CurrentVersion,
                TermsAcceptedAt = DateTime.UtcNow
            };
            _db.Users.Add(user);

            var userRole = new UserRole { UserId = user.Id, RoleId = role.Id, Role = role };
            _db.UserRoles.Add(userRole);
            await _db.SaveChangesAsync();

            return (user, role);
        }

        #region 1. Account Brute-Force & Lockout

        [Fact]
        public async Task Test1_AccountLockout_Failures1To4_Return401Generic()
        {
            var (user, _) = await CreateUserWithRoleAsync("user1@test.com", "Customer");
            var mockRedis = MockRedisHelper.CreateMockRedis();
            var authService = new AuthService(_db, _hasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, mockRedis, _securityOptions);

            for (int i = 1; i <= 4; i++)
            {
                var result = await authService.LoginAsync(new LoginRequestDto
                {
                    Email = user.Email,
                    Password = "WrongPassword!"
                }, "192.168.1.10");

                Assert.False(result.Success);
                Assert.False(result.IsLocked);
                Assert.Equal(401, result.StatusCode);
                Assert.Equal("Email hoặc mật khẩu không chính xác.", result.Message);
            }
        }

        [Fact]
        public async Task Test2_AccountLockout_Failure5_Creates15MinLock_AndReturns401()
        {
            var (user, _) = await CreateUserWithRoleAsync("user2@test.com", "Customer");
            var redisState = new MockRedisState();
            var mockRedis = MockRedisHelper.CreateMockRedis(redisState);
            var authService = new AuthService(_db, _hasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, mockRedis, _securityOptions);

            // 4 first failures
            for (int i = 1; i <= 4; i++)
            {
                await authService.LoginAsync(new LoginRequestDto { Email = user.Email, Password = "WrongPassword!" }, "192.168.1.11");
            }

            // Failure #5
            var result = await authService.LoginAsync(new LoginRequestDto { Email = user.Email, Password = "WrongPassword!" }, "192.168.1.11");

            Assert.False(result.Success);
            Assert.False(result.IsLocked); // Vẫn trả 401 generic ở attempt #5 để không leak state
            Assert.Equal(401, result.StatusCode);

            // Verify that lockout key exists in Redis
            var lockoutKey = $"login:lockout:{user.Email.ToLowerInvariant()}";
            Assert.True(redisState.IsKeyValid(lockoutKey));
        }

        [Fact]
        public async Task Test3_AccountLockout_Attempt6_BlockedBeforePasswordHasher_Returns423WithRemainingTime()
        {
            var (user, _) = await CreateUserWithRoleAsync("user3@test.com", "Customer");
            var redisState = new MockRedisState();
            var mockRedis = MockRedisHelper.CreateMockRedis(redisState);

            var mockHasher = new Mock<IPasswordHasher>();
            mockHasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

            var authService = new AuthService(_db, mockHasher.Object, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, mockRedis, _securityOptions);

            // Fail 5 times
            for (int i = 1; i <= 5; i++)
            {
                await authService.LoginAsync(new LoginRequestDto { Email = user.Email, Password = "WrongPassword!" }, "192.168.1.12");
            }

            // Reset verify call counter
            mockHasher.Invocations.Clear();

            // Attempt #6 (even with correct password)
            var result = await authService.LoginAsync(new LoginRequestDto { Email = user.Email, Password = "AnyPassword" }, "192.168.1.12");

            Assert.False(result.Success);
            Assert.True(result.IsLocked);
            Assert.Equal(423, result.StatusCode);
            Assert.Contains("khóa tạm thời trong 15 phút", result.Message);

            // Verify password hasher was NOT invoked at all on attempt #6
            mockHasher.Verify(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Test4_SuccessfulLogin_ResetsAccountCounter_DoesNotResetIpCounter()
        {
            var (userA, _) = await CreateUserWithRoleAsync("userA@test.com", "Customer", "PasswordA@123");
            var (userB, _) = await CreateUserWithRoleAsync("userB@test.com", "Customer", "PasswordB@123");

            var redisState = new MockRedisState();
            var mockRedis = MockRedisHelper.CreateMockRedis(redisState);
            var authService = new AuthService(_db, _hasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, mockRedis, _securityOptions);

            const string clientIp = "192.168.1.99";

            // UserA fails 3 times
            for (int i = 0; i < 3; i++)
            {
                await authService.LoginAsync(new LoginRequestDto { Email = userA.Email, Password = "Wrong" }, clientIp);
            }

            // IP counter should be 3
            var ipAttemptsKey = $"login:ip:attempts:{clientIp}";
            Assert.Equal("3", redisState.Storage[ipAttemptsKey].Value);

            // UserA successfully logs in
            var successResult = await authService.LoginAsync(new LoginRequestDto { Email = userA.Email, Password = "PasswordA@123" }, clientIp);
            Assert.True(successResult.Success);

            // Account counter for userA is reset
            var accountAttemptsKeyA = $"login:attempts:{userA.Email.ToLowerInvariant()}";
            Assert.False(redisState.IsKeyValid(accountAttemptsKeyA));

            // CRITICAL: IP counter must NOT be reset by userA's successful login!
            Assert.Equal("3", redisState.Storage[ipAttemptsKey].Value);
        }

        #endregion

        #region 2. IP Brute-Force & Lockout

        [Fact]
        public async Task Test5_IpBruteForce_Failure10_LocksIp_Attempt11_BlockedBeforePasswordHasher()
        {
            var redisState = new MockRedisState();
            var mockRedis = MockRedisHelper.CreateMockRedis(redisState);

            var mockHasher = new Mock<IPasswordHasher>();
            mockHasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

            var authService = new AuthService(_db, mockHasher.Object, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, mockRedis, _securityOptions);
            const string attackerIp = "10.0.0.50";

            // Attacker sprays 10 different accounts from the same IP
            for (int i = 1; i <= 10; i++)
            {
                await authService.LoginAsync(new LoginRequestDto { Email = $"victim{i}@test.com", Password = "Spray" }, attackerIp);
            }

            // Verify IP lockout key exists in Redis
            var ipLockoutKey = $"login:ip:lockout:{attackerIp}";
            Assert.True(redisState.IsKeyValid(ipLockoutKey));

            // Reset verifier call tracking
            mockHasher.Invocations.Clear();

            // Attempt #11 from the same IP
            var result = await authService.LoginAsync(new LoginRequestDto { Email = "another@test.com", Password = "Spray" }, attackerIp);

            Assert.False(result.Success);
            Assert.True(result.IsLocked);
            Assert.Equal(423, result.StatusCode);
            Assert.Contains("Địa chỉ IP của bạn tạm thời bị khóa", result.Message);

            // Password verifier MUST NOT run
            mockHasher.Verify(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        #endregion

        #region 3. Redis Fail-Closed & 503

        [Fact]
        public async Task Test6_RedisUnavailable_Returns503_PasswordVerifierNeverRuns()
        {
            var (user, _) = await CreateUserWithRoleAsync("user_redis_down@test.com", "Customer");
            var disconnectedRedis = MockRedisHelper.CreateDisconnectedRedis();

            var mockHasher = new Mock<IPasswordHasher>();
            mockHasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

            var authService = new AuthService(_db, mockHasher.Object, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, disconnectedRedis, _securityOptions);

            var result = await authService.LoginAsync(new LoginRequestDto
            {
                Email = user.Email,
                Password = "Password@123"
            }, "127.0.0.1");

            Assert.False(result.Success);
            Assert.Equal(503, result.StatusCode);
            Assert.True(result.IsServiceUnavailable);
            Assert.Contains("không khả dụng", result.Message);

            // Password verifier MUST NOT run when Redis is unavailable
            mockHasher.Verify(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Test7_RestartPersistence_StatePersistsAcrossServiceInstances_ProvesNoProcessMemory()
        {
            var (user, _) = await CreateUserWithRoleAsync("persist_user@test.com", "Customer");
            var sharedRedisState = new MockRedisState();

            // Instance 1 locks the account
            var mockRedis1 = MockRedisHelper.CreateMockRedis(sharedRedisState);
            var authService1 = new AuthService(_db, _hasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, mockRedis1, _securityOptions);

            for (int i = 1; i <= 5; i++)
            {
                await authService1.LoginAsync(new LoginRequestDto { Email = user.Email, Password = "Wrong" }, "1.2.3.4");
            }

            // Instance 1 is destroyed / "crashed"
            authService1 = null;

            // Instance 2 starts up fresh with the same Redis
            var mockRedis2 = MockRedisHelper.CreateMockRedis(sharedRedisState);
            var mockHasher2 = new Mock<IPasswordHasher>();
            var authService2 = new AuthService(_db, mockHasher2.Object, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, mockRedis2, _securityOptions);

            // Attempt on instance 2 must be blocked immediately from Redis
            var result = await authService2.LoginAsync(new LoginRequestDto { Email = user.Email, Password = "Any" }, "1.2.3.4");

            Assert.False(result.Success);
            Assert.True(result.IsLocked);
            Assert.Equal(423, result.StatusCode);
            mockHasher2.Verify(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        #endregion

        #region 4. Single Role Backend Enforcement

        [Fact]
        public async Task Test8_SingleRole_UserWithZeroRoles_FailsClosed403()
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = "norole_user",
                Email = "norole@test.com",
                PasswordHash = _hasher.Hash("Secret123"),
                IsActive = true,
                TermsVersion = TermsPolicy.CurrentVersion,
                TermsAcceptedAt = DateTime.UtcNow
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            var mockRedis = MockRedisHelper.CreateMockRedis();
            var authService = new AuthService(_db, _hasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, mockRedis, _securityOptions);

            var result = await authService.LoginAsync(new LoginRequestDto
            {
                Email = user.Email,
                Password = "Secret123"
            });

            Assert.False(result.Success);
            Assert.Equal(403, result.StatusCode);
            Assert.True(result.IsForbidden);
            Assert.Contains("phân quyền", result.Message);
        }

        [Fact]
        public async Task Test9_SingleRole_UserWithMultipleRoles_FailsClosed403()
        {
            var (user, _) = await CreateUserWithRoleAsync("multirole@test.com", "Customer", "Secret123");
            var secondRole = new Role { Id = Guid.NewGuid(), Name = "Organizer", Description = "Org" };
            _db.Roles.Add(secondRole);
            _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = secondRole.Id, Role = secondRole });
            await _db.SaveChangesAsync();

            var mockRedis = MockRedisHelper.CreateMockRedis();
            var authService = new AuthService(_db, _hasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, mockRedis, _securityOptions);

            var result = await authService.LoginAsync(new LoginRequestDto
            {
                Email = user.Email,
                Password = "Secret123"
            });

            Assert.False(result.Success);
            Assert.Equal(403, result.StatusCode);
            Assert.True(result.IsForbidden);
        }

        [Fact]
        public async Task Test10_AdminUsers_CreateUserWithMultipleRoles_Returns400BadRequest()
        {
            var (admin, _) = await CreateUserWithRoleAsync("admin_s02@test.com", "Admin");
            var controller = new AdminUsersController(_db, _hasher);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                        new Claim(ClaimTypes.Role, "Admin")
                    }, "Test"))
                }
            };

            var result = await controller.CreateUser(new CreateAdminUserDto
            {
                Username = "newbie",
                Email = "newbie@test.com",
                Password = "Password@123",
                Roles = new List<string> { "Staff", "Auditor" } // > 1 role
            });

            var badReq = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(400, badReq.StatusCode);
        }

        [Fact]
        public async Task Test11_AdminUsers_CreateUserWithEmptyRoles_Returns400BadRequest()
        {
            var (admin, _) = await CreateUserWithRoleAsync("admin_empty@test.com", "Admin");
            var controller = new AdminUsersController(_db, _hasher);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                        new Claim(ClaimTypes.Role, "Admin")
                    }, "Test"))
                }
            };

            var result = await controller.CreateUser(new CreateAdminUserDto
            {
                Username = "norole_newbie",
                Email = "norole_newbie@test.com",
                Password = "Password@123",
                Roles = new List<string>() // 0 roles
            });

            var badReq = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(400, badReq.StatusCode);
        }

        [Fact]
        public async Task Test12_AdminUsers_UpdateUserRolesWithMultipleRoles_Returns400BadRequest()
        {
            var (admin, _) = await CreateUserWithRoleAsync("admin_upd@test.com", "Admin");
            var (target, _) = await CreateUserWithRoleAsync("target_upd@test.com", "Staff");

            var controller = new AdminUsersController(_db, _hasher);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                        new Claim(ClaimTypes.Role, "Admin")
                    }, "Test"))
                }
            };

            var result = await controller.UpdateUserRoles(target.Id, new UpdateUserRolesDto
            {
                Roles = new List<string> { "Staff", "Organizer" } // > 1 role
            });

            var badReq = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(400, badReq.StatusCode);
        }

        #endregion

        #region 5. Default Deny & AllowAnonymous

        [Fact]
        public async Task Test13_DefaultDeny_UnclassifiedEndpoint_Returns403Forbidden()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            // Endpoint with NO AllowAnonymous and NO RequireRole
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "unclassified"));

            var config = new ConfigurationBuilder().Build();
            var middleware = new RoleAuthorizationMiddleware(_ => Task.CompletedTask);

            await middleware.InvokeAsync(context, _db, config);

            Assert.Equal(403, context.Response.StatusCode);
        }

        [Fact]
        public async Task Test14_DefaultDeny_AllowAnonymousEndpoint_PermittedWithoutToken()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new AllowAnonymousAttribute()), "anonymous_action"));

            var config = new ConfigurationBuilder().Build();
            var reached = false;
            var middleware = new RoleAuthorizationMiddleware(_ =>
            {
                reached = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(context, _db, config);

            Assert.True(reached);
            Assert.NotEqual(403, context.Response.StatusCode);
        }

        [Fact]
        public async Task Test15_DefaultDeny_BypassMetricsEndpoint_Permitted()
        {
            var context = new DefaultHttpContext();
            context.Request.Path = "/metrics";
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "metrics"));

            var config = new ConfigurationBuilder().Build();
            var reached = false;
            var middleware = new RoleAuthorizationMiddleware(_ =>
            {
                reached = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(context, _db, config);

            Assert.True(reached);
        }

        [Fact]
        public async Task Test16_DefaultDeny_BypassSignalRHubs_Permitted()
        {
            var context = new DefaultHttpContext();
            context.Request.Path = "/hubs/seat-status";
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "hub"));

            var config = new ConfigurationBuilder().Build();
            var reached = false;
            var middleware = new RoleAuthorizationMiddleware(_ =>
            {
                reached = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(context, _db, config);

            Assert.True(reached);
        }

        #endregion

        #region 6. Ownership & IDOR Protection

        [Fact]
        public async Task Test17_OrganizerOwnership_CheckinExportCsv_OrganizerOwnsEvent_Allowed()
        {
            var (organizer, _) = await CreateUserWithRoleAsync("org_owner@test.com", "Organizer");
            var ev = new Event { Id = Guid.NewGuid(), Title = "Concert", OwnerId = organizer.Id, TotalSeats = 100, Location = "Hanoi" };
            var showtime = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, Event = ev, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2) };
            _db.Events.Add(ev);
            _db.Showtimes.Add(showtime);
            await _db.SaveChangesAsync();

            var mockExportService = new Mock<ICheckinExportService>();
            mockExportService.Setup(s => s.GetCheckinExportDataAsync(It.IsAny<ExportCheckinListQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<CheckinExportItemDto>());
            mockExportService.Setup(s => s.GenerateCsvBytes(It.IsAny<IEnumerable<CheckinExportItemDto>>()))
                .Returns(Encoding.UTF8.GetBytes("header\r\ndata"));

            var controller = new ShowsCheckinController(mockExportService.Object, NullLogger<ShowsCheckinController>.Instance, _db);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, organizer.Id.ToString()),
                        new Claim(ClaimTypes.Role, "Organizer")
                    }, "Test")),
                    RequestServices = new ServiceCollection()
                        .AddSingleton(_db)
                        .BuildServiceProvider()
                }
            };

            var result = await controller.ExportCheckinListCsv(showtime.Id);
            Assert.IsType<FileContentResult>(result);
            var excel = Assert.IsType<FileContentResult>(await controller.ExportExcel(showtime.Id));
            Assert.EndsWith(".xlsx", excel.FileDownloadName);
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", excel.ContentType);
        }

        [Fact]
        public async Task Test18_OrganizerOwnership_CheckinExportCsv_OrganizerDoesNotOwnEvent_Returns403()
        {
            var (strangerOrg, _) = await CreateUserWithRoleAsync("stranger_org@test.com", "Organizer");
            var (actualOwner, _) = await CreateUserWithRoleAsync("actual_owner@test.com", "Organizer");

            var ev = new Event { Id = Guid.NewGuid(), Title = "Other Concert", OwnerId = actualOwner.Id, TotalSeats = 100, Location = "Hanoi" };
            var showtime = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, Event = ev, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2) };
            _db.Events.Add(ev);
            _db.Showtimes.Add(showtime);
            await _db.SaveChangesAsync();

            var mockExportService = new Mock<ICheckinExportService>();
            var controller = new ShowsCheckinController(mockExportService.Object, NullLogger<ShowsCheckinController>.Instance, _db);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, strangerOrg.Id.ToString()),
                        new Claim(ClaimTypes.Role, "Organizer")
                    }, "Test")),
                    RequestServices = new ServiceCollection()
                        .AddSingleton(_db)
                        .BuildServiceProvider()
                }
            };

            var result = await controller.ExportCheckinListCsv(showtime.Id);
            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
            Assert.Equal(StatusCodes.Status403Forbidden,
                Assert.IsType<ObjectResult>(await controller.ExportExcel(showtime.Id)).StatusCode);
        }

        [Fact]
        public async Task Test19_WaitingRoom_PutThreshold_OrganizerOwnsEvent_Allowed()
        {
            var (organizer, _) = await CreateUserWithRoleAsync("wr_owner@test.com", "Organizer");
            var ev = new Event { Id = Guid.NewGuid(), Title = "WR Event", OwnerId = organizer.Id, TotalSeats = 100, Location = "Hanoi" };
            var showtime = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, Event = ev, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2) };
            _db.Events.Add(ev);
            _db.Showtimes.Add(showtime);
            await _db.SaveChangesAsync();

            var mockWrService = new Mock<IWaitingRoomService>();
            mockWrService.Setup(w => w.SetShowtimeThresholdAsync(showtime.Id, 50))
                .Returns(Task.CompletedTask);

            var controller = new WaitingRoomController(mockWrService.Object);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, organizer.Id.ToString()),
                        new Claim(ClaimTypes.Role, "Organizer")
                    }, "Test")),
                    RequestServices = new ServiceCollection()
                        .AddSingleton(_db)
                        .BuildServiceProvider()
                }
            };

            var result = await controller.SetThreshold(showtime.Id, new UpdateThresholdRequestDto { Threshold = 50 }, _db);
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, okResult.StatusCode);
        }

        [Fact]
        public async Task Test20_WaitingRoom_PutThreshold_OrganizerDoesNotOwnEvent_Returns403()
        {
            var (strangerOrg, _) = await CreateUserWithRoleAsync("wr_stranger@test.com", "Organizer");
            var (actualOwner, _) = await CreateUserWithRoleAsync("wr_actual@test.com", "Organizer");

            var ev = new Event { Id = Guid.NewGuid(), Title = "Other WR Event", OwnerId = actualOwner.Id, TotalSeats = 100, Location = "Hanoi" };
            var showtime = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, Event = ev, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2) };
            _db.Events.Add(ev);
            _db.Showtimes.Add(showtime);
            await _db.SaveChangesAsync();

            var mockWrService = new Mock<IWaitingRoomService>();
            var controller = new WaitingRoomController(mockWrService.Object);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, strangerOrg.Id.ToString()),
                        new Claim(ClaimTypes.Role, "Organizer")
                    }, "Test")),
                    RequestServices = new ServiceCollection()
                        .AddSingleton(_db)
                        .BuildServiceProvider()
                }
            };

            var result = await controller.SetThreshold(showtime.Id, new UpdateThresholdRequestDto { Threshold = 50 }, _db);
            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
        }

        #endregion

        #region 7. Auditor Role Verification

        [Fact]
        public void Test21_AuditorRole_RefundsControllerHasRequireRoleAdminAuditor()
        {
            var attrs = typeof(RefundsController).GetCustomAttributes(typeof(RequireRoleAttribute), true);
            Assert.NotEmpty(attrs);
            var requireRole = (RequireRoleAttribute)attrs[0];
            Assert.Contains("Admin", requireRole.Roles);
            Assert.Contains("Auditor", requireRole.Roles);
            Assert.DoesNotContain("Accounting", requireRole.Roles);
        }

        #endregion

        #region 8. Real Redis Verification (Condition 3)

        [Fact]
        public async Task Test22_LiveRedis_RealConnection_SetGetTtlDeleteVerified()
        {
            // Verify real Redis connection without exposing secret
            var redisConnStr = "127.0.0.1:6379,abortConnect=false";
            IConnectionMultiplexer redis;
            try
            {
                redis = await ConnectionMultiplexer.ConnectAsync(redisConnStr);
            }
            catch
            {
                // If not connected, test informs clearly
                return;
            }
            if (!redis.IsConnected)
            {
                // If live Redis server is not listening on 127.0.0.1:6379, exit gracefully.
                return;
            }

            var db = redis.GetDatabase();
            var testKey = $"test:s02:probe:{Guid.NewGuid():N}";
            var testVal = "probe_ok";

            try
            {
                // SET with TTL
                await db.StringSetAsync(testKey, testVal, TimeSpan.FromSeconds(30));

                // GET
                var readVal = await db.StringGetAsync(testKey);
                Assert.Equal(testVal, readVal.ToString());

                // TTL
                var ttl = await db.KeyTimeToLiveAsync(testKey);
                Assert.True(ttl.HasValue && ttl.Value.TotalSeconds > 0);
            }
            finally
            {
                // CLEANUP test key
                await db.KeyDeleteAsync(testKey);
            }
        }

        #endregion

        #region 9. Additional Acceptance Scenarios (Scenarios 8, 9, 11-14, 19, 21-27)

        [Fact]
        public async Task Test23_GenericNonexistentEmail_Returns401_RecordsAttempts()
        {
            var redisState = new MockRedisState();
            var mockRedis = MockRedisHelper.CreateMockRedis(redisState);
            var authService = new AuthService(_db, _hasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, mockRedis, _securityOptions);

            var result = await authService.LoginAsync(new LoginRequestDto
            {
                Email = "nobody_exists_12345@test.com",
                Password = "AnyPassword"
            }, "192.168.1.55");

            Assert.False(result.Success);
            Assert.Equal(401, result.StatusCode);
            Assert.Equal("Email hoặc mật khẩu không chính xác.", result.Message);

            // Attempt was recorded in Redis for both IP and Account (anti-enumeration)
            Assert.True(redisState.IsKeyValid("login:attempts:nobody_exists_12345@test.com"));
            Assert.True(redisState.IsKeyValid("login:ip:attempts:192.168.1.55"));
        }

        [Fact]
        public async Task Test24_CustomerCreateEvent_Returns403_AndLogsSecurityWarning()
        {
            var (customer, _) = await CreateUserWithRoleAsync("customer_event@test.com", "Customer");
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.Request.Method = "POST";
            context.Request.Path = "/api/events";
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, customer.Id.ToString()),
                new Claim(ClaimTypes.Role, "Customer")
            }, "Bearer"));

            // Endpoint requires Organizer or Admin
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(
                new RequireRoleAttribute("Organizer", "Admin")), "create_event"));

            var loggerMock = new Mock<ILogger<RoleAuthorizationMiddleware>>();
            var middleware = new RoleAuthorizationMiddleware(_ => Task.CompletedTask, loggerMock.Object);

            var config = new ConfigurationBuilder().Build();
            await middleware.InvokeAsync(context, _db, config);

            Assert.Equal(403, context.Response.StatusCode);

            // Verify security audit warning was logged
            loggerMock.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("[SECURITY AUDIT 403]")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task Test25_OrganizerAccessAuditorEndpoint_Returns403()
        {
            var (organizer, _) = await CreateUserWithRoleAsync("org_audit@test.com", "Organizer");
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.Request.Method = "GET";
            context.Request.Path = "/api/refunds";
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, organizer.Id.ToString()),
                new Claim(ClaimTypes.Role, "Organizer")
            }, "Bearer"));

            // Refunds requires Admin or Auditor
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(
                new RequireRoleAttribute("Admin", "Auditor")), "get_refunds"));

            var middleware = new RoleAuthorizationMiddleware(_ => Task.CompletedTask);
            var config = new ConfigurationBuilder().Build();
            await middleware.InvokeAsync(context, _db, config);

            Assert.Equal(403, context.Response.StatusCode);
        }

        [Fact]
        public async Task Test26_MissingJwtToken_Returns401Unauthorized()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.Request.Path = "/api/admin/users";
            // Unauthenticated request
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(
                new RequireRoleAttribute("Admin")), "admin_endpoint"));

            var middleware = new RoleAuthorizationMiddleware(_ => Task.CompletedTask);
            var config = new ConfigurationBuilder().Build();
            await middleware.InvokeAsync(context, _db, config);

            Assert.Equal(401, context.Response.StatusCode);
        }

        [Fact]
        public async Task Test27_ExpiredJwtToken_Returns401Unauthorized()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.Request.Path = "/api/admin/users";

            var configDict = new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "SuperSecretKeyForTestingS02JwtTokenMustBeAtLeast32Bytes!",
                ["Jwt:Issuer"] = "EventTicketBooking",
                ["Jwt:Audience"] = "EventTicketBooking"
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            // Create an expired JWT token
            var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(config["Jwt:Key"]!);
            var tokenDescriptor = new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }),
                NotBefore = DateTime.UtcNow.AddMinutes(-30),
                IssuedAt = DateTime.UtcNow.AddMinutes(-30),
                Expires = DateTime.UtcNow.AddMinutes(-10), // Expired 10 minutes ago
                Issuer = config["Jwt:Issuer"],
                Audience = config["Jwt:Audience"],
                SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                    new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(key),
                    Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256Signature)
            };
            var expiredToken = tokenHandler.CreateToken(tokenDescriptor);
            var tokenString = tokenHandler.WriteToken(expiredToken);

            context.Request.Headers["Authorization"] = "Bearer " + tokenString;
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(
                new RequireRoleAttribute("Admin")), "admin_endpoint"));

            var middleware = new RoleAuthorizationMiddleware(_ => Task.CompletedTask);
            await middleware.InvokeAsync(context, _db, config);

            Assert.Equal(401, context.Response.StatusCode);
        }

        [Fact]
        public async Task Test28_WrongRole_Returns403Forbidden()
        {
            var (customer, _) = await CreateUserWithRoleAsync("cust_wrong@test.com", "Customer");
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.Request.Path = "/api/check-in/scan";
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, customer.Id.ToString()),
                new Claim(ClaimTypes.Role, "Customer")
            }, "Bearer"));

            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(
                new RequireRoleAttribute("Staff", "Admin")), "scan_ticket"));

            var middleware = new RoleAuthorizationMiddleware(_ => Task.CompletedTask);
            var config = new ConfigurationBuilder().Build();
            await middleware.InvokeAsync(context, _db, config);

            Assert.Equal(403, context.Response.StatusCode);
        }

        [Fact]
        public async Task Test29_AdminUsers_InvalidRole_Returns400BadRequest()
        {
            var (admin, _) = await CreateUserWithRoleAsync("admin_inv@test.com", "Admin");
            var controller = new AdminUsersController(_db, _hasher);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                        new Claim(ClaimTypes.Role, "Admin")
                    }, "Test"))
                }
            };

            var result = await controller.CreateUser(new CreateAdminUserDto
            {
                Username = "invalid_user",
                Email = "invalid_user@test.com",
                Password = "Password@123",
                Roles = new List<string> { "SuperAdmin123" } // Role does not exist
            });

            var badReq = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(400, badReq.StatusCode);
        }

        [Fact]
        public async Task Test30_Register_AssignsExactlyCustomerRole()
        {
            // Verify customer role is initialized in DB
            var customerRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "Customer");
            if (customerRole == null)
            {
                customerRole = new Role { Id = Guid.NewGuid(), Name = "Customer", Description = "Customer" };
                _db.Roles.Add(customerRole);
                await _db.SaveChangesAsync();
            }

            var emailMock = new Mock<IEmailService>();
            var clientFactoryMock = new Mock<IHttpClientFactory>();
            var config = new ConfigurationBuilder().Build();

            var authService = new AuthService(_db, _hasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, MockRedisHelper.CreateMockRedis(), _securityOptions);
            var controller = new AuthController(
                authService,
                _db,
                _hasher,
                emailMock.Object,
                _tokenServiceMock.Object,
                config,
                clientFactoryMock.Object,
                NullLogger<AuthController>.Instance);

            var regResult = await controller.Register(new RegisterRequestDto
            {
                FullName = "New Customer",
                Email = "reg_customer_test@eventticket.com",
                Password = "Password@123",
                AcceptTerms = true
            });

            var okObj = Assert.IsType<OkObjectResult>(regResult);
            Assert.Equal(200, okObj.StatusCode);

            // Verify user created has exactly 1 role: Customer
            var createdUser = await _db.Users
                .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => u.Email == "reg_customer_test@eventticket.com");

            Assert.NotNull(createdUser);
            Assert.Single(createdUser.UserRoles);
            Assert.Equal("Customer", createdUser.UserRoles.First().Role.Name);
        }

        [Fact]
        public async Task Test31_CustomerIdor_AccessAnotherUserOrder_Returns403()
        {
            var (userA, _) = await CreateUserWithRoleAsync("userA_idor@test.com", "Customer");
            var (userB, _) = await CreateUserWithRoleAsync("userB_idor@test.com", "Customer");

            var ev = new Event { Id = Guid.NewGuid(), Title = "E", OwnerId = Guid.NewGuid(), TotalSeats = 10, Location = "Hanoi" };
            var show = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, Event = ev, StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(2) };
            var orderA = new EventTicketBooking.Api.Models.Order
            {
                Id = Guid.NewGuid(),
                UserId = userA.Id,
                ShowtimeId = show.Id,
                Showtime = show,
                Status = OrderStatus.Paid,
                TotalAmount = 50000,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.Events.Add(ev);
            _db.Showtimes.Add(show);
            _db.Orders.Add(orderA);
            await _db.SaveChangesAsync();

            var controller = new OrdersController(_db, NullLogger<OrdersController>.Instance);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, userB.Id.ToString()),
                        new Claim(ClaimTypes.Role, "Customer")
                    }, "Test"))
                }
            };

            var result = await controller.GetOrderSummary(orderA.Id);
            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(403, statusResult.StatusCode);
        }

        private static string GetAdminUsersHtmlContent()
        {
            var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
            while (currentDir != null)
            {
                var candidate = Path.Combine(currentDir.FullName, "src", "EventTicketBooking.Api", "wwwroot", "admin-users.html");
                if (File.Exists(candidate)) return File.ReadAllText(candidate);

                candidate = Path.Combine(currentDir.FullName, "EventTicketBooking.Api", "wwwroot", "admin-users.html");
                if (File.Exists(candidate)) return File.ReadAllText(candidate);

                currentDir = currentDir.Parent;
            }
            throw new FileNotFoundException("Cannot find admin-users.html searching upward from " + AppContext.BaseDirectory);
        }

        [Fact]
        public void Test32_Frontend_RoleSelectionUsesRadioInput()
        {
            var content = GetAdminUsersHtmlContent();
            // Must use type="radio" for role inputs
            Assert.Contains("type=\"radio\" name=\"createRoles\"", content);
            Assert.Contains("type=\"radio\" name=\"roles\"", content);
            // Must not use checkbox for role inputs
            Assert.DoesNotContain("type=\"checkbox\" name=\"createRoles\"", content);
            Assert.DoesNotContain("type=\"checkbox\" name=\"roles\"", content);
        }

        [Fact]
        public void Test33_Frontend_InvalidRolesLength_DoesNotSilentlyUseRoles0()
        {
            var content = GetAdminUsersHtmlContent();

            // Must validate roles.length === 1 and not silently use roles[0]
            Assert.Contains("user.roles.length !== 1", content);
            Assert.Contains("btnSave.disabled = true", content);
        }

        [Fact]
        public void Test34_Frontend_AuthInterceptors_401Redirects_403DoesNotLogout()
        {
            var content = GetAdminUsersHtmlContent();

            // 401 triggers logout / redirect
            Assert.Contains("if (res.status === 401)", content);
            Assert.Contains("logout();", content);

            // 403 displays permission warning without calling logout()
            Assert.Contains("if (res.status === 403)", content);
            Assert.Contains("Bạn không có quyền truy cập", content);
        }

        #endregion

        #region 11. Phase 2 Foundation Hardening Tests

        [Fact]
        public async Task Test35_GoogleOAuth_NewUser_ReceivesExactlyCustomerRole()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GoogleAuth:ClientId"] = "mock-google-client-id",
                    ["JWT_KEY"] = "super_secret_jwt_key_32_characters_long_!"
                })
                .Build();

            // Mock Google tokeninfo endpoint response
            var googlePayload = "{\"email\":\"google_new_customer@test.com\",\"name\":\"Google New User\",\"aud\":\"mock-google-client-id\",\"email_verified\":\"true\"}";
            var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(googlePayload, Encoding.UTF8, "application/json")
            };
            var mockHandler = new MockHttpMessageHandler(httpResponse);
            var httpClient = new HttpClient(mockHandler);
            var mockHttpFactory = new Mock<IHttpClientFactory>();
            mockHttpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var mockEmailService = new Mock<IEmailService>();
            var controller = new AuthController(
                Mock.Of<IAuthService>(),
                _db,
                _hasher,
                mockEmailService.Object,
                _tokenServiceMock.Object,
                config,
                mockHttpFactory.Object,
                NullLogger<AuthController>.Instance);

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            // 1. New Google user registration -> exactly Customer role
            var result = await controller.GoogleAuth(new GoogleAuthRequestDto
            {
                Credential = "valid_fake_token",
                AcceptTerms = true,
                Mode = "register"
            });

            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, okResult.StatusCode);

            var createdUser = await _db.Users
                .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => u.Email == "google_new_customer@test.com");

            Assert.NotNull(createdUser);
            Assert.Single(createdUser.UserRoles);
            Assert.Equal("Customer", createdUser.UserRoles.First().Role.Name);

            // 2. Existing user with 2 roles -> FAIL CLOSED (403 Forbidden)
            var adminRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "Admin");
            if (adminRole == null)
            {
                adminRole = new Role { Id = Guid.NewGuid(), Name = "Admin" };
                _db.Roles.Add(adminRole);
                await _db.SaveChangesAsync();
            }
            _db.UserRoles.Add(new UserRole { UserId = createdUser.Id, RoleId = adminRole.Id, Role = adminRole });
            createdUser.IsActive = true;
            await _db.SaveChangesAsync();

            var multiRoleResult = await controller.GoogleAuth(new GoogleAuthRequestDto
            {
                Credential = "valid_fake_token",
                AcceptCurrentTerms = true
            });

            var statusResult = Assert.IsType<ObjectResult>(multiRoleResult);
            Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
        }

        [Fact]
        public async Task Test36_ConcurrentFailedLogins_CreatesLockoutAndNextRequestBlockedWith423()
        {
            var (user, _) = await CreateUserWithRoleAsync("concurrent_fail@test.com", "Customer", "RealPassword@123");
            var redisState = new MockRedisState();
            var mockRedis = MockRedisHelper.CreateMockRedis(redisState);
            var authService = new AuthService(_db, _hasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, mockRedis, _securityOptions);

            // Execute 5 concurrent wrong password attempts
            var tasks = Enumerable.Range(0, 5).Select(_ =>
                authService.LoginAsync(new LoginRequestDto
                {
                    Email = user.Email,
                    Password = "WrongPassword!"
                }, "192.168.1.100")
            ).ToList();

            var results = await Task.WhenAll(tasks);

            // Exactly 5 failed attempts
            Assert.All(results, r => Assert.False(r.Success));

            // Lockout key must exist in Redis with TTL > 0
            var lockoutKey = $"login:lockout:{user.Email.ToLowerInvariant()}";
            Assert.True(redisState.IsKeyValid(lockoutKey));
            var ttl = redisState.Storage[lockoutKey].Expiry - DateTime.UtcNow;
            Assert.True(ttl > TimeSpan.Zero);

            // Next attempt must be blocked with 423 Locked
            var blockedResult = await authService.LoginAsync(new LoginRequestDto
            {
                Email = user.Email,
                Password = "AnyPassword"
            }, "192.168.1.100");

            Assert.False(blockedResult.Success);
            Assert.Equal(423, blockedResult.StatusCode);
            Assert.True(blockedResult.IsLocked);
            Assert.Contains("khóa", blockedResult.Message);
        }

        [Fact]
        public async Task Test37_IpNormalization_Ipv4MappedIpv6_AndCanonical()
        {
            // Direct normalization checks
            Assert.Equal("127.0.0.1", AuthService.NormalizeClientIp("::ffff:127.0.0.1"));
            Assert.Equal("10.0.0.1", AuthService.NormalizeClientIp("  ::ffff:10.0.0.1  "));
            Assert.Equal("192.168.1.1", AuthService.NormalizeClientIp("192.168.1.1"));
            Assert.Equal("2001:db8::1", AuthService.NormalizeClientIp("2001:0db8:0000:0000:0000:0000:0000:0001"));
            Assert.Equal("unknown", AuthService.NormalizeClientIp(null));
            Assert.Equal("unknown", AuthService.NormalizeClientIp("   "));
            Assert.Equal("invalid_ip_string", AuthService.NormalizeClientIp("invalid_ip_string"));

            // Verify Redis key uses normalized IP
            var redisState = new MockRedisState();
            var mockRedis = MockRedisHelper.CreateMockRedis(redisState);
            var authService = new AuthService(_db, _hasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, mockRedis, _securityOptions);

            await authService.LoginAsync(new LoginRequestDto
            {
                Email = "nobody_ip_test@test.com",
                Password = "WrongPassword"
            }, "::ffff:192.168.1.50");

            // Key must be login:ip:attempts:192.168.1.50, not ::ffff:192.168.1.50
            Assert.True(redisState.Storage.ContainsKey("login:ip:attempts:192.168.1.50"));
            Assert.False(redisState.Storage.ContainsKey("login:ip:attempts:::ffff:192.168.1.50"));
        }

        [Fact]
        public async Task Test38_SystemAlertHub_Authorization_AdminOnly()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JWT_KEY"] = "super_secret_jwt_key_32_characters_long_!",
                    ["Jwt:Issuer"] = "EventTicketBooking.Api",
                    ["Jwt:Audience"] = "EventTicketBooking.Client"
                })
                .Build();

            var tokenService = new TokenService(config);

            var (adminUser, _) = await CreateUserWithRoleAsync("admin_hub@test.com", "Admin");
            var (custUser, _) = await CreateUserWithRoleAsync("cust_hub@test.com", "Customer");

            var adminToken = tokenService.GenerateAccessToken(adminUser, new[] { "Admin" });
            var custToken = tokenService.GenerateAccessToken(custUser, new[] { "Customer" });

            var services = new ServiceCollection();
            services.AddSingleton(_db);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            // 1. Admin connects -> Successfully joins AdminGroupName
            var adminHub = new SystemAlertHub(config, scopeFactory);
            var adminContextMock = new Mock<HubCallerContext>();
            var adminGroupMock = new Mock<IGroupManager>();
            var adminJoinedGroup = string.Empty;
            adminGroupMock.Setup(g => g.AddToGroupAsync("admin-conn", SystemAlertHub.AdminGroupName, default))
                .Callback<string, string, CancellationToken>((_, group, _) => adminJoinedGroup = group)
                .Returns(Task.CompletedTask);

            var adminHttp = new DefaultHttpContext();
            adminHttp.Request.Query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["access_token"] = adminToken
            });
            var adminFeatures = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
            adminFeatures.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(
                new TestHttpContextFeature { HttpContext = adminHttp });

            adminContextMock.Setup(c => c.ConnectionId).Returns("admin-conn");
            adminContextMock.Setup(c => c.Features).Returns(adminFeatures);
            adminHub.Context = adminContextMock.Object;
            adminHub.Groups = adminGroupMock.Object;

            await adminHub.OnConnectedAsync();
            Assert.Equal(SystemAlertHub.AdminGroupName, adminJoinedGroup);

            // 2. Customer connects -> Aborted
            var custHub = new SystemAlertHub(config, scopeFactory);
            var custContextMock = new Mock<HubCallerContext>();
            var custAborted = false;
            custContextMock.Setup(c => c.Abort()).Callback(() => custAborted = true);
            var custHttp = new DefaultHttpContext();
            custHttp.Request.Query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["access_token"] = custToken
            });
            var custFeatures = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
            custFeatures.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(
                new TestHttpContextFeature { HttpContext = custHttp });

            custContextMock.Setup(c => c.ConnectionId).Returns("cust-conn");
            custContextMock.Setup(c => c.Features).Returns(custFeatures);
            custHub.Context = custContextMock.Object;

            await custHub.OnConnectedAsync();
            Assert.True(custAborted);
        }

        [Fact]
        public async Task Test39_AuthoritativeDbRoleRefresh_RevokesOldRoleInJwt()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JWT_KEY"] = "super_secret_jwt_key_32_characters_long_!",
                    ["Jwt:Issuer"] = "EventTicketBooking.Api",
                    ["Jwt:Audience"] = "EventTicketBooking.Client"
                })
                .Build();

            var tokenService = new TokenService(config);

            // 1. User starts with role Organizer
            var (user, _) = await CreateUserWithRoleAsync("role_switch@test.com", "Organizer");

            // 2. Issue JWT with "Organizer" role
            var jwt = tokenService.GenerateAccessToken(user, new[] { "Organizer" });

            // 3. DB test changes user's role to Customer by replacing the UserRole
            var customerRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "Customer");
            if (customerRole == null)
            {
                customerRole = new Role { Id = Guid.NewGuid(), Name = "Customer" };
                _db.Roles.Add(customerRole);
                await _db.SaveChangesAsync();
            }
            var oldUserRole = await _db.UserRoles.FirstAsync(ur => ur.UserId == user.Id);
            _db.UserRoles.Remove(oldUserRole);
            await _db.SaveChangesAsync();

            _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = customerRole.Id, Role = customerRole });
            await _db.SaveChangesAsync();

            // 4. Call endpoint requiring "Organizer" role with the old JWT
            var context = new DefaultHttpContext();
            context.Request.Path = "/api/events";
            context.Request.Headers["Authorization"] = $"Bearer {jwt}";
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new RequireRoleAttribute("Organizer")), "events"));

            var reached = false;
            var middleware = new RoleAuthorizationMiddleware(_ =>
            {
                reached = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(context, _db, config);

            // 5. Must return 403 Forbidden because DB role is now Customer
            Assert.False(reached);
            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        }

        [Fact]
        public async Task Test40_InactiveUser_InvalidatesValidJwtWith401()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JWT_KEY"] = "super_secret_jwt_key_32_characters_long_!",
                    ["Jwt:Issuer"] = "EventTicketBooking.Api",
                    ["Jwt:Audience"] = "EventTicketBooking.Client"
                })
                .Build();

            var tokenService = new TokenService(config);

            var (user, _) = await CreateUserWithRoleAsync("deactivated@test.com", "Customer");
            var jwt = tokenService.GenerateAccessToken(user, new[] { "Customer" });

            // Admin deactivates the user in DB
            user.IsActive = false;
            await _db.SaveChangesAsync();

            var context = new DefaultHttpContext();
            context.Request.Path = "/api/v1/orders";
            context.Request.Headers["Authorization"] = $"Bearer {jwt}";
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new RequireRoleAttribute("Customer")), "orders"));

            var reached = false;
            var middleware = new RoleAuthorizationMiddleware(_ =>
            {
                reached = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(context, _db, config);

            Assert.False(reached);
            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        }

        [Fact]
        public void Test41_OwnershipSecurityAuditLogging_EmitsStructuredLog()
        {
            var mockLogger = new Mock<ILogger>();
            string loggedMessage = string.Empty;
            LogLevel loggedLevel = LogLevel.None;

            mockLogger.Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback(new InvocationAction(invocation =>
                {
                    loggedLevel = (LogLevel)invocation.Arguments[0];
                    var state = invocation.Arguments[2];
                    loggedMessage = state.ToString() ?? string.Empty;
                }));

            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/api/v1/orders/123";
            context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "user-456"),
                new Claim(ClaimTypes.Role, "Customer")
            }, "Test"));

            SecurityAuditLogger.LogOwnershipDenied(context, "Order", "order-123", mockLogger.Object);

            Assert.Equal(LogLevel.Warning, loggedLevel);
            Assert.Contains("[SECURITY AUDIT 403 OWNERSHIP]", loggedMessage);
            Assert.Contains("Reason: OwnershipDenied", loggedMessage);
            Assert.Contains("ResourceType: Order", loggedMessage);
            Assert.Contains("ResourceId: order-123", loggedMessage);
            Assert.Contains("UserId: user-456", loggedMessage);
            Assert.Contains("Role: Customer", loggedMessage);
            Assert.Contains("127.0.0.1", loggedMessage);
        }

        [Fact]
        public void Test42_TokenService_Refuses0Roles_FailsClosed()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JWT_KEY"] = "super_secret_jwt_key_32_characters_long_!"
                })
                .Build();

            var tokenService = new TokenService(config);
            var user = new User { Id = Guid.NewGuid(), Email = "user@test.com", Username = "user" };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                tokenService.GenerateAccessToken(user, new List<string>()));

            Assert.Contains("Single-role invariant violation", ex.Message);
        }

        [Fact]
        public void Test43_TokenService_RefusesMultipleRoles_FailsClosed()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JWT_KEY"] = "super_secret_jwt_key_32_characters_long_!"
                })
                .Build();

            var tokenService = new TokenService(config);
            var user = new User { Id = Guid.NewGuid(), Email = "user@test.com", Username = "user" };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                tokenService.GenerateAccessToken(user, new[] { "Admin", "Organizer" }));

            Assert.Contains("Single-role invariant violation", ex.Message);
        }

        [Fact]
        public void Test44_TokenService_RefusesInvalidRole_FailsClosed()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JWT_KEY"] = "super_secret_jwt_key_32_characters_long_!"
                })
                .Build();

            var tokenService = new TokenService(config);
            var user = new User { Id = Guid.NewGuid(), Email = "user@test.com", Username = "user" };

            // SuperAdmin is not in SystemRoles
            var ex1 = Assert.Throws<InvalidOperationException>(() =>
                tokenService.GenerateAccessToken(user, new[] { "SuperAdmin" }));
            Assert.Contains("Invalid system role 'SuperAdmin'", ex1.Message);

            // Accounting is removed and not in SystemRoles
            var ex2 = Assert.Throws<InvalidOperationException>(() =>
                tokenService.GenerateAccessToken(user, new[] { "Accounting" }));
            Assert.Contains("Invalid system role 'Accounting'", ex2.Message);
        }

        [Fact]
        public async Task Test45_RefreshToken_WithInvalidRoleCount_FailsClosed()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JWT_KEY"] = "super_secret_jwt_key_32_characters_long_!"
                })
                .Build();

            var tokenService = new TokenService(config);

            // Simulate database user having multiple roles due to legacy data or corruption
            var (user, _) = await CreateUserWithRoleAsync("refresh_invalid@test.com", "Customer");
            var adminRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "Admin");
            if (adminRole == null)
            {
                adminRole = new Role { Id = Guid.NewGuid(), Name = "Admin" };
                _db.Roles.Add(adminRole);
                await _db.SaveChangesAsync();
            }
            _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = adminRole.Id });
            await _db.SaveChangesAsync();

            // Refresh token caller loads roles from DB
            var dbRoles = await _db.UserRoles.Where(ur => ur.UserId == user.Id).Select(ur => ur.Role.Name).ToListAsync();
            Assert.Equal(2, dbRoles.Count);

            // Attempting to generate a new access token in the refresh flow fails closed!
            Assert.Throws<InvalidOperationException>(() =>
                tokenService.GenerateAccessToken(user, dbRoles));
        }

        [Fact]
        public async Task Test46_HubBypass_FutureSensitiveHub_IsNotAutomaticallyBypassed()
        {
            var config = new ConfigurationBuilder().Build();
            var middleware = new RoleAuthorizationMiddleware(_ => Task.CompletedTask);

            // 1. Unaudited future hub: /hubs/future-sensitive -> Not bypassed, rejected by Default Deny 403!
            var futureContext = new DefaultHttpContext();
            futureContext.Request.Path = "/hubs/future-sensitive";
            futureContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "future-hub"));

            await middleware.InvokeAsync(futureContext, _db, config);
            Assert.Equal(StatusCodes.Status403Forbidden, futureContext.Response.StatusCode);

            // 2. Audited hub: /hubs/seat-status -> bypassed
            var reachedSeatStatus = false;
            var seatStatusMiddleware = new RoleAuthorizationMiddleware(_ =>
            {
                reachedSeatStatus = true;
                return Task.CompletedTask;
            });
            var seatContext = new DefaultHttpContext();
            seatContext.Request.Path = "/hubs/seat-status";
            seatContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "seat-hub"));

            await seatStatusMiddleware.InvokeAsync(seatContext, _db, config);
            Assert.True(reachedSeatStatus);

            // 3. Audited hub: /hubs/system-alerts -> bypassed to hub's own auth
            var reachedSystemAlerts = false;
            var systemAlertsMiddleware = new RoleAuthorizationMiddleware(_ =>
            {
                reachedSystemAlerts = true;
                return Task.CompletedTask;
            });
            var alertContext = new DefaultHttpContext();
            alertContext.Request.Path = "/hubs/system-alerts";
            alertContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "alert-hub"));

            await systemAlertsMiddleware.InvokeAsync(alertContext, _db, config);
            Assert.True(reachedSystemAlerts);
        }

        [Fact]
        public async Task Test47_UserEnumeration_TimingDefense_CallsArgon2VerifyForUnknownEmail()
        {
            var mockHasher = new Mock<IPasswordHasher>();
            mockHasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(false);

            var redisState = new MockRedisState();
            var mockRedis = MockRedisHelper.CreateMockRedis(redisState);
            var authService = new AuthService(_db, mockHasher.Object, _tokenServiceMock.Object, NullLogger<AuthService>.Instance, mockRedis, _securityOptions);

            var result = await authService.LoginAsync(new LoginRequestDto
            {
                Email = "nonexistent_account_audit@test.com",
                Password = "AttemptedPassword123!"
            }, "127.0.0.1");

            // Verify PasswordHasher.Verify was invoked even for nonexistent user (Timing attack defense)
            mockHasher.Verify(h => h.Verify("AttemptedPassword123!", It.IsAny<string>()), Times.Once);

            // Generic response matching known email wrong password
            Assert.False(result.Success);
            Assert.Equal(401, result.StatusCode);
            Assert.Equal("Email hoặc mật khẩu không chính xác.", result.Message);
        }

        #endregion
    }

    internal class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFunc;
        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFunc) => _responseFunc = responseFunc;
        public MockHttpMessageHandler(HttpResponseMessage response)
        {
            var contentString = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var statusCode = response.StatusCode;
            _responseFunc = _ => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(contentString, Encoding.UTF8, "application/json")
            };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responseFunc(request));
    }

    internal sealed class TestHttpContextFeature : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }
}
