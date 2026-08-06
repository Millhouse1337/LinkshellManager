using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class HealAutoFlaggedDuplicateSnapshots : Migration
    {
        // Data-only. Nothing auto-flags a snapshot "PossibleDuplicate" any more — posts landing
        // close together are folded into one snapshot instead — but rows flagged by the old check
        // are still sitting in the database, and a flagged row is EXCLUDED from its Window Event's
        // combined roster (AttendanceSectionsBuilder.BuildCombinedMembers filters to Active). So
        // every member who appeared only in a flagged snapshot is currently missing from the roster
        // their officer is about to post to the DKP sheet. Releasing them is the whole point.
        //
        // "PossibleDuplicate" ONLY. "Duplicate" and "Ignored" are set by an officer deciding a
        // snapshot shouldn't count, and that decision is theirs to keep.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""AttendanceSnapshots""
                SET ""SnapshotStatus"" = 'Active',
                    ""DuplicateOfSnapshotId"" = NULL
                WHERE ""SnapshotStatus"" = 'PossibleDuplicate';");
        }

        // Deliberately empty, and NOT reversible. Which rows a heuristic had guessed at is not
        // recorded anywhere once cleared, and re-running the old ±8min/75%-overlap guess on a Down
        // would invent a different set of flags than the ones removed. An officer who wants a
        // specific snapshot excluded can mark it Duplicate or Ignored by hand.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
