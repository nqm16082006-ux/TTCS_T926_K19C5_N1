using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketBooking.Api.Migrations
{
    /// <inheritdoc />
    public partial class UpdateEmailFailureLog_S27 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RetryCount",
                table: "EmailFailureLogs",
                newName: "Attempts");

            migrationBuilder.RenameColumn(
                name: "FailedAt",
                table: "EmailFailureLogs",
                newName: "CreatedAt");

            migrationBuilder.AddColumn<bool>(
                name: "Resolved",
                table: "EmailFailureLogs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Resolved",
                table: "EmailFailureLogs");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "EmailFailureLogs",
                newName: "FailedAt");

            migrationBuilder.RenameColumn(
                name: "Attempts",
                table: "EmailFailureLogs",
                newName: "RetryCount");
        }
    }
}
