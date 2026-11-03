using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class ReclassifySkyPopItems : Migration
    {
        // Data-only. The other half of ReclassifySkyChartItems, which turned Sky's drops into Drop
        // rows but left every Drop row alone. Before Sky split its lists, a card's drop form was free
        // text, so a pop item could be typed in there - Diorite went onto Ullikummi as a Drop and sat
        // under "Drop Item" next to Autumnstone. An edit never changes Kind, so it has to happen here.
        //
        // Flips exactly the rows whose item is on that card's POP list and whose Kind says Drop.
        //
        // PUBLIC so ChartPopItemMigrationTests can check it against the catalog.
        public static readonly (string Boss, string Item)[] NowPops =
        {
            ("Brigandish Blade", "Curtana"),
            ("Olla Grande",      "Ro'Maeve Water"),
            ("Ullikummi",        "Diorite"),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (boss, item) in NowPops)
            {
                migrationBuilder.Sql($@"
                    UPDATE ""ChartPopItems""
                    SET ""Kind"" = 'Pop'
                    WHERE ""Board"" = 'Sky'
                      AND ""Boss"" ILIKE '{Literal(boss)}'
                      AND ""ItemName"" ILIKE '{Literal(item)}'
                      AND ""Kind"" = 'Drop';");
            }
        }

        // Nothing to undo with any accuracy: once Up has run, a row it flipped and a row that was
        // added as Pop look the same. Leaving them Pop is what the catalog says they are anyway.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }

        // Ro'Maeve Water carries an apostrophe, which would close the SQL string literal early. Same
        // escaping RefileSeaPopItemsOntoTheirDroppers uses for Ix'aern.
        private static string Literal(string value) => value.Replace("'", "''");
    }
}
