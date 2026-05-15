using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddHnmAutoEventLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceTodId",
                table: "Events",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_SourceTodId",
                table: "Events",
                column: "SourceTodId");

            migrationBuilder.AddForeignKey(
                name: "FK_Events_Tods_SourceTodId",
                table: "Events",
                column: "SourceTodId",
                principalTable: "Tods",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Events_Tods_SourceTodId",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_SourceTodId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "SourceTodId",
                table: "Events");
        }
    }
}
