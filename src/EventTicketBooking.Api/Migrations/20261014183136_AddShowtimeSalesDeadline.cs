using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace EventTicketBooking.Api.Migrations;
public partial class AddShowtimeSalesDeadline : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(name: "SalesEndTime", table: "Showtimes", type: "timestamp with time zone", nullable: true);
        migrationBuilder.Sql("UPDATE \"Showtimes\" SET \"SalesEndTime\" = \"StartTime\" WHERE \"SalesEndTime\" IS NULL;");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(name: "SalesEndTime", table: "Showtimes");
}
