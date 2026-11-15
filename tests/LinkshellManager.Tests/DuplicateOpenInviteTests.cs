using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LinkshellManager.Tests;

// "Already invited" is the answer only for the two one-open-invite indexes. Invites has a third
// unique index (one open join request), and a rejection anywhere else is a real error.
public class DuplicateOpenInviteTests
{
    private static DbUpdateException Rejection(string sqlState, string? table, string? constraint) =>
        new("save failed", new PostgresException(
            "rejected", "ERROR", "ERROR", sqlState, tableName: table, constraintName: constraint));

    [Theory]
    [InlineData(UniqueRaceRetry.OneOpenInviteIndex)]
    [InlineData(UniqueRaceRetry.OneOpenDiscordInviteIndex)]
    public void A_second_open_invite_is_a_duplicate(string index)
    {
        Assert.True(UniqueRaceRetry.IsDuplicateOpenInvite(
            Rejection(PostgresErrorCodes.UniqueViolation, "Invites", index)));
    }

    [Theory]
    // The open join request index: a different question with its own answer.
    [InlineData(PostgresErrorCodes.UniqueViolation, "Invites", "IX_Invites_OneOpenJoinRequest")]
    // A membership race is answered by IsDuplicateMembership, not here.
    [InlineData(PostgresErrorCodes.UniqueViolation, UniqueRaceRetry.Memberships, "IX_AppUserLinkshells_OneMembershipPerLinkshell")]
    // Same name, but not a unique rejection.
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, "Invites", UniqueRaceRetry.OneOpenInviteIndex)]
    [InlineData(PostgresErrorCodes.UniqueViolation, "Invites", null)]
    public void Anything_else_is_not(string sqlState, string table, string? constraint)
    {
        Assert.False(UniqueRaceRetry.IsDuplicateOpenInvite(Rejection(sqlState, table, constraint)));
    }

    [Fact]
    public void A_save_failure_from_something_other_than_Postgres_is_not()
    {
        Assert.False(UniqueRaceRetry.IsDuplicateOpenInvite(new DbUpdateException("save failed")));
    }
}
