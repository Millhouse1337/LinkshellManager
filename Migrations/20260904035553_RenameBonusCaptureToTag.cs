using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// Renames the capture that carries what no window pays.
    ///
    /// It was filed as "Claim & kill bonuses" when it held both. The kill bonus now rides on the
    /// kill post's own capture — that is what pays for being in the roster when the mob died — so
    /// this one is left holding the tag bonus alone, and is named for it.
    ///
    /// A data-only rename: the label is stored per snapshot (AttendanceSnapshot.Name), so camps
    /// already handed off keep whatever they were filed under unless it is corrected here, and an
    /// officer would see two names for one thing depending on when the camp ended.
    /// </summary>
    public partial class RenameBonusCaptureToTag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "AttendanceSnapshots"
                SET "Name" = 'Tag'
                WHERE "Name" = 'Claim & kill bonuses';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "AttendanceSnapshots"
                SET "Name" = 'Claim & kill bonuses'
                WHERE "Name" = 'Tag';
                """);
        }
    }
}
