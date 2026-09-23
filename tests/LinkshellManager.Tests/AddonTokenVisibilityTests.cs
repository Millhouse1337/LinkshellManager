using System;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace LinkshellManager.Tests;

// WHOSE ADDON PAIRINGS THE GAME ADDON CARD LISTS.
//
// A pairing is a personal credential: it belongs to one member's game client. The card used to
// list every pairing bound to the linkshell being viewed as well, tagged "another member", so
// opening your own settings page showed you other people's devices. Nobody asks their linkshell's
// settings page who else has the addon installed.
//
// Revoking someone else's pairing is untouched as a capability — RevokeTokenAsync still allows it
// with manage rights on that token's linkshell. It is simply not something this listing volunteers.
public class AddonTokenVisibilityTests
{
    private const string Me = "user-me";
    private const string SomeoneElse = "user-them";

    private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AddonApiAuthService NewService(ApplicationDbContext db) =>
        new(db, new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions())));

    private static AddonApiToken Token(
        int id, int linkshellId, string issuedTo, Guid? batch = null, DateTime? revokedAt = null)
        => new()
        {
            Id = id,
            LinkshellId = linkshellId,
            IssuedToAppUserId = issuedTo,
            TokenPrefix = $"att_{id}",
            TokenHash = $"hash-{id}",
            CreatedAt = Now,
            RevokedAt = revokedAt,
            PairingBatchId = batch,
        };

    private static async Task<int[]> ListedIdsAsync(params AddonApiToken[] tokens)
    {
        using var db = NewInMemoryContext();
        // Every linkshell the tokens name has to exist: the listing Includes the navigation so the
        // card can print which linkshells a pairing covers, and a token pointing at nothing would
        // be testing a row the app cannot produce.
        foreach (var linkshellId in tokens.Select(t => t.LinkshellId).Distinct())
        {
            db.Linkshells.Add(new Linkshell { Id = linkshellId, LinkshellName = $"Linkshell {linkshellId}" });
        }
        db.AddonApiTokens.AddRange(tokens);
        await db.SaveChangesAsync();

        var listed = await NewService(db).ListForUserAsync(Me);
        return listed.Select(t => t.Id).OrderBy(id => id).ToArray();
    }

    [Fact]
    public async Task MyOwnPairing_IsListed()
    {
        Assert.Equal(new[] { 1 }, await ListedIdsAsync(Token(1, linkshellId: 5, issuedTo: Me)));
    }

    // THE ONE THIS EXISTS FOR. Same linkshell, somebody else's game client.
    [Fact]
    public async Task AnotherMembersPairing_OnALinkshellIShare_IsNotListed()
    {
        Assert.Equal(
            new[] { 1 },
            await ListedIdsAsync(
                Token(1, linkshellId: 5, issuedTo: Me),
                Token(2, linkshellId: 5, issuedTo: SomeoneElse)));
    }

    [Fact]
    public async Task WhenOnlyOtherMembersHavePaired_TheListIsEmpty()
    {
        Assert.Empty(await ListedIdsAsync(Token(2, linkshellId: 5, issuedTo: SomeoneElse)));
    }

    // One pairing code mints a token per linkshell you belong to, and the card must not read "no
    // active tokens" merely because a different linkshell happens to be selected. So the listing
    // stays UNSCOPED by linkshell — narrowing it to the viewed one is the other way to get this
    // wrong, and it is the reason ListTokensAsync collapses the batch afterwards.
    [Fact]
    public async Task MyPairingIsListed_ForEveryLinkshellItMinted()
    {
        var batch = Guid.NewGuid();
        Assert.Equal(
            new[] { 1, 2, 3 },
            await ListedIdsAsync(
                Token(1, linkshellId: 5, issuedTo: Me, batch: batch),
                Token(2, linkshellId: 6, issuedTo: Me, batch: batch),
                Token(3, linkshellId: 7, issuedTo: Me, batch: batch)));
    }

    // A disconnected pairing is not an active one. Unchanged behaviour, pinned because the Where
    // clause it lives in is what this change edited.
    [Fact]
    public async Task ARevokedPairingOfMine_IsNotListed()
    {
        Assert.Equal(
            new[] { 1 },
            await ListedIdsAsync(
                Token(1, linkshellId: 5, issuedTo: Me),
                Token(2, linkshellId: 5, issuedTo: Me, revokedAt: Now)));
    }
}
