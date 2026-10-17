using System;
using EventTicketBooking.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketBooking.Api.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20261017200000_AddTicketTransfers_S57")]
    public partial class AddTicketTransfers_S57 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowTicketTransfer",
                table: "Events",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "TransferCutoffHours",
                table: "Events",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<Guid>(
                name: "CurrentUserId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TransferCount",
                table: "tickets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastTransferredAt",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ticket_transfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromUserEmail = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    ToUserEmail = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    OldTicketCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NewTicketCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_transfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ticket_transfers_tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ticket_transfers_Users_FromUserId",
                        column: x => x.FromUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ticket_transfers_Users_ToUserId",
                        column: x => x.ToUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_CurrentUserId",
                table: "tickets",
                column: "CurrentUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_tickets_Users_CurrentUserId",
                table: "tickets",
                column: "CurrentUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_transfers_TicketId",
                table: "ticket_transfers",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_transfers_FromUserId",
                table: "ticket_transfers",
                column: "FromUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_transfers_ToUserId",
                table: "ticket_transfers",
                column: "ToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_transfers_OldTicketCode",
                table: "ticket_transfers",
                column: "OldTicketCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_transfers");

            migrationBuilder.DropForeignKey(
                name: "FK_tickets_Users_CurrentUserId",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_tickets_CurrentUserId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "LastTransferredAt",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "TransferCount",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "CurrentUserId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "TransferCutoffHours",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "AllowTicketTransfer",
                table: "Events");
        }
    }
}
