using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketBooking.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDataDeletionRequests_S53 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AnonymizedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAnonymized",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "data_deletion_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterEmail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RequesterName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AdminNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_deletion_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_data_deletion_requests_Users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_data_deletion_requests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_data_deletion_requests_ReviewedByUserId",
                table: "data_deletion_requests",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_data_deletion_requests_UserId",
                table: "data_deletion_requests",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_deletion_requests");

            migrationBuilder.DropColumn(
                name: "AnonymizedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsAnonymized",
                table: "Users");
        }
    }
}
