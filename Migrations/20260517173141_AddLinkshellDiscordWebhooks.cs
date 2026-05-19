using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkshellDiscordWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LinkshellDiscordWebhooks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellDiscordWebhooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellDiscordWebhooks_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellDiscordWebhooks_LinkshellId",
                table: "LinkshellDiscordWebhooks",
                column: "LinkshellId");

            // Preserve any existing single webhook URL by migrating it into a
            // "Default" row before the old column is dropped.
            migrationBuilder.Sql(@"
                INSERT INTO ""LinkshellDiscordWebhooks"" (""LinkshellId"", ""Name"", ""Url"", ""CreatedAtUtc"")
                SELECT ""Id"", 'Default', ""DiscordWebhookUrl"", now()
                FROM ""Linkshells""
                WHERE ""DiscordWebhookUrl"" IS NOT NULL AND btrim(""DiscordWebhookUrl"") <> '';");

            migrationBuilder.DropColumn(
                name: "DiscordWebhookUrl",
                table: "Linkshells");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkshellDiscordWebhooks");

            migrationBuilder.AddColumn<string>(
                name: "DiscordWebhookUrl",
                table: "Linkshells",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }
    }
}
