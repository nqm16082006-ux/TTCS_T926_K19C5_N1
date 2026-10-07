using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketBooking.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddImageUrlToEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 
                        FROM information_schema.columns 
                        WHERE table_name = 'Events' AND column_name = 'ImageUrl'
                    ) THEN
                        ALTER TABLE ""Events"" ADD ""ImageUrl"" text;
                    END IF;
                END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 
                        FROM information_schema.columns 
                        WHERE table_name = 'Events' AND column_name = 'ImageUrl'
                    ) THEN
                        ALTER TABLE ""Events"" DROP COLUMN ""ImageUrl"";
                    END IF;
                END $$;");
        }
    }
}
