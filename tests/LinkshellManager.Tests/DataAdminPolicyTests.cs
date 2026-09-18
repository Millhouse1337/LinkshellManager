using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// DataAdminPolicy is the only thing standing between "the engine can edit any column of any
// table" and the app's real invariants: ledgers are derived history, Linkshell deletes wipe a
// tenant, IsSuperAdmin is seeded from configuration, dropdown columns must stay inside their
// const vocabularies. These tests pin that the shipped policy says what it is meant to say and
// that its option lists cannot drift from the const classes they mirror.
public class DataAdminPolicyTests
{
    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DataAdminCatalog NewCatalog(DataAdminPolicy? policy = null)
    {
        using var db = NewInMemoryContext();
        return new DataAdminCatalog(db.Model, policy ?? DataAdminPolicy.Default);
    }

    private static DataAdminModel Table(DataAdminCatalog catalog, Type entityType) =>
        catalog.FindByClrType(entityType)
        ?? throw new InvalidOperationException($"{entityType.Name} is not in the catalog.");

    private static IReadOnlyList<string> Options(DataAdminCatalog catalog, Type entityType, string column) =>
        Table(catalog, entityType).FindColumn(column)?.Options
        ?? throw new InvalidOperationException($"{entityType.Name}.{column} has no option list.");

    [Fact]
    public void EveryPolicyEntry_IsACatalogTable()
    {
        var catalog = NewCatalog();

        foreach (var entityType in DataAdminPolicy.Default.Entries.Keys)
        {
            Assert.NotNull(catalog.FindByClrType(entityType));
        }
    }

    [Fact]
    public void Flags_DriveCapabilities()
    {
        var catalog = NewCatalog();

        foreach (var ledger in new[] { typeof(DkpLedgerEntry), typeof(JournalEntry), typeof(JournalEntryLine), typeof(LedgerPeriod), typeof(AuctionHistory), typeof(AppUserEventHistory), typeof(AppUserEventStatusLedger) })
        {
            Assert.True(Table(catalog, ledger).IsReadOnly, $"{ledger.Name} should be read-only.");
        }

        var linkshell = Table(catalog, typeof(Linkshell));
        Assert.False(linkshell.CanDelete);
        Assert.True(linkshell.CanEdit);
        Assert.True(linkshell.CanCreate);

        var appUser = Table(catalog, typeof(AppUser));
        Assert.False(appUser.CanCreate);
        Assert.False(appUser.CanDelete);
        Assert.True(appUser.CanEdit);

        var banner = Table(catalog, typeof(LinkshellBanner));
        Assert.False(banner.CanCreate);
        Assert.False(banner.CanEdit);
        Assert.True(banner.CanDelete);

        var rule = Table(catalog, typeof(Rule));
        Assert.True(rule.CanCreate && rule.CanEdit && rule.CanDelete);
    }

    [Fact]
    public void AppUser_EditableSet_ExcludesSuperAdminAndLoginColumns()
    {
        var editable = Table(NewCatalog(), typeof(AppUser)).EditableColumns.Select(c => c.Name).ToList();

        Assert.Contains(nameof(AppUser.CharacterName), editable);
        Assert.Contains(nameof(AppUser.IsPlaceholder), editable);
        Assert.DoesNotContain(nameof(AppUser.IsSuperAdmin), editable);
        Assert.DoesNotContain(nameof(AppUser.Email), editable);
        Assert.DoesNotContain(nameof(AppUser.UserName), editable);
        Assert.DoesNotContain(nameof(AppUser.PasswordHash), editable);
    }

