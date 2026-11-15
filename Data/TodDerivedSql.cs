namespace LinkshellManagerDiscordApp.Data;

// SQL copies of two C# rules, for SQL that fills or checks the derived ToD columns:
// - DurationMinutesSql is TodDurationFormat.StoredMinutes (Cooldown / Interval text -> minutes);
// - SpawnKeySql is HnmConfig.SpawnKey (MonsterName -> one key per spawn).
// The save hook (ApplicationDbContext.StampDerivedColumns) uses the C#. These were first written for
// the AddTodDurationsAndSpawnKeys migration's fill and moved here when the migrations were squashed
// into one Baseline (Round 4, D11), so a later fill (SpawnKey on more tables, say) can reuse them.
//
// TodDerivedColumnsParityTests runs both on real Postgres and checks they give the same answer for
// every value production holds plus the edge cases; TodDerivedValueTests checks WhitespaceChars.
public static class TodDerivedSql
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
}
