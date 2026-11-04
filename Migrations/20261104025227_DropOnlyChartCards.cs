using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class DropOnlyChartCards : Migration
    {
        // Data-only. Cards now say which kinds they record, and a drop-only card has no Pop section
        // and no pop button - so a Pop row left on one would sit under a heading nothing can add to.
        // An edit never changes Kind, so they are turned into drops here:
        //
        //   * Sea - EVERY card is drop-only now, so every Pop row on the board.
        //   * Sky - the ten cards that take no pop item. Brigandish Blade, Olla Grande and Ullikummi
        //     keep their pop rows (Curtana, Ro'Maeve Water, Diorite).
        //
        // PUBLIC so ChartPopItemMigrationTests can hold the Sky list against the catalog.
        public static readonly string[] SkyDropOnlyCards =
        {
            "Faust", "Zipacna", "Steam Cleaner", "Mother Globe", "Despot",
            "Suzaku", "Genbu", "Seiryu", "Byakko", "Kirin",
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""ChartPopItems""
                SET ""Kind"" = 'Drop'
                WHERE ""Board"" = 'Sea'
                  AND ""Kind"" = 'Pop';");

            // ILIKE for the reason the Refile migrations give: older rows can differ in case.
            foreach (var boss in SkyDropOnlyCards)
            {
                migrationBuilder.Sql($@"
                    UPDATE ""ChartPopItems""
                    SET ""Kind"" = 'Drop'
                    WHERE ""Board"" = 'Sky'
                      AND ""Boss"" ILIKE '{boss}'
                      AND ""Kind"" = 'Pop';");
            }
        }

        // Nothing to undo with any accuracy: once Up has run, a row it flipped and a row that was
        // added as a drop look the same. Leaving them Drop is what the catalog says they are anyway.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