    // Drift guard: the dropdowns must equal the const classes they mirror, so adding a rank or a
    // theme never needs a second edit here and a typo in the policy cannot invent a value.
    [Fact]
    public void OptionLists_MatchTheirConstClasses()
    {
        var catalog = NewCatalog();

        Assert.Equal(DataAdminOptions.FromConsts(typeof(LinkshellRanks)), Options(catalog, typeof(AppUserLinkshell), nameof(AppUserLinkshell.Rank)));
        Assert.Contains(LinkshellRanks.Trial, Options(catalog, typeof(AppUserLinkshell), nameof(AppUserLinkshell.Rank)));
        Assert.Equal(HnmAttendanceModes.All, Options(catalog, typeof(Linkshell), nameof(Linkshell.HnmAttendanceMode)));
        Assert.Equal(EventBoardThemes.All.Select(theme => theme.Key), Options(catalog, typeof(Linkshell), nameof(Linkshell.EventBoardTheme)));
        Assert.Equal(DkpPoolAccents.All, Options(catalog, typeof(DkpPool), nameof(DkpPool.Accent)));
        Assert.Equal(DataAdminOptions.FromConsts(typeof(WindowEventStatuses)), Options(catalog, typeof(WindowEvent), nameof(WindowEvent.Status)));
        Assert.Contains(WindowEventStatuses.Archived, Options(catalog, typeof(WindowEvent), nameof(WindowEvent.Status)));
        Assert.Equal(WindowEventEntryTypes.All, Options(catalog, typeof(WindowEvent), nameof(WindowEvent.EntryType)));
        Assert.Equal(DataAdminOptions.FromConsts(typeof(ChartItemKinds)), Options(catalog, typeof(ChartPopItem), nameof(ChartPopItem.Kind)));
        Assert.Contains(ChartItemKinds.Drop, Options(catalog, typeof(ChartPopItem), nameof(ChartPopItem.Kind)));
        Assert.Equal(ChartWishlistStatuses.All, Options(catalog, typeof(ChartWishlistRequest), nameof(ChartWishlistRequest.Status)));
        Assert.Equal(AttendanceSnapshotSlotKinds.All, Options(catalog, typeof(AttendanceSnapshot), nameof(AttendanceSnapshot.SlotKind)));
        Assert.Contains(AttendanceSnapshotStatuses.Active, Options(catalog, typeof(AttendanceSnapshot), nameof(AttendanceSnapshot.SnapshotStatus)));
        Assert.Contains(AttendanceSnapshotStatuses.Ignored, Options(catalog, typeof(AttendanceSnapshot), nameof(AttendanceSnapshot.SnapshotStatus)));
        Assert.Equal(DiscordChannelPurposes.All, Options(catalog, typeof(LinkshellDiscordChannel), nameof(LinkshellDiscordChannel.Purpose)));
        Assert.Equal(DataAdminOptions.FromConsts(typeof(JournalEntryStatuses)), Options(catalog, typeof(JournalEntry), nameof(JournalEntry.Status)));
        Assert.Contains(JournalEntryStatuses.Confirmed, Options(catalog, typeof(JournalEntry), nameof(JournalEntry.Status)));
    }

    [Fact]
    public void FromConsts_ReadsPublicConstStrings_InDeclarationOrder()
    {
        Assert.Equal(new[] { "Leader", "Officer", "Member", "Trial" }, DataAdminOptions.FromConsts(typeof(LinkshellRanks)));
        Assert.Throws<InvalidOperationException>(() => DataAdminOptions.FromConsts(typeof(DataAdminDefaults)));
    }

    [Fact]
    public void ReadOnlyColumn_IsListedButNeverEditable()
    {
        var table = Table(NewCatalog(), typeof(DkpPoolEventType));

        Assert.Contains(table.DetailColumns, c => c.Name == nameof(DkpPoolEventType.NormalizedEventType));
        Assert.DoesNotContain(table.EditableColumns, c => c.Name == nameof(DkpPoolEventType.NormalizedEventType));
        Assert.Contains(table.EditableColumns, c => c.Name == nameof(DkpPoolEventType.EventType));
    }

    // Keys the database generates are never typed in; keys the caller supplies (AppSetting.Key)
    // are asked for on create and locked on edit.
    [Fact]
    public void CreateColumns_IncludeSuppliedKeys_AndEditColumnsNeverDo()
    {
        var catalog = NewCatalog();

        Assert.Contains(Table(catalog, typeof(AppSetting)).CreateColumns, c => c.Name == "Key");
        Assert.DoesNotContain(Table(catalog, typeof(AppSetting)).EditableColumns, c => c.Name == "Key");
        Assert.DoesNotContain(Table(catalog, typeof(Rule)).CreateColumns, c => c.Name == nameof(Rule.Id));
    }

