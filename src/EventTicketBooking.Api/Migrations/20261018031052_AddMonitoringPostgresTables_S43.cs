using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketBooking.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoringPostgresTables_S43 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonitoringAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Metric = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    ObservedValue = table.Column<double>(type: "double precision", nullable: false),
                    WindowMinutes = table.Column<int>(type: "integer", nullable: false),
                    FirstTriggeredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastTriggeredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReferenceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Environment = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RequestTelemetries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Route = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<double>(type: "double precision", nullable: false),
                    Environment = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestTelemetries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringAlerts_IsActive",
                table: "MonitoringAlerts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringAlerts_IsActive_Metric",
                table: "MonitoringAlerts",
                columns: new[] { "IsActive", "Metric" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringEvents_OccurredAtUtc",
                table: "MonitoringEvents",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringEvents_OccurredAtUtc_Type",
                table: "MonitoringEvents",
                columns: new[] { "OccurredAtUtc", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_RequestTelemetries_OccurredAtUtc",
                table: "RequestTelemetries",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RequestTelemetries_OccurredAtUtc_StatusCode",
                table: "RequestTelemetries",
                columns: new[] { "OccurredAtUtc", "StatusCode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonitoringAlerts");

            migrationBuilder.DropTable(
                name: "MonitoringEvents");

            migrationBuilder.DropTable(
                name: "RequestTelemetries");
        }
    }
}
