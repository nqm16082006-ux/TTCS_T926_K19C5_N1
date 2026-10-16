using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace EventTicketBooking.Api.Migrations
{
    public partial class AddShowtimeName : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "Name", table: "Showtimes",
                type: "character varying(200)", maxLength: 200, nullable: true);
        }
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Name", table: "Showtimes");
        }
    }
}
