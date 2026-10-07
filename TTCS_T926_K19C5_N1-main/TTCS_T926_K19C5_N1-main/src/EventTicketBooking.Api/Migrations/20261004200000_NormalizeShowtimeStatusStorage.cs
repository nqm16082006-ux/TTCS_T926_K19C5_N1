using EventTicketBooking.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EventTicketBooking.Api.Migrations;

public partial class NormalizeShowtimeStatusStorage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Historical migration uses a PostgreSQL enum; EF now stores canonical enum names as text.
        migrationBuilder.Sql("""
            ALTER TABLE "Showtimes" ALTER COLUMN "Status" DROP DEFAULT;
            ALTER TABLE "Showtimes" ALTER COLUMN "Status" TYPE text USING
                CASE lower(replace("Status"::text, '_', ''))
                    WHEN 'draft' THEN 'Draft'
                    WHEN 'onsale' THEN 'OnSale'
                    WHEN 'closed' THEN 'Closed'
                    ELSE "Status"::text
                END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "Showtimes" ALTER COLUMN "Status" TYPE showtime_status USING
                (CASE "Status" WHEN 'Draft' THEN 'draft' WHEN 'OnSale' THEN 'on_sale'
                  WHEN 'Closed' THEN 'closed' ELSE "Status" END)::showtime_status;
            ALTER TABLE "Showtimes" ALTER COLUMN "Status" SET DEFAULT 'draft'::showtime_status;
            """);
    }
}
