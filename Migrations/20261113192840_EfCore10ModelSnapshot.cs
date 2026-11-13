using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Changes nothing in the database on purpose. Moving to EF Core 10 changed how the model
    // snapshot DESCRIBES the jsonb array columns (JobLevels, MeritJobs, RelicNames, ...): EF 10
    // calls an int[] / string[] property a "primitive collection" stored as JSON text, where EF 8
    // called it a plain property. The column type (jsonb) and the stored JSON ([75,0,37]) are the
    // same. This migration only carries that refreshed snapshot, so the next real migration
    // starts from an EF 10 snapshot instead of mixing this rename into its own diff.
    /// <inheritdoc />
    public partial class EfCore10ModelSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: see the note on the class.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: see the note on the class.
        }
    }
}
