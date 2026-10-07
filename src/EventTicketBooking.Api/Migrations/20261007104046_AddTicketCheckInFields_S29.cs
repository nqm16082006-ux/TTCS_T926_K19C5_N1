using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketBooking.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketCheckInFields_S29 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckInGate",
                table: "order_items",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CheckInTime",
                table: "order_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCheckedIn",
                table: "order_items",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckInGate",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "CheckInTime",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "IsCheckedIn",
                table: "order_items");
        }
    }
}
