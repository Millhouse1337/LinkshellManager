using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordChannelsAndEventMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiscordChannelId",
                table: "Events",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscordMessageId",
                table: "Events",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LinkshellDiscordChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ChannelId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChannelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellDiscordChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellDiscordChannels_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellDiscordChannels_LinkshellId",
                table: "LinkshellDiscordChannels",
                column: "LinkshellId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkshellDiscordChannels");

            migrationBuilder.DropColumn(
                name: "DiscordChannelId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "DiscordMessageId",
                table: "Events");
        }
    }
}
