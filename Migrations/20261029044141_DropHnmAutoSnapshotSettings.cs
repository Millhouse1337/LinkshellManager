using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class DropHnmAutoSnapshotSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HnmAutoSnapshotDelaySeconds",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "HnmAutoSnapshotEnabled",
                table: "Linkshells");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 20, not EF's generated 0: the column's own default when it existed. A rolled-back
            // linkshell that switched the feature on would otherwise capture at window open.
            migrationBuilder.AddColumn<int>(
                name: "HnmAutoSnapshotDelaySeconds",
                table: "Linkshells",
                type: "integer",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<bool>(
                name: "HnmAutoSnapshotEnabled",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
