using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkshellChannelRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LinkshellChannelRoutes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ChannelId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChannelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PostEvents = table.Column<bool>(type: "boolean", nullable: false),
                    PostLoot = table.Column<bool>(type: "boolean", nullable: false),
                    PostAuctions = table.Column<bool>(type: "boolean", nullable: false),
                    PostAttendance = table.Column<bool>(type: "boolean", nullable: false),
                    PostTodBoard = table.Column<bool>(type: "boolean", nullable: false),
                    EventTypeFilter = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TodBoardMessageId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellChannelRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellChannelRoutes_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellChannelRoutes_LinkshellId",
                table: "LinkshellChannelRoutes",
                column: "LinkshellId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkshellChannelRoutes");
        }
    }
}
