using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// Moves a Standard HNM camp's money onto its CAPTURES.
    ///
    /// A camp posts one capture per window and each window is priced differently — the open, the
    /// close, the regular rate, the kill roster at 0 — but the review card only ever held one
    /// amount per member, so it showed that same number in every capture. Three windows at 2 read
    /// as 6. AttendanceSnapshotEntries.DkpAmount is what each capture pays one person, and
    /// WindowEvents.PerCaptureDkp says which of the two shapes a row is paid from.
    ///
    /// Both default to their empty value, so every review row that already exists keeps paying from
    /// its per-member WindowEventMemberDkps rows exactly as it was reviewed. Only camps handed off
    /// after this deploy are priced per capture.
    /// </summary>
    public partial class PriceHnmCampCapturesIndividually : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PerCaptureDkp",
                table: "WindowEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "DkpAmount",
                table: "AttendanceSnapshotEntries",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PerCaptureDkp",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "DkpAmount",
                table: "AttendanceSnapshotEntries");
        }
    }
}
