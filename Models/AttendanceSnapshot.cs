using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AttendanceSnapshot
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    [MaxLength(256)]
    public string? CapturedByCharacterName { get; set; }

    [MaxLength(8)]
    public string? UtcOffset { get; set; }

    public int EntryCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    // Optional human-friendly label for the snapshot. Set by the addon via
    // `/lsm now <name>` or edited by an officer in the web UI. Null when
    // the capture was anonymous.
    [MaxLength(128)]
    public string? Name { get; set; }

    // Optional association with an Event. Visual metadata only -- linking
    // does NOT credit attendance against the event. ON DELETE SET NULL so a
    // deleted event leaves the snapshot intact but unlinked.
    public int? LinkedEventId { get; set; }

    [ForeignKey(nameof(LinkedEventId))]
    public Event? LinkedEvent { get; set; }

    // Window Events are snapshot-native groups created from /lsm now posts.
    // They are separate from timed Events so multi-alliance HNM/window checks
    // can be merged, deduped, and reviewed without implying timed DKP credit.
    public int? WindowEventId { get; set; }

    [ForeignKey(nameof(WindowEventId))]
    public WindowEvent? WindowEvent { get; set; }

    // Which spawn window this capture was taken in, measured from its Window Event's grid anchor
    // (WindowEvent.WindowGridAnchorUtc) at the monster's own cadence — 10-minute steps on the
    // 7-window kings/dragons, hourly on the 25-window wyrms. Stamped once at capture, so a snapshot
    // keeps the window it was actually taken in rather than being re-derived later against a grid
    // that may have moved.
    //
    // NULL means "this camp has no window grid" — Sky gods, farm NMs and ad-hoc `/lsm now` posts
    // run no cadence, so there is no window to name. Deliberately distinct from window 1.
    public int? WindowNumber { get; set; }

    // Whether this capture was filed against a numbered WINDOW or as a MISC post — people who
    // were at the camp but outside any window (the classic case: the shell did not claim the pop,
    // but members stayed in zone in case the holding group wiped).
    //
    // This is NOT expressible as `WindowNumber == null`. That null is already load-bearing: it
    // means "this camp runs no window grid" (Sky gods, farm NMs, every camp-handoff roster), and
    // AttendanceSectionsBuilder.MapSnapshot RE-DERIVES a number at read time when it is null.
    // Overloading it would make a misc post indistinguishable from an ungridded camp and would
    // reclassify every existing ungridded snapshot as Misc.
    //
    // SlotKind is orthogonal to the grid. Three states, all of which occur:
    //   Window + a number  — a gridded camp
    //   Window + null      — an ungridded camp (Kirin, farm NMs, camp handoffs)
    //   Misc   + null      — outside any window; Misc always nulls the number
    [MaxLength(16)]
    public string SlotKind { get; set; } = AttendanceSnapshotSlotKinds.Window;

    // Which alliance this capture came from. The FFXI client can only see your OWN alliance
    // (party memory slots 0-17), so two alliances at one camp are completely invisible to each
    // other.
    //
    // DERIVED since 2026-08-29, not typed. It used to be a manual `/lsm alliance N` setting that
    // defaulted to 1 -- so a shell where nobody ran the command rendered every alliance merged into
    // one row, which is the failure this replaced. The number is now assigned server-side from
    // AllianceKey below (first distinct key on the camp becomes 1, the next 2, ...), which keeps
    // every existing number-based query, chip and index working unchanged.
    //
    // Null on rows created before this column existed. Those pre-date per-alliance posting, so
    // they are deliberately left unlabelled rather than being assumed to be alliance 1.
    public int? AllianceNumber { get; set; }

    // WHO this alliance is, as the addon recognised it: the alliance leader's character name where
    // the game confirms one (IParty:GetAllianceLeaderServerId), else the first poster's name.
    //
    // This is the MERGE KEY. Two officers standing in the same alliance compute the same key from
    // their own client, so folding their posts together is an exact string match rather than a bet
    // that both of them typed the same digit. Two alliances compute different keys by construction.
    //
    // Null on legacy rows, which fall back to matching on AllianceNumber.
    [MaxLength(256)]
    public string? AllianceKey { get; set; }

    // The alliance leader's character name, set ONLY when the game actually reported one. Null for
    // a solo player or a party with no alliance formed -- and null is why the UI shows no leader
    // marker rather than guessing at one.
    [MaxLength(256)]
    public string? AllianceLeaderName { get; set; }

    // The account behind the addon token that posted this. CapturedByCharacterName is the
    // in-game character, which does not resolve to an account when the poster is scanning on an
    // alt — this does, so the verification trail names a person rather than a character.
    [MaxLength(450)]
    public string? PostedByAppUserId { get; set; }

    // When an officer confirmed the capture. Stamped immediately for a moderator's own post
    // (they are the reviewer), and on the Confirm action for anyone else's.
    public DateTime? VerifiedAtUtc { get; set; }

    [MaxLength(450)]
    public string? VerifiedByAppUserId { get; set; }

    [MaxLength(32)]
    public string SnapshotStatus { get; set; } = AttendanceSnapshotStatuses.Active;

    // Idempotency stamp for AttInput appends. Set once after a successful
    // append so retries don't duplicate rows in the sheet.
    public DateTime? AttInputAppendedAt { get; set; }

    public ICollection<AttendanceSnapshotEntry> Entries { get; set; } = new List<AttendanceSnapshotEntry>();
}
