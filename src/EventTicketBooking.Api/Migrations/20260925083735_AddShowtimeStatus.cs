using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketBooking.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddShowtimeStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:showtime_status", "draft,on_sale,closed");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Showtimes",
                type: "showtime_status",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "Showtimes");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:Enum:showtime_status", "draft,on_sale,closed");
        }
    }
}
