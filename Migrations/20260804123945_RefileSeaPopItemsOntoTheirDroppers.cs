using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RefileSeaPopItemsOntoTheirDroppers : Migration
    {
        // Data-only, and the twin of RefileSkyPopItemsOntoTheirDroppers. The Sea board used to file an
        // item under the boss it POPS; it now files it under the boss it DROPS FROM, which is what let
        // the six trash families join the board as cards of their own. Rows written under the old
        // shape sit on a card whose picker no longer offers them — nothing breaks (both surfaces keep
        // an unlisted name as an "already on this row" option), but the per-boss ledger fractions
        // would say something the board no longer means.
        //
        // Fifteen moves, and that is EVERY item the board now lists: unlike Sky, where Curtana was
        // already filed on the god that drops it, nothing on Sea was in the right place beforehand.
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
            // The organs and chips, from the NM they pop to the trash family that drops them. These
            // are the six that had NO home on the old board — the card they sat on was the thing they
            // were spent on, so an officer holding four Xzomit organs filed them under the Jailer.
            ("Jailer of Fortitude", "Ghrah M Chip",              "Ghrah"),
            ("Jailer of Justice",   "High-Quality Xzomit Organ", "Xzomit"),
            ("Ix'aern (MNK)",       "High-Quality Aern Organ",   "Aern"),
            ("Jailer of Hope",      "High-Quality Phuabo Organ", "Phuabo"),
            ("Jailer of Faith",     "High-Quality Euvhi Organ",  "Euvhi"),
            ("Jailer of Prudence",  "High-Quality Hpemde Organ", "Hpemde"),

            // The first-stage virtues and deeds, from the second-stage Jailer they pop to the NM that
            // drops them.
            ("Jailer of Justice",   "Second Virtue",       "Jailer of Fortitude"),
            ("Jailer of Justice",   "Deed of Moderation",  "Ix'aern (DRK)"),
            ("Jailer of Hope",      "First Virtue",        "Jailer of Temperance"),
            ("Jailer of Hope",      "Deed of Placidity",   "Ix'aern (MNK)"),
            ("Jailer of Prudence",  "Third Virtue",        "Jailer of Faith"),
            ("Jailer of Prudence",  "Deed of Sensibility", "Ix'aern (DRG)"),

            // The second-stage virtues, from Jailer of Love (which they pop) to the Jailer that drops
            // them. Love keeps nothing, exactly as Kirin does at the foot of Sky.
            ("Jailer of Love",      "Fourth Virtue", "Jailer of Justice"),
            ("Jailer of Love",      "Fifth Virtue",  "Jailer of Hope"),
            ("Jailer of Love",      "Sixth Virtue",  "Jailer of Prudence"),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (oldBoss, item, newBoss) in Moves)
            {
                Move(migrationBuilder, from: oldBoss, item: item, to: newBoss);
            }
        }

        // A true inverse: every move is one-to-one on the ITEM — no item name appears twice in the
        // list — so a row cannot be caught by a second move on the way back, and the two moves that
        // both touch Ix'aern (MNK) carry different items and so cannot see each other. A row already
        // on a target card could only have got there through Up (or by being added after it), and
        // either way the old shape filed it where Down puts it.
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
        // differ in case, and a case-sensitive '=' would silently leave those behind. Nothing on this
        // board contains a % or _, so nothing here is a wildcard by accident.
        //
        // Board is matched EXACTLY: 'Sea' is what ChartBoardCatalog.NormalizeBoard writes on every
        // path in, and matching it loosely would let a board added later share these item names.
        private static void Move(MigrationBuilder migrationBuilder, string from, string item, string to)
        {
            migrationBuilder.Sql($@"
                UPDATE ""ChartPopItems""
                SET ""Boss"" = '{Literal(to)}'
                WHERE ""Board"" = 'Sea'
                  AND ""Boss"" ILIKE '{Literal(from)}'
                  AND ""ItemName"" ILIKE '{Literal(item)}';");
        }

        // Sea is the board with apostrophes in its boss names — Ix'aern (DRK) and its two siblings.
        // Sky had none, so its twin interpolates raw; here an unescaped name closes the SQL string
        // literal early and the migration fails to parse on the one run it ever gets.
        private static string Literal(string value) => value.Replace("'", "''");
    }
}
