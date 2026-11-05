using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class MergeHnmRecurringBoardsPerSpawn : Migration
    {
        // Data-only. A merge pair is ONE spawn, so a linkshell keeps ONE standing re-post board for
        // it, named with the combined label ("Adamantoise/Aspidochelone"). Older rows were keyed on
        // whichever half the camp was named at the time, so some linkshells hold two or three rows
        // for one spawn: the edit form read the re-post lead from one row and saved it to another,
        // and End Camp could switch re-post off on one row while another kept re-posting the board.
        //
        // Per linkshell and pair, the row kept is the one in use: enabled first, then the most
        // recently updated, then the newest id. The others are deleted and the kept row is renamed
        // to the combined label. Delete runs first because (LinkshellId, MonsterName) is unique.
        //
        // PUBLIC so HnmRecurringBoardMergeMigrationTests can check the pairs match HnmConfig.
        public static readonly (string Base, string Stronger)[] Pairs =
        {
            ("Adamantoise", "Aspidochelone"),
            ("Behemoth", "King Behemoth"),
            ("Fafnir", "Nidhogg"),
        };

        public static string MergeSql(string baseName, string stronger)
        {
            var combined = $"{baseName}/{stronger}";
            var names = $"'{baseName.ToLowerInvariant()}', '{stronger.ToLowerInvariant()}', '{combined.ToLowerInvariant()}'";
            return $@"
                DELETE FROM ""HnmRecurringBoards"" AS board
                USING (
                    SELECT ""Id"", row_number() OVER (
                        PARTITION BY ""LinkshellId""
                        ORDER BY ""Enabled"" DESC, ""UpdatedAt"" DESC, ""Id"" DESC) AS rank
                    FROM ""HnmRecurringBoards""
                    WHERE lower(trim(""MonsterName"")) IN ({names})
                ) AS ranked
                WHERE board.""Id"" = ranked.""Id"" AND ranked.rank > 1;

                UPDATE ""HnmRecurringBoards""
                SET ""MonsterName"" = '{combined}'
                WHERE lower(trim(""MonsterName"")) IN ({names})
                  AND ""MonsterName"" <> '{combined}';";
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (baseName, stronger) in Pairs)
            {
                migrationBuilder.Sql(MergeSql(baseName, stronger));
            }

            // The turtle's default zone was wrong (HnmConfig said Qufim Island), and a standing
            // board copies its location onto every board it posts. Only the old default is fixed;
            // a location an officer typed is left alone.
            migrationBuilder.Sql(@"
                UPDATE ""HnmRecurringBoards""
                SET ""EventLocation"" = 'Valley of Sorrows'
                WHERE ""MonsterName"" = 'Adamantoise/Aspidochelone'
                  AND ""EventLocation"" = 'Qufim Island';");
        }

        // Nothing to undo with any accuracy: the deleted rows are gone, and the kept row no longer
        // says which half it was named after.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
