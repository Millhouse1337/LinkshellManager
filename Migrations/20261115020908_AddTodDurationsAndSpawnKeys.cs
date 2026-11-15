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
        // Every character .NET calls whitespace (char.IsWhiteSpace, string.Trim and Regex \s all use
        // this set), written out for a Postgres bracket expression. Postgres's own \s depends on the
        // server's locale and leaves out the no-break space, so the C# trimmed "22<nbsp>Hour" and the
        // SQL did not; spelling the set out makes both agree on any server.
        // TodDerivedValueTests checks it is exactly char.IsWhiteSpace.
        public const string WhitespaceChars = @" \t\n\v\f\r\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000";

        // The minutes TodDurationFormat.StoredMinutes gives for a Cooldown / Interval text.
        // bareMultiplier is what a unit-less number means: 60 (hours) for a cooldown, 1 (minutes)
        // for an interval. \y is Postgres's word boundary (.NET's \b).
        //
        // The maths is done in double precision (float8), step for step as the C# does it, NOT in
        // exact numeric: "1.025" hours is 61.5 minutes exactly, but as a double it is
        // 61.49999..., so the C# says 61 and exact maths would say 62. Each number is capped at
        // 1e15 first (anything that big is more minutes than an integer holds either way, so the
        // answer is the same) so a huge text never overflows float8. The rounding is the C#'s
        // Math.Round(MidpointRounding.AwayFromZero): add one when the fraction is at least a half
        // (x - trunc(x) is exact in float8).
        public static string DurationMinutesSql(string column, int bareMultiplier) => $"""
            (SELECT CASE WHEN y.n > 0 AND y.n <= 2147483647 THEN y.n::integer END
             FROM (SELECT CASE WHEN x.raw - trunc(x.raw) >= 0.5 THEN trunc(x.raw) + 1 ELSE trunc(x.raw) END AS n
                   FROM (SELECT CASE
                           WHEN {column} IS NULL OR {column} !~ '[^{WhitespaceChars}]' THEN NULL
                           WHEN b.m IS NOT NULL THEN {Float8Sql("b.m[1]")} * {bareMultiplier}
                           WHEN p.m IS NOT NULL AND (p.m[1] IS NOT NULL OR p.m[2] IS NOT NULL)
                             THEN {Float8Sql("p.m[1]")} * 60
                                  + {Float8Sql("p.m[2]")}
                         END AS raw
                         FROM (SELECT regexp_match({column},
                                  '^[{WhitespaceChars}]*([0-9]+(?:\.[0-9]+)?)[{WhitespaceChars}]*$') AS m) b,
                              (SELECT regexp_match({column},
                                  '^[{WhitespaceChars}]*(?:([0-9]+(?:\.[0-9]+)?)[{WhitespaceChars}]*(?:hours?|hrs?|h)\y)?[{WhitespaceChars}]*(?:([0-9]+(?:\.[0-9]+)?)[{WhitespaceChars}]*(?:minutes?|mins?|m)\y)?[{WhitespaceChars}]*$',
                                  'i') AS m) p) x) y)
            """;

        // One matched number as the double the C# parses it to (0 when the group did not match, as
        // the C# leaves it), capped at 1e15. Through numeric so the cap works on any length of text;
        // numeric to float8 reads the exact decimal digits, like double.Parse.
        private static string Float8Sql(string group) =>
            $"CASE WHEN {group} IS NULL THEN 0::float8 ELSE least({group}::numeric, 1e15)::float8 END";

        // The key HnmConfig.SpawnKey gives for a MonsterName: blank is NULL; otherwise the pieces
        // between '/' are trimmed, empty ones dropped, and lower-cased; a name with either half of a
        // merge pair (checked in HnmConfig.MonsterMergePairs order) is that pair's combined label,
        // anything else its pieces joined by '/'.
        public static string SpawnKeySql(string column) => $$"""
            (SELECT CASE
                WHEN {{column}} IS NULL OR {{column}} !~ '[^{{WhitespaceChars}}]' THEN NULL
                WHEN 'adamantoise' = ANY(s.l) OR 'aspidochelone' = ANY(s.l) THEN 'adamantoise/aspidochelone'
                WHEN 'behemoth' = ANY(s.l) OR 'king behemoth' = ANY(s.l) THEN 'behemoth/king behemoth'
                WHEN 'fafnir' = ANY(s.l) OR 'nidhogg' = ANY(s.l) THEN 'fafnir/nidhogg'
                ELSE array_to_string(s.l, '/') END
             FROM (SELECT coalesce(array_agg(lower(z.t) ORDER BY z.i), '{}'::text[]) AS l
                   FROM (SELECT regexp_replace(u.s, '^[{{WhitespaceChars}}]+|[{{WhitespaceChars}}]+$', '', 'g') AS t, u.i
                         FROM unnest(string_to_array({{column}}, '/')) WITH ORDINALITY AS u(s, i)) z
                   WHERE z.t <> '') s)
            """;

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
