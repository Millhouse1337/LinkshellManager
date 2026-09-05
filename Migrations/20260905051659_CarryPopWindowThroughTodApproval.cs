using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// Lets a queued ToD keep the spawn window it popped on.
    ///
    /// A member without ToD-manage rights posts through the approval queue, and the queue had
    /// nowhere to put the window — so the addon's ToD Tracker stamped it, sent it, and it was
    /// dropped on the floor for exactly the people least able to re-enter it by hand. The
    /// submission carries it now, and approval copies it onto the ToD.
    ///
    /// Nullable with no default: every row already queued predates the column and genuinely has no
    /// reading, which is what null means everywhere else this value travels.
    /// </summary>
    public partial class CarryPopWindowThroughTodApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PopWindow",
                table: "PendingTodSubmissions",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PopWindow",
                table: "PendingTodSubmissions");
        }
    }
}
