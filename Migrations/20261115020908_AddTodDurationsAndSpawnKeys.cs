using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Expand step for numeric ToD durations and one key per spawn.
    //
    // Tods and PendingTodSubmissions gain CooldownMinutes and IntervalMinutes (the Cooldown /
    // Interval text in whole minutes) and SpawnKey (the MonsterName's one-per-spawn key: either
    // half of a merge pair, or the combined label, all give "fafnir/nidhogg"). HnmRecurringBoards
    // gains SpawnKey. They are filled here from the text, and the app stamps them on every save
    // (ApplicationDbContext.StampDerivedColumns, using TodDurationFormat.StoredMinutes and
    // HnmConfig.SpawnKey). Nothing reads them yet; a later deploy switches the lookups and the repop
    // maths onto them, after re-running FillSql to repair rows an older build saved meanwhile.
    //
    // The SQL below copies the C# exactly, and TodDerivedColumnsParityTests checks both give the
    // same answer for every value production holds plus the edge cases. A text that reads as no
    // duration (blank, zero, unknown words, or more minutes than an integer holds) stays NULL.
    //
    // The new SpawnKey index on HnmRecurringBoards is NOT unique yet: two boards for one spawn are
    // merged first, in the later deploy. The old (LinkshellId, MonsterName) indexes stay. EF drops
    // IX_PendingTodSubmissions_LinkshellId because the new (LinkshellId, SpawnKey) index covers its
    // foreign key.
    //
    // Safe to run twice: the fill only writes rows whose stored value differs from the derived one,
    // and says in a NOTICE how many it wrote. No DKP or gil is touched, and no row is deleted.
    //
    // Down drops the columns, checks and indexes (the values are re-derived from the text on the
    // next Up). The old build never reads or writes them, so rolling the app back without Down also
    // works.
    public partial class AddTodDurationsAndSpawnKeys : Migration
    {
        // The SQL copies of the C# rules live in Data/TodDerivedSql.cs (moved there before the
        // migration squash); these forward to them, so the SQL this migration runs is unchanged.
        public const string WhitespaceChars = TodDerivedSql.WhitespaceChars;

        public static string DurationMinutesSql(string column, int bareMultiplier) =>
            TodDerivedSql.DurationMinutesSql(column, bareMultiplier);

        public static string SpawnKeySql(string column) => TodDerivedSql.SpawnKeySql(column);

        private static string FillDurationsAndKeySql(string table) => $"""
                WITH d AS (
                    SELECT t."Id",
                           {DurationMinutesSql("t.\"Cooldown\"", 60)} AS c,
                           {DurationMinutesSql("t.\"Interval\"", 1)} AS i,
                           {SpawnKeySql("t.\"MonsterName\"")} AS k
                    FROM "{table}" t)
                UPDATE "{table}" t
                SET "CooldownMinutes" = d.c, "IntervalMinutes" = d.i, "SpawnKey" = d.k
                FROM d
                WHERE t."Id" = d."Id"
                  AND (t."CooldownMinutes", t."IntervalMinutes", t."SpawnKey") IS DISTINCT FROM (d.c, d.i, d.k);
            """;

        // Public so the Postgres round-trip test (and the later switch migration) can run exactly
        // this fill again.
        public static readonly string FillSql = $"""
            DO $$
            DECLARE tods integer; pending integer; boards integer;
            BEGIN
                {FillDurationsAndKeySql("Tods")}
                GET DIAGNOSTICS tods = ROW_COUNT;
                {FillDurationsAndKeySql("PendingTodSubmissions")}
                GET DIAGNOSTICS pending = ROW_COUNT;
                WITH d AS (SELECT b."Id", {SpawnKeySql("b.\"MonsterName\"")} AS k FROM "HnmRecurringBoards" b)
                UPDATE "HnmRecurringBoards" b SET "SpawnKey" = d.k
                FROM d
                WHERE b."Id" = d."Id" AND b."SpawnKey" IS DISTINCT FROM d.k;
                GET DIAGNOSTICS boards = ROW_COUNT;
                RAISE NOTICE 'AddTodDurationsAndSpawnKeys: filled % ToD(s), % pending ToD(s), % standing board(s).', tods, pending, boards;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingTodSubmissions_LinkshellId",
                table: "PendingTodSubmissions");

            migrationBuilder.AddColumn<int>(
                name: "CooldownMinutes",
                table: "Tods",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IntervalMinutes",
                table: "Tods",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpawnKey",
                table: "Tods",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CooldownMinutes",
                table: "PendingTodSubmissions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IntervalMinutes",
                table: "PendingTodSubmissions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpawnKey",
                table: "PendingTodSubmissions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpawnKey",
                table: "HnmRecurringBoards",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.Sql(FillSql);

            migrationBuilder.CreateIndex(
                name: "IX_Tods_LinkshellId_SpawnKey_Time",
                table: "Tods",
                columns: new[] { "LinkshellId", "SpawnKey", "Time" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tods_CooldownMinutes_Positive",
                table: "Tods",
                sql: "\"CooldownMinutes\" IS NULL OR \"CooldownMinutes\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tods_IntervalMinutes_Positive",
                table: "Tods",
                sql: "\"IntervalMinutes\" IS NULL OR \"IntervalMinutes\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTodSubmissions_LinkshellId_SpawnKey",
                table: "PendingTodSubmissions",
                columns: new[] { "LinkshellId", "SpawnKey" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PendingTodSubmissions_CooldownMinutes_Positive",
                table: "PendingTodSubmissions",
                sql: "\"CooldownMinutes\" IS NULL OR \"CooldownMinutes\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PendingTodSubmissions_IntervalMinutes_Positive",
                table: "PendingTodSubmissions",
                sql: "\"IntervalMinutes\" IS NULL OR \"IntervalMinutes\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_HnmRecurringBoards_LinkshellId_SpawnKey",
                table: "HnmRecurringBoards",
                columns: new[] { "LinkshellId", "SpawnKey" });
        }

        // Schema only: the derived values are dropped with their columns and filled again from the
        // text on the next Up. No row is deleted either way.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tods_LinkshellId_SpawnKey_Time",
                table: "Tods");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tods_CooldownMinutes_Positive",
                table: "Tods");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tods_IntervalMinutes_Positive",
                table: "Tods");

            migrationBuilder.DropIndex(
                name: "IX_PendingTodSubmissions_LinkshellId_SpawnKey",
                table: "PendingTodSubmissions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PendingTodSubmissions_CooldownMinutes_Positive",
                table: "PendingTodSubmissions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PendingTodSubmissions_IntervalMinutes_Positive",
                table: "PendingTodSubmissions");

            migrationBuilder.DropIndex(
                name: "IX_HnmRecurringBoards_LinkshellId_SpawnKey",
                table: "HnmRecurringBoards");

            migrationBuilder.DropColumn(
                name: "CooldownMinutes",
                table: "Tods");

            migrationBuilder.DropColumn(
                name: "IntervalMinutes",
                table: "Tods");

            migrationBuilder.DropColumn(
                name: "SpawnKey",
                table: "Tods");

            migrationBuilder.DropColumn(
                name: "CooldownMinutes",
                table: "PendingTodSubmissions");

            migrationBuilder.DropColumn(
                name: "IntervalMinutes",
                table: "PendingTodSubmissions");

            migrationBuilder.DropColumn(
                name: "SpawnKey",
                table: "PendingTodSubmissions");

            migrationBuilder.DropColumn(
                name: "SpawnKey",
                table: "HnmRecurringBoards");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTodSubmissions_LinkshellId",
                table: "PendingTodSubmissions",
                column: "LinkshellId");
        }
    }
}
