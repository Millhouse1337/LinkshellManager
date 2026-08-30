using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddChartDropItemsWishlistAndKeyItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "ChartPopItems",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "Pop");

            migrationBuilder.CreateTable(
                name: "ChartMemberKeyItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Board = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    KeyItemName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MembershipId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SetByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    SetByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChartMemberKeyItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChartMemberKeyItems_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChartWishlistRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Board = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Boss = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ItemName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Pending"),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    RequestedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    RequestedByMembershipId = table.Column<int>(type: "integer", nullable: true),
                    RequestedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FulfilledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FulfilledByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    FulfilledByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChartWishlistRequests", x => x.Id);
                    table.CheckConstraint("CK_ChartWishlistRequests_Quantity_Positive", "\"Quantity\" >= 1");
                    table.CheckConstraint("CK_ChartWishlistRequests_Status", "\"Status\" IN ('Pending','Fulfilled')");
                    table.ForeignKey(
                        name: "FK_ChartWishlistRequests_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChartPopItems_Kind",
                table: "ChartPopItems",
                sql: "\"Kind\" IN ('Pop','Drop')");

            migrationBuilder.CreateIndex(
                name: "IX_ChartMemberKeyItems_LinkshellId_Board_KeyItemName_Membershi~",
                table: "ChartMemberKeyItems",
                columns: new[] { "LinkshellId", "Board", "KeyItemName", "MembershipId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChartWishlistRequests_LinkshellId_Board_Status_Priority",
                table: "ChartWishlistRequests",
                columns: new[] { "LinkshellId", "Board", "Status", "Priority" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChartMemberKeyItems");

            migrationBuilder.DropTable(
                name: "ChartWishlistRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ChartPopItems_Kind",
                table: "ChartPopItems");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ChartPopItems");
        }
    }
}
