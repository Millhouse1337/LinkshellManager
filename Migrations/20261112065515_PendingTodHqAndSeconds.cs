using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // A member's ToD waiting for approval keeps the two answers it used to lose: which half of a
    // merge pair died (Hq) and the monster's Additional Seconds. Rows already waiting get false / 0,
    // which is what approving them did anyway; the approval still reads HQ off a lone "Nidhogg" name.
    //
    // ReviewNotes goes: nothing has ever written or read it. Down() puts the column back empty.
    /// <inheritdoc />
    public partial class PendingTodHqAndSeconds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewNotes",
                table: "PendingTodSubmissions");

            migrationBuilder.AddColumn<int>(
                name: "AdditionalSeconds",
                table: "PendingTodSubmissions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Hq",
                table: "PendingTodSubmissions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdditionalSeconds",
                table: "PendingTodSubmissions");

            migrationBuilder.DropColumn(
                name: "Hq",
                table: "PendingTodSubmissions");

            migrationBuilder.AddColumn<string>(
                name: "ReviewNotes",
                table: "PendingTodSubmissions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }
    }
}
