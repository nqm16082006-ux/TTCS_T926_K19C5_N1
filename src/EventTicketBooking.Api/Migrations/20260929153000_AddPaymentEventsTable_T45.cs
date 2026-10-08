using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketBooking.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentEventsTable_T45 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
                        migrationBuilder.Sql("CREATE TYPE order_status AS ENUM ('pending', 'paid', 'cancelled', 'expired', 'needs_attention');");
migrationBuilder.CreateTable(
                name: "payment_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RawPayload = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_events_TransactionId_Unique",
                table: "payment_events",
                column: "TransactionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_events");
        }
    }
}

