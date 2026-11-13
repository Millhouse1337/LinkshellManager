using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class DropDeadAccountAuctionChartColumns : Migration
    {
        // Phase 4B, step 3. Drops account, auction, chart, Claim Shield and presence columns no code
        // has read or written since Phase 4A, on AspNetUsers, AuctionHistories, AuctionItems,
        // ChartMemberKeyItems, ChartPopItemCredits, ChartPopItems, ChartWishlistRequests,
        // ClaimShieldCaptures, DiscordActivityUsers and LinkshellPresences.
        // Down puts the columns back EMPTY; the values only come back from the pg_dump deploy.ps1
        // takes before migrating.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuctionHistories_DkpPools_DkpPoolId",
                table: "AuctionHistories");

            migrationBuilder.DropIndex(
                name: "IX_ChartPopItemCredits_LinkshellId_MembershipId",
                table: "ChartPopItemCredits");

            migrationBuilder.DropIndex(
                name: "IX_AuctionHistories_DkpPoolId",
                table: "AuctionHistories");

            migrationBuilder.DropColumn(
                name: "AppUserId",
                table: "LinkshellPresences");

            migrationBuilder.DropColumn(
                name: "ReportedByCharacterName",
                table: "LinkshellPresences");

            migrationBuilder.DropColumn(
                name: "Discriminator",
                table: "DiscordActivityUsers");

            migrationBuilder.DropColumn(
                name: "LastSeenAtUtc",
                table: "DiscordActivityUsers");

            migrationBuilder.DropColumn(
                name: "MatchedCount",
                table: "ClaimShieldCaptures");

            migrationBuilder.DropColumn(
                name: "MemberCount",
                table: "ClaimShieldCaptures");

            migrationBuilder.DropColumn(
                name: "RequestedByMembershipId",
                table: "ChartWishlistRequests");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "ChartWishlistRequests");

            migrationBuilder.DropColumn(
                name: "HeldByMembershipId",
                table: "ChartPopItems");

            migrationBuilder.DropColumn(
                name: "Detail",
                table: "ChartPopItemCredits");

            migrationBuilder.DropColumn(
                name: "LinkshellId",
                table: "ChartPopItemCredits");

            migrationBuilder.DropColumn(
                name: "MembershipId",
                table: "ChartPopItemCredits");

            migrationBuilder.DropColumn(
                name: "CharacterName",
                table: "ChartMemberKeyItems");

            migrationBuilder.DropColumn(
                name: "EndTime",
                table: "AuctionItems");

            migrationBuilder.DropColumn(
                name: "EndingBidDkp",
                table: "AuctionItems");

            migrationBuilder.DropColumn(
                name: "StartTime",
                table: "AuctionItems");

            migrationBuilder.DropColumn(
                name: "DiscordChannelId",
                table: "AuctionHistories");

            migrationBuilder.DropColumn(
                name: "DkpPoolId",
                table: "AuctionHistories");

            migrationBuilder.DropColumn(
                name: "PrimaryLinkshellName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ProfileImage",
                table: "AspNetUsers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppUserId",
                table: "LinkshellPresences",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportedByCharacterName",
                table: "LinkshellPresences",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Discriminator",
                table: "DiscordActivityUsers",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                // "0" is what the app used to write (Discord retired discriminators).
                defaultValue: "0");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAtUtc",
                table: "DiscordActivityUsers",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // NOT NULL, so rows that exist on rollback need a real value: when the row was made is
            // the closest thing we have to "last seen".
            migrationBuilder.Sql("UPDATE \"DiscordActivityUsers\" SET \"LastSeenAtUtc\" = \"CreatedAtUtc\";");

            migrationBuilder.AddColumn<int>(
                name: "MatchedCount",
                table: "ClaimShieldCaptures",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MemberCount",
                table: "ClaimShieldCaptures",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RequestedByMembershipId",
                table: "ChartWishlistRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "ChartWishlistRequests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // NOT NULL, so rows that exist on rollback need a real value: the request time.
            migrationBuilder.Sql("UPDATE \"ChartWishlistRequests\" SET \"UpdatedAt\" = \"RequestedAt\";");

            migrationBuilder.AddColumn<int>(
                name: "HeldByMembershipId",
                table: "ChartPopItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Detail",
                table: "ChartPopItemCredits",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LinkshellId",
                table: "ChartPopItemCredits",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // The credit's linkshell is its pop item's linkshell.
            migrationBuilder.Sql(
                "UPDATE \"ChartPopItemCredits\" c SET \"LinkshellId\" = p.\"LinkshellId\" " +
                "FROM \"ChartPopItems\" p WHERE p.\"Id\" = c.\"ChartPopItemId\";");

            migrationBuilder.AddColumn<int>(
                name: "MembershipId",
                table: "ChartPopItemCredits",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CharacterName",
                table: "ChartMemberKeyItems",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "EndTime",
                table: "AuctionItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EndingBidDkp",
                table: "AuctionItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartTime",
                table: "AuctionItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscordChannelId",
                table: "AuctionHistories",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DkpPoolId",
                table: "AuctionHistories",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrimaryLinkshellName",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ProfileImage",
                table: "AspNetUsers",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChartPopItemCredits_LinkshellId_MembershipId",
                table: "ChartPopItemCredits",
                columns: new[] { "LinkshellId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuctionHistories_DkpPoolId",
                table: "AuctionHistories",
                column: "DkpPoolId");

            migrationBuilder.AddForeignKey(
                name: "FK_AuctionHistories_DkpPools_DkpPoolId",
                table: "AuctionHistories",
                column: "DkpPoolId",
                principalTable: "DkpPools",
                principalColumn: "Id");
        }
    }
}
