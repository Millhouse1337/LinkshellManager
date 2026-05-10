using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class MakeTodClaimNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "Claim",
                table: "Tods",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Backfill any null Claim values to false before tightening the
            // column so the down-migration can recreate the non-null constraint.
            migrationBuilder.Sql(@"UPDATE ""Tods"" SET ""Claim"" = false WHERE ""Claim"" IS NULL;");

            migrationBuilder.AlterColumn<bool>(
                name: "Claim",
                table: "Tods",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);
        }
    }
}
