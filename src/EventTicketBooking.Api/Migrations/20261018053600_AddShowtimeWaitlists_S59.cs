using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketBooking.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddShowtimeWaitlists_S59 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShowtimeWaitlists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowtimeId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FullName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PhoneNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "WAITING"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    NotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NotificationCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShowtimeWaitlists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShowtimeWaitlists_Showtimes_ShowtimeId",
                        column: x => x.ShowtimeId,
                        principalTable: "Showtimes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShowtimeWaitlists_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShowtimeWaitlists_ShowtimeId_Email",
                table: "ShowtimeWaitlists",
                columns: new[] { "ShowtimeId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_ShowtimeWaitlists_ShowtimeId_Status_CreatedAt",
                table: "ShowtimeWaitlists",
                columns: new[] { "ShowtimeId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShowtimeWaitlists_UserId",
                table: "ShowtimeWaitlists",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShowtimeWaitlists");
        }
    }
}
