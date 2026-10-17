using System;
using EventTicketBooking.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketBooking.Api.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20261017090000_AddReconciliationSnapshots_S51")]
    public partial class AddReconciliationSnapshots_S51 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reconciliation_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PeriodFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalInternalCount = table.Column<int>(type: "integer", nullable: false),
                    TotalInternalAmount = table.Column<long>(type: "bigint", nullable: false),
                    TotalGatewayCount = table.Column<int>(type: "integer", nullable: false),
                    TotalGatewayAmount = table.Column<long>(type: "bigint", nullable: false),
                    MatchedCount = table.Column<int>(type: "integer", nullable: false),
                    SystemOnlyCount = table.Column<int>(type: "integer", nullable: false),
                    GatewayOnlyCount = table.Column<int>(type: "integer", nullable: false),
                    AmountMismatchCount = table.Column<int>(type: "integer", nullable: false),
                    IneligibleCount = table.Column<int>(type: "integer", nullable: false),
                    RawCsv = table.Column<string>(type: "text", nullable: false),
                    SummaryJson = table.Column<string>(type: "text", nullable: false),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExecutedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliation_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reconciliation_sessions_SessionCode",
                table: "reconciliation_sessions",
                column: "SessionCode");

            migrationBuilder.CreateIndex(
                name: "IX_reconciliation_sessions_ExecutedAt",
                table: "reconciliation_sessions",
                column: "ExecutedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reconciliation_sessions");
        }
    }
}
