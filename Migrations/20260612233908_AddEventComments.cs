using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddEventComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiscussionChannelId",
                table: "Linkshells",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EventComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventHistoryId = table.Column<int>(type: "integer", nullable: false),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsAnonymous = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DiscordMessageId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventComments_EventHistories_EventHistoryId",
                        column: x => x.EventHistoryId,
                        principalTable: "EventHistories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventComments_EventHistoryId_CreatedAt",
                table: "EventComments",
                columns: new[] { "EventHistoryId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventComments");

            migrationBuilder.DropColumn(
                name: "DiscussionChannelId",
                table: "Linkshells");
        }
    }
}
