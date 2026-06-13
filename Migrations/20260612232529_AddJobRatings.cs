using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddJobRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobRatings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    TargetAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    RaterAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    JobIndex = table.Column<int>(type: "integer", nullable: false),
                    Gear = table.Column<int>(type: "integer", nullable: false),
                    Skill = table.Column<int>(type: "integer", nullable: false),
                    HasRelic = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRatings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobRatings_LinkshellId_TargetAppUserId",
                table: "JobRatings",
                columns: new[] { "LinkshellId", "TargetAppUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_JobRatings_LinkshellId_TargetAppUserId_RaterAppUserId_JobIn~",
                table: "JobRatings",
                columns: new[] { "LinkshellId", "TargetAppUserId", "RaterAppUserId", "JobIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobRatings");
        }
    }
}
