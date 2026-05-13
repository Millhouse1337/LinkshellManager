using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkshellGoogleOAuthTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "GoogleOAuthConnectedAt",
                table: "Linkshells",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleOAuthRefreshTokenEnc",
                table: "Linkshells",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleOAuthUserEmail",
                table: "Linkshells",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GoogleOAuthConnectedAt",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "GoogleOAuthRefreshTokenEnc",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "GoogleOAuthUserEmail",
                table: "Linkshells");
        }
    }
}
