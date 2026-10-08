using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketBooking.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueActiveSeatHoldConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_seat_holds_SeatId",
                table: "seat_holds");

            migrationBuilder.CreateIndex(
                name: "IX_seat_holds_SeatId_Active",
                table: "seat_holds",
                column: "SeatId",
                unique: true,
                filter: "\"Status\" = 'ACTIVE'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_seat_holds_SeatId_Active",
                table: "seat_holds");

            migrationBuilder.CreateIndex(
                name: "IX_seat_holds_SeatId",
                table: "seat_holds",
                column: "SeatId");
        }
    }
}
