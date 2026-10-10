using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketBooking.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOfflineCheckInRecords_S35 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OfflineCheckInRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OfflineScanId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OrderItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShowtimeId = table.Column<Guid>(type: "uuid", nullable: false),
                    GateName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ScannedAtDevice = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAtServer = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    SyncedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsConflict = table.Column<bool>(type: "boolean", nullable: false),
                    ConflictReason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExistingCheckInGate = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExistingCheckInTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfflineCheckInRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OfflineCheckInRecords_Showtimes_ShowtimeId",
                        column: x => x.ShowtimeId,
                        principalTable: "Showtimes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OfflineCheckInRecords_Users_SyncedByUserId",
                        column: x => x.SyncedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OfflineCheckInRecords_order_items_OrderItemId",
                        column: x => x.OrderItemId,
                        principalTable: "order_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OfflineCheckInRecords_OrderItemId",
                table: "OfflineCheckInRecords",
                column: "OrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_OfflineCheckInRecords_ShowtimeId",
                table: "OfflineCheckInRecords",
                column: "ShowtimeId");

            migrationBuilder.CreateIndex(
                name: "IX_OfflineCheckInRecords_SyncedByUserId",
                table: "OfflineCheckInRecords",
                column: "SyncedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OfflineCheckInRecords_TicketCode",
                table: "OfflineCheckInRecords",
                column: "TicketCode");

            migrationBuilder.CreateIndex(
                name: "UX_OfflineCheckInRecords_OfflineScanId",
                table: "OfflineCheckInRecords",
                column: "OfflineScanId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfflineCheckInRecords");
        }
    }
}
