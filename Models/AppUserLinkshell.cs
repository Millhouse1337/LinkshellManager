using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AppUserLinkshell
{
    [Key]
    public int Id { get; set; }

    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    // For an UNSYNCED member (a placeholder AppUser, IsPlaceholder=true): the Discord
    // user id of the player it belongs to. Set when they self-register via the Discord
    // board's "you're not synced" modal, so later board signups auto-recognize them by
    // Discord id (no re-typing). Null for real, synced members. See ManualMemberService
    // + DiscordSignupIdentity.ResolveSignupContextAsync.
    [MaxLength(32)]
    public string? DiscordUserId { get; set; }

    // True when the Discord id above can be trusted to mean "this person": an officer set it (the
    // roster's Link action, or approving a claim request), or the sign-up form CREATED this
    // placeholder for that Discord user. Only then does the person's first LSM launch take the
    // placeholder over in place (DiscordIdentityService). The sign-up form used to attach a typed
    // name's Discord id to an existing imported member, which handed that member's DKP to anyone
    // who knew the name; that now asks an officer instead (PlaceholderClaimRequest).
    public bool DiscordIdConfirmed { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    // (A membership has no name of its own. The roster shows the account's CharacterName, the main,
    // and names resolve through the account's Characters. Round5Contract dropped the old roster name.)

    public string? Rank { get; set; }

    // The role this member holds. This id is the source; Rank above is only the role's name, copied
    // on every save (ApplicationDbContext.StampMembershipRoles), so a writer sets one of them and
    // the save fills in the other:
    //   - set the id (or Role), and Rank becomes that role's Name;
    //   - set only Rank, and the id becomes the role whose Name equals the trimmed Rank exactly
    //     (case matters). A Rank that names no role gets the Member role, and Rank says "Member".
    // Required in the database (MembershipRoleRequired), and permissions read this id only. It
    // stays int?, and optional in the EF model, on purpose: a save may set only Rank and let the
    // hook fill the id in, and a row left NULL by a skipped guard must still load (see the
    // relationship in ApplicationDbContext). The foreign key is on (LinkshellId, LinkshellRoleId),
    // so a member can never hold another linkshell's role.
    public int? LinkshellRoleId { get; set; }

    // The role row LinkshellRoleId names (the same two-column foreign key). It lets one save add a
    // linkshell's default roles and point a member at one of them before either has an id.
    public LinkshellRole? Role { get; set; }

    public string? Status { get; set; }

    // Officer override of the computed active-credit streak (the roster "Count").
    // Acts as a SEED/baseline for the activity state machine: the machine starts
    // from this value at ManualStreakSetAt and processes counting events that
    // ended AFTER it, so a manually-set credit accumulates with subsequent
    // attendance instead of being discarded.
    public int? ManualActiveCreditStreak { get; set; }

    // Officer override of the computed ABSENT streak (the roster red "Count").
    // Mutually exclusive with ManualActiveCreditStreak — setting one nulls the
    // other. Same SEED semantics: drives the machine's starting state.
    public int? ManualAbsentStreak { get; set; }

    // When the manual streak seed above was set. The state machine only replays
    // counting events that ended after this instant (events before are superseded
    // by the manual baseline). Null when there's no manual seed.
    public DateTime? ManualStreakSetAt { get; set; }

    public double? LinkshellDkp { get; set; }

    // Postgres's hidden xmin value, which changes every time the row is saved. EF checks it on each
    // save of this row: if someone else saved the member between our read and our save, our save
    // fails instead of quietly overwriting their balance change (two officers paying and charging
    // Bob at the same moment used to leave him at 105 or 97 instead of 102). Not a real column --
    // see Data/ConcurrencyRetry.cs for what happens after the failure.
    public uint Version { get; set; }

    // Lifetime DKP totals seeded from the generic DKP template import. The
    // app's live totals = these seeds + the DkpLedgerEntry rows recorded AFTER
    // DkpSeedLedgerId (the ledger Id watermark at import time). This lets a
    // linkshell migrating in from an external sheet carry its lifetime
    // earned/spent without re-bookkeeping every past transaction, while
    // app-native linkshells (never seeded → all 0) compute totals purely from
    // the ledger. See Services/DkpSheetService (the seeded-vs-ledger totals logic).
    public double SeededDkpEarned { get; set; }

    public double SeededDkpSpent { get; set; }

    public int DkpSeedLedgerId { get; set; }

    // The pool-attribution epoch: ledger rows with Id <= this are NOT attributed to a DKP
    // pool (they collapse into the default pool's residual — see DkpPoolBalanceService).
    // Same idiom as DkpSeedLedgerId above.
    //
    // Why it's needed: a kicked member's DkpLedgerEntry rows SURVIVE the AppUserLinkshells
    // delete (the ledger's only cascade is from Linkshell). Without an epoch, re-inviting
    // them would resurrect their pre-kick earns as spendable pool DKP even though their
    // LinkshellDkp is back to 0. Stamped on every new membership by ApplicationDbContext.
    public int DkpPoolLedgerFromId { get; set; }

    public DateTime? DateJoined { get; set; }

    // (The main character's jobs used to have a copy here too. They live on the account:
    // the account's main Characters row since Round 5. Round4Contract dropped the old copies.)
}
