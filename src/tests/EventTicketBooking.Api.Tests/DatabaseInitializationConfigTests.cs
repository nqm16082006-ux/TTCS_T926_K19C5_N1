using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Api.Tests
{
    public class DatabaseInitializationConfigTests
    {
        private (IServiceProvider Services, AppDbContext Db) BuildServiceProvider(Dictionary<string, string?> configValues, string dbName)
        {
            var services = new ServiceCollection();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configValues)
                .Build();

            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase(dbName));

            var provider = services.BuildServiceProvider();
            var db = provider.GetRequiredService<AppDbContext>();

            return (provider, db);
        }

        [Fact]
        public async Task DataSeeder_WhenAutoMigrateAndAutoSeedDisabled_DoesNotModifyDatabase()
        {
            // Arrange: Cấu hình AutoMigrate=false và AutoSeed=false (môi trường restore drill)
            var config = new Dictionary<string, string?>
            {
                { "Database:AutoMigrate", "false" },
                { "Database:AutoSeed", "false" }
            };

            var (services, db) = BuildServiceProvider(config, "drill_test_disabled_db_" + Guid.NewGuid().ToString("N"));

            // Act
            await DataSeeder.SeedAsync(services);

            // Assert: Không có role, user hay event nào bị chèn vào database
            Assert.Empty(await db.Roles.ToListAsync());
            Assert.Empty(await db.Users.ToListAsync());
            Assert.Empty(await db.Events.ToListAsync());
        }

        [Fact]
        public async Task DataSeeder_WhenAutoSeedEnabled_SeedsRolesAndUsers()
        {
            // Arrange: Cấu hình AutoMigrate=false nhưng AutoSeed=true
            var config = new Dictionary<string, string?>
            {
                { "Database:AutoMigrate", "false" },
                { "Database:AutoSeed", "true" }
            };

            var (services, db) = BuildServiceProvider(config, "drill_test_enabled_db_" + Guid.NewGuid().ToString("N"));

            // Act
            await DataSeeder.SeedAsync(services);

            // Assert: Roles và Demo Users được khởi tạo
            var roles = await db.Roles.ToListAsync();
            var users = await db.Users.ToListAsync();

            Assert.NotEmpty(roles);
            Assert.Contains(roles, r => r.Name == "Admin");
            Assert.Contains(roles, r => r.Name == "Organizer");

            Assert.NotEmpty(users);
            Assert.Contains(users, u => u.Username == "admin");
            Assert.Contains(users, u => u.Username == "organizer");
        }

        [Fact]
        public async Task DatabaseInitializer_WhenAutoMigrateDisabled_ReturnsImmediately()
        {
            // Arrange: Cấu hình AutoMigrate=false
            var configDict = new Dictionary<string, string?>
            {
                { "Database:AutoMigrate", "false" },
                { "Bootstrap:AdminEmail", "admin@bootstrap.local" },
                { "Bootstrap:AdminPassword", "BootstrapAdminPass123!" }
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configDict)
                .Build();

            var (services, db) = BuildServiceProvider(configDict, "db_init_disabled_" + Guid.NewGuid().ToString("N"));

            // Act: Gọi DatabaseInitializer khi AutoMigrate=false
            await DatabaseInitializer.InitializeAsync(services, configuration);

            // Assert: Không thực hiện migration hay seed bootstrap admin
            Assert.Empty(await db.Roles.ToListAsync());
            Assert.Empty(await db.Users.ToListAsync());
        }
    }
}
