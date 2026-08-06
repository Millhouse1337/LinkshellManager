using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// Rewrites the literal sub-job string "Player's Choice" to "ANY" on event participation rows.
    ///
    /// Everywhere else an open sub-job is the NULL/empty sentinel — PartySetupSlot.SubJob is
    /// MaxLength(8) and physically cannot hold the phrase, and every reader branches on
    /// IsNullOrWhiteSpace. The Activity was the exception: EVENT_SUB_JOB_OPTIONS shipped
    /// "Player's Choice" as a selectable *value* rather than a blank-option label, and the signup
    /// endpoints only trim what they're handed, so picking it wrote the phrase straight into
    /// AppUserEvents.SubJobName (an untyped text column) — and from there into
    /// AppUserEventHistories.SubJobName when the event closed. Those rosters read
    /// "WAR/Player's Choice" while every other surface said "WAR/PC".
    ///
    /// The wording is now "ANY" across the website, the Activity, and the Discord board, so these
    /// rows are brought along rather than left as the last place the old phrase survives.
    ///
    /// Both columns are nullable text with no catalog constraint, so the WHERE is an exact match on
    /// the one phrase the dropdown could produce — a sub-job a member typed themselves can't collide
    /// with it, and re-running the migration is a no-op.
    /// </summary>
    public partial class NormalizePlayersChoiceSubJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "AppUserEvents"
                SET "SubJobName" = 'ANY'
                WHERE "SubJobName" = 'Player''s Choice';

                UPDATE "AppUserEventHistories"
                SET "SubJobName" = 'ANY'
                WHERE "SubJobName" = 'Player''s Choice';
                """);
        }

        /// <inheritdoc />
        // True inverse. "ANY" is only ever written by the dropdown this migration follows, so
        // reverting the code and the data together restores the exact prior state.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "AppUserEvents"
                SET "SubJobName" = 'Player''s Choice'
                WHERE "SubJobName" = 'ANY';

                UPDATE "AppUserEventHistories"
                SET "SubJobName" = 'Player''s Choice'
                WHERE "SubJobName" = 'ANY';
                """);
        }
    }
}
