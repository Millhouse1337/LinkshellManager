using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class ReclassifySkyChartItems : Migration
    {
        // Data-only. Sky used to list everything that falls off a card as that card's POP items; it
        // now splits them: pop items are what is traded TO a card, drop items are what falls OFF it.
        // A row's Kind picks which list its edit form offers and which heading it sits under on the
        // card, and an update never changes Kind - so the rows written under the old shape have to be
        // moved here or they would sit under "Pop" forever.
        //
        // Two changes, both taken from ChartBoardCatalog.SkyFarmNmCards and SkyGodCards:
        //
        //   1. The twelve items that are drops now - each farm NM's gem or stone and each god's seal -
        //      go from Pop to Drop on the card they already sit on.
        //   2. Curtana moves from Suzaku to Brigandish Blade, which it is traded to. It stays Pop.
        //
        // Only Pop rows are touched in either step. A Drop row of one of these names was typed into
        // the drop form on purpose, and nothing here should second-guess it.
        //
        // Credits ride along untouched: ChartPopItemCredits keys on ChartPopItemId, and no row is
        // created or deleted.
        //
        // PUBLIC so ChartPopItemMigrationTests can check both lists against the catalog: a typo here
        // reclassifies nothing, silently, on the one run this migration ever gets.
        public static readonly (string Boss, string Item)[] NowDrops =
        {
            // The gems and seasonal stones, on the farm NM that drops them.
            ("Brigandish Blade", "Gem of the South"),
            ("Faust",            "Summerstone"),
            ("Zipacna",          "Gem of the North"),
            ("Olla Grande",      "Winterstone"),
            ("Steam Cleaner",    "Gem of the East"),
            ("Mother Globe",     "Springstone"),
            ("Despot",           "Gem of the West"),
            ("Ullikummi",        "Autumnstone"),

            // The seals, on the god that drops them.
            ("Suzaku",           "Seal of Suzaku"),
            ("Genbu",            "Seal of Genbu"),
            ("Seiryu",           "Seal of Seiryu"),
            ("Byakko",           "Seal of Byakko"),
        };

        public static readonly (string OldBoss, string Item, string NewBoss) CurtanaMove =
            ("Suzaku", "Curtana", "Brigandish Blade");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (boss, item) in NowDrops)
            {
                SetKind(migrationBuilder, boss, item, from: "Pop", to: "Drop");
            }

            var (oldBoss, curtana, newBoss) = CurtanaMove;
            MovePopRow(migrationBuilder, from: oldBoss, item: curtana, to: newBoss);
        }

        // An inverse for every row Up touched. The one thing it cannot tell apart is a Drop row of one
        // of these twelve names that was ALREADY Drop before Up ran - Down makes that Pop too, which is
        // what the old shape would have called it anyway.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var (oldBoss, curtana, newBoss) = CurtanaMove;
            MovePopRow(migrationBuilder, from: newBoss, item: curtana, to: oldBoss);

            foreach (var (boss, item) in NowDrops)
            {
                SetKind(migrationBuilder, boss, item, from: "Drop", to: "Pop");
            }
        }

        // ILIKE with no wildcards is case-insensitive equality, the same comparison the Refile
        // migrations use: rows written before NormalizePopItemName existed can differ in case. Board
        // is matched exactly, for the reason given there.
        private static void SetKind(MigrationBuilder migrationBuilder, string boss, string item, string from, string to)
        {
            migrationBuilder.Sql($@"
                UPDATE ""ChartPopItems""
                SET ""Kind"" = '{to}'
                WHERE ""Board"" = 'Sky'
                  AND ""Boss"" ILIKE '{boss}'
                  AND ""ItemName"" ILIKE '{item}'
                  AND ""Kind"" = '{from}';");
        }

        private static void MovePopRow(MigrationBuilder migrationBuilder, string from, string item, string to)
        {
            migrationBuilder.Sql($@"
                UPDATE ""ChartPopItems""
                SET ""Boss"" = '{to}'
                WHERE ""Board"" = 'Sky'
                  AND ""Boss"" ILIKE '{from}'
                  AND ""ItemName"" ILIKE '{item}'
                  AND ""Kind"" = 'Pop';");
        }
    }
}
