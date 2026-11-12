using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaceholderClaimRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DiscordIdConfirmed",
                table: "AppUserLinkshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Discord links that already exist stay trusted, so nobody who signed up through the
            // form before today has to be approved again. We cannot tell which of them the form
            // attached to an existing imported member (the hole this step closes), so those are
            // reviewed by hand once. Only links made from now on are checked.
            migrationBuilder.Sql("""
                UPDATE "AppUserLinkshells" SET "DiscordIdConfirmed" = TRUE WHERE "DiscordUserId" IS NOT NULL;
                """);

            migrationBuilder.CreateTable(
                name: "PlaceholderClaimRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    PlaceholderAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    RequesterAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    RequesterDiscordUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RequesterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Approved = table.Column<bool>(type: "boolean", nullable: true),
                    ResolvedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaceholderClaimRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaceholderClaimRequests_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaceholderClaimRequests_LinkshellId_ResolvedAtUtc",
                table: "PlaceholderClaimRequests",
                columns: new[] { "LinkshellId", "ResolvedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaceholderClaimRequests_PlaceholderAppUserId_RequesterAppU~",
                table: "PlaceholderClaimRequests",
                columns: new[] { "PlaceholderAppUserId", "RequesterAppUserId" },
                unique: true,
                filter: "\"ResolvedAtUtc\" IS NULL AND \"RequesterAppUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlaceholderClaimRequests_PlaceholderAppUserId_RequesterDisc~",
                table: "PlaceholderClaimRequests",
                columns: new[] { "PlaceholderAppUserId", "RequesterDiscordUserId" },
                unique: true,
                filter: "\"ResolvedAtUtc\" IS NULL AND \"RequesterDiscordUserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaceholderClaimRequests");

            migrationBuilder.DropColumn(
                name: "DiscordIdConfirmed",
                table: "AppUserLinkshells");
        }
    }
}
