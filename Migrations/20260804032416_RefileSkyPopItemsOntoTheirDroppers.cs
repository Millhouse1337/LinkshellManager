using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RefileSkyPopItemsOntoTheirDroppers : Migration
    {
        // Data-only. The Sky board used to file an item under the boss it POPS; it now files it under
        // the boss it DROPS FROM, which is what let the eight farm NMs join the board as cards of
        // their own. Rows written under the old shape sit on a card whose picker no longer offers
        // them — nothing breaks (both surfaces keep an unlisted name as an "already on this row"
        // option), but the per-boss ledger fractions would say something the board no longer means.
        //
        // Twelve moves, and only twelve: every one is a straight (old boss, item) → new boss, taken
        // from ChartBoardCatalog.SkyFarmNmCards and the seals on SkyGodCards. Nothing else on Sky
        // moves — Curtana was already on Suzaku, which still drops it.
        //
        // Credits are untouched and ride along: ChartPopItemCredits keys on ChartPopItemId, so moving
        // a row carries its farmers with it. SortOrder is left alone — cards consolidate by item name
        // and the holdings table sorts by name then holder, so it no longer drives display anywhere.
        //
        // PUBLIC so a test can check it against the catalog: a typo here moves rows onto a card that
        // does not list the item, or onto no card at all, and SQL in a migration is run once against
        // real data with nothing watching. ChartPopItemMigrationTests is that check.
        public static readonly (string OldBoss, string Item, string NewBoss)[] Moves =
        {
            // The gems and seasonal stones, from the god they pop to the farm NM that drops them.
            ("Suzaku", "Gem of the South", "Brigandish Blade"),
            ("Suzaku", "Summerstone",      "Faust"),
            ("Genbu",  "Gem of the North", "Zipacna"),
            ("Genbu",  "Winterstone",      "Olla Grande"),
            ("Seiryu", "Gem of the East",  "Steam Cleaner"),
            ("Seiryu", "Springstone",      "Mother Globe"),
            ("Byakko", "Gem of the West",  "Despot"),
            ("Byakko", "Autumnstone",      "Ullikummi"),

            // The seals, from Kirin (which they pop) to the god that drops them.
            ("Kirin",  "Seal of Suzaku",   "Suzaku"),
            ("Kirin",  "Seal of Genbu",    "Genbu"),
            ("Kirin",  "Seal of Seiryu",   "Seiryu"),
            ("Kirin",  "Seal of Byakko",   "Byakko"),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (oldBoss, item, newBoss) in Moves)
            {
                Move(migrationBuilder, from: oldBoss, item: item, to: newBoss);
            }
        }

        // A true inverse: every move is one-to-one, and no row that was already on the target card
        // can be caught by it — a Gem of the South on Brigandish Blade could only have got there
        // through Up (or by being added after it), and either way Suzaku is where the old shape
        // filed it.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (oldBoss, item, newBoss) in Moves)
            {
                Move(migrationBuilder, from: newBoss, item: item, to: oldBoss);
            }
        }

        // ILIKE with no wildcards is case-insensitive equality that respects collation — the same
        // comparison AltCharacterValidator uses. Rows written before NormalizePopItemName existed can
        // differ in case, and a case-sensitive '=' would silently leave those behind.
        //
        // Board is matched EXACTLY: 'Sky' is what ChartBoardCatalog.NormalizeBoard writes on every
        // path in, and matching it loosely would let a board added later share these item names.
        private static void Move(MigrationBuilder migrationBuilder, string from, string item, string to)
        {
            migrationBuilder.Sql($@"
                UPDATE ""ChartPopItems""
                SET ""Boss"" = '{to}'
                WHERE ""Board"" = 'Sky'
                  AND ""Boss"" ILIKE '{from}'
                  AND ""ItemName"" ILIKE '{item}';");
        }
    }
}