    [Fact]
    public void ProtectedColumns_FromPolicy_JoinTheGlobalRule()
    {
        var catalog = NewCatalog();

        Assert.True(Table(catalog, typeof(AddonApiToken)).FindColumn(nameof(AddonApiToken.TokenHash))?.IsProtected);
        Assert.True(Table(catalog, typeof(AppUser)).FindColumn(nameof(AppUser.PasswordHash))?.IsProtected);
        Assert.True(Table(catalog, typeof(AppUser)).FindColumn(nameof(AppUser.ProfileImage))?.IsProtected);
        Assert.False(Table(catalog, typeof(AppUser)).FindColumn(nameof(AppUser.CharacterName))?.IsProtected);
    }

    // A Hidden table is excluded from every capability, and nothing outside the policy can undo that.
    [Fact]
    public void HiddenFlag_RemovesEveryCapability()
    {
        var policy = new DataAdminPolicy(new Dictionary<Type, DataAdminEntityPolicy>
        {
            [typeof(Rule)] = new() { Flags = DataAdminPolicyFlags.Hidden },
        });
        var rule = Table(NewCatalog(policy), typeof(Rule));

        Assert.True(rule.IsHidden);
        Assert.False(rule.CanCreate);
        Assert.False(rule.CanEdit);
        Assert.False(rule.CanDelete);
    }

    [Fact]
    public void UnlistedTables_DefaultToFullCrud()
    {
        var table = Table(NewCatalog(), typeof(LinkshellRole));

        Assert.False(DataAdminPolicy.Default.Entries.ContainsKey(typeof(LinkshellRole)));
        Assert.True(table.CanCreate && table.CanEdit && table.CanDelete);
        Assert.Null(table.Policy.DiscordSideEffect);
    }

    [Fact]
    public void DiscordSideEffects_AreDeclaredForTheTablesSaveChangesWatches()
    {
        var catalog = NewCatalog();

        foreach (var watched in new[] { typeof(Tod), typeof(DkpPool), typeof(DkpPoolEventType), typeof(Event), typeof(EventHistory), typeof(Auction), typeof(Bid), typeof(TodLootDetail), typeof(EventLootDetail) })
        {
            Assert.False(string.IsNullOrWhiteSpace(Table(catalog, watched).Policy.DiscordSideEffect), $"{watched.Name} needs a Discord side-effect note.");
        }
    }

    // A membership's DKP balance and ledger watermarks are only ever moved by DkpLedgerWriter,
    // which keeps them in step with the ledger; a raw edit here would desync them.
    [Fact]
    public void MembershipDkpColumns_AreShownButNeverEditable()
    {
        var table = Table(NewCatalog(), typeof(AppUserLinkshell));
        var locked = new[]
        {
            nameof(AppUserLinkshell.LinkshellDkp), nameof(AppUserLinkshell.SeededDkpEarned), nameof(AppUserLinkshell.SeededDkpSpent),
            nameof(AppUserLinkshell.DkpSeedLedgerId), nameof(AppUserLinkshell.DkpPoolLedgerFromId),
        };

        foreach (var name in locked)
        {
            Assert.Contains(table.DetailColumns, c => c.Name == name);
            Assert.DoesNotContain(table.EditableColumns, c => c.Name == name);
        }
        Assert.Contains(table.EditableColumns, c => c.Name == nameof(AppUserLinkshell.Rank));
    }

    [Fact]
    public async Task BeforeSaveHook_NormalisesDkpPoolEventType()
    {
        var hook = Table(NewCatalog(), typeof(DkpPoolEventType)).Policy.BeforeSave
            ?? throw new InvalidOperationException("DkpPoolEventType needs a BeforeSave hook.");
        var row = new DkpPoolEventType { EventType = "  Dynamis " };
        using var db = NewInMemoryContext();

        await hook(db, row, CancellationToken.None);

        Assert.Equal("DYNAMIS", row.NormalizedEventType);
    }
}
