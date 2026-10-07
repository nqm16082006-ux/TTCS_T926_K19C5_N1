using Microsoft.EntityFrameworkCore.Migrations;

namespace EventTicketBooking.Api.Migrations;

public partial class AlignLegacyOrderStatusAndImageUrl : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // A new migration repairs legacy schemas without changing previously applied files.
        // Both enum and text representations are accepted, but unknown values fail safely.
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "orders"
                    WHERE "Status" IS NULL OR lower(replace("Status"::text, '_', ''))
                    NOT IN ('pending', 'paid', 'cancelled', 'expired', 'needsattention')) THEN
                    RAISE EXCEPTION 'Unknown order status; inspect data before applying migration';
                END IF;
                IF EXISTS (SELECT 1 FROM "orders"
                    WHERE lower(replace("Status"::text, '_', '')) = 'pending'
                    GROUP BY "UserId", "ShowtimeId" HAVING count(*) > 1) THEN
                    RAISE EXCEPTION 'Duplicate pending orders; resolve explicitly before applying migration';
                END IF;
            END $$;
            DROP INDEX IF EXISTS "IX_orders_UserId_ShowtimeId_Pending";
            ALTER TABLE "orders" ALTER COLUMN "Status" DROP DEFAULT;
            ALTER TABLE "orders" ALTER COLUMN "Status" TYPE text USING
                CASE lower(replace("Status"::text, '_', ''))
                    WHEN 'pending' THEN 'Pending'
                    WHEN 'paid' THEN 'Paid'
                    WHEN 'cancelled' THEN 'Cancelled'
                    WHEN 'expired' THEN 'Expired'
                    WHEN 'needsattention' THEN 'NeedsAttention'
                END;
            CREATE UNIQUE INDEX "IX_orders_UserId_ShowtimeId_Pending"
                ON "orders" ("UserId", "ShowtimeId") WHERE "Status" = 'Pending';
            ALTER TABLE "Events" ALTER COLUMN "ImageUrl" TYPE text USING "ImageUrl"::text;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Original enum labels/casing and varchar limits differ between legacy databases.
        // A guessed rollback could truncate URLs or change status semantics.
        throw new NotSupportedException("This legacy normalization requires a reviewed backup/restore plan to roll back; no automatic destructive rollback is provided.");
    }
}
