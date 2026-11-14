using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // CHECK constraints for five status/type columns whose allowed values are a fixed list in code
    // (built from the same constants, see ApplicationDbContext.ClosedListCheck).
    //
    // Added NOT VALID, on purpose. EF's AddCheckConstraint checks every existing row, so one old row
    // holding a value outside the list would fail this migration, and with it the app's startup.
    // NOT VALID checks only rows inserted or updated from now on. Before deploying, the owner runs
    // the read-only counts (each must be 0, or an UPDATE of that old row would now be refused).
    // Later, as a separate owner step, once per constraint:
    //   ALTER TABLE "WindowEvents" VALIDATE CONSTRAINT "CK_WindowEvents_Status";
    // which only reads the rows, and fails harmlessly if a bad one is still there.
    //
    // Down drops them, which is all Up added.
    public partial class AddClosedListCheckConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            AddNotValid(migrationBuilder, "WindowEvents", "CK_WindowEvents_EntryType",
                "\"EntryType\" IS NULL OR \"EntryType\" IN ('Kings Camp', 'Wyrms Camp', 'Misc Camp', 'Kill')");
            AddNotValid(migrationBuilder, "WindowEvents", "CK_WindowEvents_Status",
                "\"Status\" IN ('Open', 'Closed', 'Archived')");
            AddNotValid(migrationBuilder, "Linkshells", "CK_Linkshells_HnmAttendanceMode",
                "\"HnmAttendanceMode\" IN ('Standard', 'Wd')");
            AddNotValid(migrationBuilder, "AttendanceSnapshots", "CK_AttendanceSnapshots_SlotKind",
                "\"SlotKind\" IN ('Window', 'Misc')");
            AddNotValid(migrationBuilder, "AttendanceSnapshots", "CK_AttendanceSnapshots_SnapshotStatus",
                "\"SnapshotStatus\" IN ('Active', 'Ignored', 'Pending')");
        }

        // The constraint EF would add, but NOT VALID (see above).
        private static void AddNotValid(MigrationBuilder migrationBuilder, string table, string name, string sql)
            => migrationBuilder.Sql($"ALTER TABLE \"{table}\" ADD CONSTRAINT \"{name}\" CHECK ({sql}) NOT VALID;");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WindowEvents_EntryType",
                table: "WindowEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WindowEvents_Status",
                table: "WindowEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Linkshells_HnmAttendanceMode",
                table: "Linkshells");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AttendanceSnapshots_SlotKind",
                table: "AttendanceSnapshots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AttendanceSnapshots_SnapshotStatus",
                table: "AttendanceSnapshots");
        }
    }
}
