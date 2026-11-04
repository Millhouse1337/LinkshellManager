using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class HenmPopItemsByTier : Migration
    {
        // Data-only. HENM pop items belong to a whole TIER - one set opens every NM in it - so they
        // are now filed under the tier's own entry ("Tier 1") instead of an NM card, and the NM cards
        // take no items at all. Rows written under an NM's name move to that NM's tier, or they would
        // sit on a card that no longer shows an item table.
        //
        // Item requests (ChartWishlistRequests) are NOT touched: a request still names the NM it
        // wants loot from.
        //
        // Credits ride along: ChartPopItemCredits keys on ChartPopItemId.
        //
        // PUBLIC so ChartPopItemMigrationTests can check every NM lands on its own tier's entry.
        public static readonly (string Nm, string Tier)[] Moves =
        {
            ("Ruinous Rocs",          "Tier 1"),
            ("Despotic Decapod",      "Tier 1"),
            ("Sacred Scorpions",      "Tier 1"),
            ("Mammet-9999",           "Tier 2"),
            ("Tonberry Sovereign",    "Tier 2"),
            ("Ultimega",              "Tier 2"),
            ("Primordial Behemoth",   "Tier 3"),
            ("Minogame",              "Tier 3"),
            ("IO",                    "Tier 3"),
            ("Council of the Zilart", "Tier 4"),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ILIKE for the reason the Refile migrations give: older rows can differ in case.
            foreach (var (nm, tier) in Moves)
            {
                migrationBuilder.Sql($@"
                    UPDATE ""ChartPopItems""
                    SET ""Boss"" = '{tier}'
                    WHERE ""Board"" = 'HENM'
                      AND ""Boss"" ILIKE '{nm}';");
            }
        }

        // Nothing to undo with any accuracy: three NMs share each tier, so once Up has run there is
        // no telling which NM a tier's row came from.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
