using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketBooking.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundRequests_S48 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "refund_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderCode = table.Column<long>(type: "bigint", nullable: false),
                    TransactionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RefundTransactionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "VND"),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    MaxRetries = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextRetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsRetryable = table.Column<bool>(type: "boolean", nullable: true),
                    AccountingNotified = table.Column<bool>(type: "boolean", nullable: false),
                    AccountingNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AccountingNotificationError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refund_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_refund_requests_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_refund_requests_IdempotencyKey_Unique",
                table: "refund_requests",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refund_requests_NextRetryAt",
                table: "refund_requests",
                column: "NextRetryAt");

            migrationBuilder.CreateIndex(
                name: "IX_refund_requests_OrderId",
                table: "refund_requests",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_refund_requests_Status",
                table: "refund_requests",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refund_requests");
        }
    }
}
