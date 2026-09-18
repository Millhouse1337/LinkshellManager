using System.Reflection;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;

namespace LinkshellManagerDiscordApp.Services;

[Flags]
public enum DataAdminPolicyFlags
{
    None = 0,
    // Never selectable on the chooser and never routable.
    Hidden = 1,
    NoCreate = 2,
    NoEdit = 4,
    NoDelete = 8,
    ReadOnly = NoCreate | NoEdit | NoDelete,
}

// A denormalised copy kept in step on save: TargetColumn on this entity is overwritten with
// SourceColumn of the row that ViaForeignKey points at (Rule.LinkshellName <- Linkshell.LinkshellName),
// exactly as the hand-written controllers do (Controllers/RuleController.cs Create).
public sealed record DataAdminSyncedCopy(string TargetColumn, string ViaForeignKey, string SourceColumn);

// What EF metadata cannot tell us about one table. Everything here narrows what the generic
// engine would otherwise allow; a runtime choice (the chooser) can never widen it.
public sealed record DataAdminEntityPolicy
{
    public DataAdminPolicyFlags Flags { get; init; }
    // Row label, coalesced in order. Null = guess from DataAdminDefaults.LabelGuessOrder.
    public IReadOnlyList<string>? LabelColumns { get; init; }
    // ALLOW-list of editable columns. Null = every non-key, non-protected, non-read-only column.
    public IReadOnlyList<string>? EditableAllowList { get; init; }
    // Column -> dropdown values.
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Options { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    // Never listed nor edited, on top of the global rule (credentials).
    public IReadOnlySet<string> ProtectedColumns { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    // Listed but never edited (derived values BeforeSave recomputes).
    public IReadOnlySet<string> ReadOnlyColumns { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    // Banner text for the edit/delete pages when SaveChanges will post to Discord.
    public string? DiscordSideEffect { get; init; }
    public IReadOnlyList<DataAdminSyncedCopy> SyncedCopies { get; init; } = Array.Empty<DataAdminSyncedCopy>();
    // Runs after binding and synced copies, before SaveChanges (create and edit).
    public Func<ApplicationDbContext, object, CancellationToken, Task>? BeforeSave { get; init; }

    public static readonly DataAdminEntityPolicy FullCrud = new();
}

// The code-side layer over the auto-discovered catalog: which tables are read-only or cannot be
// created/deleted, which columns are credentials, which string columns are really dropdowns, and
// which saves ripple into Discord. Keyed by typeof(X) so a wrong entity is a compile error;
// column names are checked against the model when the catalog is built (DataAdminModel ctor).
public sealed class DataAdminPolicy
{
    public static readonly DataAdminPolicy Default = new(DefaultEntries());

    public DataAdminPolicy(IReadOnlyDictionary<Type, DataAdminEntityPolicy> entries)
    {
        Entries = entries;
    }

    public IReadOnlyDictionary<Type, DataAdminEntityPolicy> Entries { get; }

    public DataAdminEntityPolicy For(Type entityType) =>
        Entries.TryGetValue(entityType, out var policy) ? policy : DataAdminEntityPolicy.FullCrud;

    // The global protected-column rule. Type-based rather than column-type-based so it behaves the
    // same on the InMemory test model (which has no relational annotations): every jsonb column in
    // this app is an int[]/string[] property and both bytea columns are byte[].
    public static bool IsProtected(Type entityType, PropertyInfo property, DataAdminEntityPolicy policy) =>
        property.PropertyType == typeof(byte[])
        || property.PropertyType.IsArray
        || (typeof(IdentityUser).IsAssignableFrom(entityType) && DataAdminDefaults.IdentityInternals.Contains(property.Name))
        || policy.ProtectedColumns.Contains(property.Name);

    private static Dictionary<Type, DataAdminEntityPolicy> DefaultEntries()
    {
        var readOnly = new DataAdminEntityPolicy { Flags = DataAdminPolicyFlags.ReadOnly };
        var linkshellNameCopy = new[] { new DataAdminSyncedCopy("LinkshellName", "LinkshellId", nameof(Linkshell.LinkshellName)) };

        return new Dictionary<Type, DataAdminEntityPolicy>
        {
            // ---- append-only history: balances and attendance are DERIVED from these rows ----------
            [typeof(DkpLedgerEntry)] = readOnly with
            {
                DiscordSideEffect = "Changing DKP ledger entries reposts the linkshell's DKP sheet to Discord.",
            },
            [typeof(JournalEntry)] = readOnly with
            {
                Options = Opts(
                    (nameof(JournalEntry.Kind), DataAdminOptions.FromConsts(typeof(JournalEntryKinds))),
                    (nameof(JournalEntry.Status), DataAdminOptions.FromConsts(typeof(JournalEntryStatuses))),
                    (nameof(JournalEntry.Source), DataAdminOptions.FromConsts(typeof(JournalEntrySources))),
                    (nameof(JournalEntry.TransactionKind), TreasuryTransactionKinds.All.Select(kind => kind.Key).ToArray())),
            },
            [typeof(JournalEntryLine)] = readOnly,
            [typeof(LedgerPeriod)] = readOnly,
            [typeof(AuctionHistory)] = readOnly with
            {
                DiscordSideEffect = "Changing a settled auction reposts its Discord card.",
            },
            [typeof(AppUserEventHistory)] = readOnly,
            [typeof(AppUserEventStatusLedger)] = readOnly,

            // ---- tenant roots and identity ---------------------------------------------------------
            [typeof(EventHistory)] = new()
            {
                Flags = DataAdminPolicyFlags.NoCreate | DataAdminPolicyFlags.NoDelete,
                DiscordSideEffect = "Saving a past event can post its event-ended summary to Discord.",
            },
            [typeof(Linkshell)] = new()
            {
                Flags = DataAdminPolicyFlags.NoDelete,
                Options = Opts(
                    (nameof(Linkshell.HnmAttendanceMode), HnmAttendanceModes.All),
                    (nameof(Linkshell.EventBoardTheme), EventBoardThemes.All.Select(theme => theme.Key).ToArray())),
                ProtectedColumns = Names(nameof(Linkshell.GoogleOAuthRefreshTokenEnc)),
            },
            [typeof(AppUser)] = new()
            {
                Flags = DataAdminPolicyFlags.NoCreate | DataAdminPolicyFlags.NoDelete,
                LabelColumns = new[] { nameof(AppUser.CharacterName), nameof(AppUser.UserName), nameof(AppUser.Email) },
                // IsSuperAdmin is seeded from configuration only (Program.cs); Email/UserName drive login.
                EditableAllowList = new[]
                {
                    nameof(AppUser.CharacterName), nameof(AppUser.AltCharacterName1), nameof(AppUser.AltCharacterName2),
                    nameof(AppUser.TimeZone), nameof(AppUser.PrimaryLinkshellId), nameof(AppUser.PrimaryLinkshellName),
                    nameof(AppUser.IsPlaceholder),
                },
            },
            [typeof(AppUserLinkshell)] = new()
            {
                // The DKP epoch watermark is stamped on insert (ApplicationDbContext), so rows are born in the app.
                Flags = DataAdminPolicyFlags.NoCreate,
                Options = Opts((nameof(AppUserLinkshell.Rank), DataAdminOptions.FromConsts(typeof(LinkshellRanks)))),
                // The balance and its ledger watermarks are only ever moved by DkpLedgerWriter, which keeps
                // them in step with the ledger (INV-1 in ApplicationDbContext); shown here, never edited.
                ReadOnlyColumns = Names(
                    nameof(AppUserLinkshell.LinkshellDkp), nameof(AppUserLinkshell.SeededDkpEarned), nameof(AppUserLinkshell.SeededDkpSpent),
                    nameof(AppUserLinkshell.DkpSeedLedgerId), nameof(AppUserLinkshell.DkpPoolLedgerFromId)),
            },
            [typeof(DiscordActivityUser)] = new()
            {
                Flags = DataAdminPolicyFlags.NoCreate,
                LabelColumns = new[] { nameof(DiscordActivityUser.GlobalName), nameof(DiscordActivityUser.Username) },
            },

            // ---- DKP pools -------------------------------------------------------------------------
            [typeof(DkpPool)] = new()
            {
                // Dependents disagree on delete (NoAction, SetNull and Cascade) and the ledger points at
                // pools through an unenforced int, so a pool is retired in the app, never deleted here.
                Flags = DataAdminPolicyFlags.NoDelete,
                Options = Opts((nameof(DkpPool.Accent), DkpPoolAccents.All)),
                DiscordSideEffect = "Changing a DKP pool reposts the linkshell's DKP sheet to Discord.",
            },
            [typeof(DkpPoolEventType)] = new()
            {
                ReadOnlyColumns = Names(nameof(DkpPoolEventType.NormalizedEventType)),
                BeforeSave = (_, entity, _) =>
                {
                    if (entity is DkpPoolEventType row)
                    {
                        row.NormalizedEventType = DkpPoolEventType.Normalize(row.EventType);
                    }
                    return Task.CompletedTask;
                },
                DiscordSideEffect = "Changing an event-type assignment reposts the linkshell's DKP sheet to Discord.",
            },

            // ---- rows whose SaveChanges posts to Discord -----------------------------------------
            [typeof(Tod)] = new() { DiscordSideEffect = "Saving or deleting a ToD rebuilds the linkshell's Discord ToD board." },
            [typeof(TodLootDetail)] = new() { DiscordSideEffect = "Adding loot posts a loot announcement to Discord." },
            [typeof(EventLootDetail)] = new() { DiscordSideEffect = "Adding loot posts a loot announcement to Discord." },
            [typeof(Event)] = new() { DiscordSideEffect = "Saving an event posts or edits its Discord board; deleting it removes the Discord message." },
            [typeof(Auction)] = new() { DiscordSideEffect = "Saving an auction posts or edits its Discord card; deleting it removes the card." },
            [typeof(Bid)] = new() { DiscordSideEffect = "Saving a bid updates the auction's Discord card." },

            // ---- credentials -----------------------------------------------------------------------
            [typeof(AddonApiToken)] = new()
            {
                Flags = DataAdminPolicyFlags.NoCreate,
                ProtectedColumns = Names(nameof(AddonApiToken.TokenHash)),
                EditableAllowList = new[] { nameof(AddonApiToken.Label), nameof(AddonApiToken.RevokedAt) },
                LabelColumns = new[] { nameof(AddonApiToken.Label), nameof(AddonApiToken.TokenPrefix) },
            },
            [typeof(AddonPairingCode)] = new()
            {
                Flags = DataAdminPolicyFlags.NoCreate,
                ProtectedColumns = Names(nameof(AddonPairingCode.Code)),
            },
            [typeof(LinkshellDiscordWebhook)] = new()
            {
                Flags = DataAdminPolicyFlags.NoCreate,
                ProtectedColumns = Names(nameof(LinkshellDiscordWebhook.Url)),
            },
            [typeof(LinkshellBanner)] = new()
            {
                Flags = DataAdminPolicyFlags.NoCreate | DataAdminPolicyFlags.NoEdit,
            },

            // ---- dropdowns and labels --------------------------------------------------------------
            [typeof(ChartPopItem)] = new()
            {
                Options = Opts((nameof(ChartPopItem.Kind), DataAdminOptions.FromConsts(typeof(ChartItemKinds)))),
            },
            [typeof(ChartWishlistRequest)] = new()
            {
                Options = Opts((nameof(ChartWishlistRequest.Status), ChartWishlistStatuses.All)),
            },
            [typeof(WindowEvent)] = new()
            {
                Options = Opts(
                    (nameof(WindowEvent.Status), DataAdminOptions.FromConsts(typeof(WindowEventStatuses))),
                    (nameof(WindowEvent.EntryType), WindowEventEntryTypes.All)),
            },
            [typeof(LinkshellDiscordChannel)] = new()
            {
                Options = Opts((nameof(LinkshellDiscordChannel.Purpose), DiscordChannelPurposes.All)),
            },
            [typeof(AttendanceSnapshot)] = new()
            {
                Options = Opts(
                    (nameof(AttendanceSnapshot.SlotKind), AttendanceSnapshotSlotKinds.All),
                    (nameof(AttendanceSnapshot.SnapshotStatus), DataAdminOptions.FromConsts(typeof(AttendanceSnapshotStatuses)))),
            },
            [typeof(PartySetupSlot)] = new()
            {
                Options = Opts((nameof(PartySetupSlot.RequirementType), PartySetupSlotRequirementTypes.All)),
            },
            [typeof(Invite)] = new()
            {
                LabelColumns = new[] { nameof(Invite.DiscordDisplayName), nameof(Invite.Status) },
            },
            [typeof(Notification)] = new()
            {
                LabelColumns = new[] { nameof(Notification.NotificationType) },
            },

            // ---- denormalised LinkshellName copies ------------------------------------------------
            [typeof(Rule)] = new() { SyncedCopies = linkshellNameCopy },
            [typeof(Announcement)] = new() { SyncedCopies = linkshellNameCopy },
            [typeof(Item)] = new() { SyncedCopies = linkshellNameCopy },
            [typeof(RevenueEntry)] = new() { SyncedCopies = linkshellNameCopy },
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Opts(params (string Column, IReadOnlyList<string> Values)[] pairs) =>
        pairs.ToDictionary(pair => pair.Column, pair => pair.Values, StringComparer.Ordinal);

    private static IReadOnlySet<string> Names(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);
}
