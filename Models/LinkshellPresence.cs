using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// Who is currently in the world, per linkshell, as reported by the addons that are running.
//
// WHY THIS HAS TO EXIST. The FFXI client can only see your OWN alliance — party memory slots 0-17.
// Two alliances at one camp are completely invisible to each other, which is stated on
// AttendanceSnapshot.AllianceNumber and is the reason attendance is posted per alliance in the first
// place. So a client can never draw "the other alliances in my linkshell" from the game. The only
// way it can exist is for each addon to report its own alliance and for the server to merge them.
//
// This is a CACHE OF THE PRESENT, not a record. Nothing here is evidence of attendance, nothing here
// pays DKP, and every row is rewritten by the next heartbeat or aged out. Attendance still comes
// from AttendanceSnapshot and AppUserEventWindow, and must keep coming from there.
//
// Deliberately NOT stored: HP/MP/TP, target, coordinates. None of it feeds a roster, and all of it
// would turn a presence cache into a player-tracking surface.
public class LinkshellPresence
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    // The character standing in the world. Together with LinkshellId this is the upsert key — a
    // character is in exactly one place at a time, whoever reported it.
    [MaxLength(256)]
    public string CharacterName { get; set; } = string.Empty;

    // The account behind that character, when the name resolves to one on the roster. Null for a
    // name the linkshell does not know, which is filtered out before it ever gets here.
    [MaxLength(450)]
    public string? AppUserId { get; set; }

    // Their roster main, when CharacterName is one of their alts. Same display-hint role it plays
    // on AppUserEventWindow.
    [MaxLength(256)]
    public string? MainCharacterName { get; set; }

    // FFXI zone id, verbatim from IParty:GetMemberZone. An ID, not a name: the addon owns the
    // zone table (resources.attZoneList) and the server has no copy worth keeping in sync.
    //
    // Reported PER MEMBER, not per batch — party slots 0-17 include people who are in other zones,
    // which is exactly why the addon's own gather filters on it.
    public int? ZoneId { get; set; }

    // Which alliance this character is in, and the identity it was recognised by. The number is an
    // ordinal; the key is the leader's character name where the game confirms one. See
    // AllianceIdentityService for why the number stopped being typed by hand.
    public int AllianceNumber { get; set; }

    [MaxLength(256)]
    public string? AllianceKey { get; set; }

    // True ONLY when the reporting client's game confirmed this character leads the alliance
    // (IParty:GetAllianceLeaderServerId matched to their slot). Never inferred, never defaulted —
    // the UI shows a leader marker only where this is true, and shows nothing at all otherwise.
    public bool IsAllianceLeader { get; set; }

    [MaxLength(8)]
    public string? MainJob { get; set; }

    public int? MainJobLevel { get; set; }

    [MaxLength(8)]
    public string? SubJob { get; set; }

    public int? SubJobLevel { get; set; }

    // Whose addon reported this row. Not the same as CharacterName: one poster reports their whole
    // alliance, so seventeen of these rows name somebody else. Kept for the "who is feeding this"
    // question when a roster looks wrong.
    [MaxLength(256)]
    public string? ReportedByCharacterName { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

// How long a presence row counts as "still here", and how long before it is swept.
public static class LinkshellPresenceWindow
{
    // 2.5 heartbeats at the addon's 60-second cadence. One dropped beat — a zone, a lag spike, a
    // curl timeout — must not blink a whole alliance off somebody's Lobby; two consecutive misses
    // should. A tighter cutoff makes the roster flicker; a looser one leaves ghosts standing in a
    // camp everyone left.
    public const int FreshSeconds = 150;

    // Rows older than this are deleted opportunistically on the next write for that linkshell.
    // No background service: presence is worthless the moment it is stale, so the only thing that
    // needs to happen is that the table does not grow forever.
    public const int PurgeMinutes = 30;

    // Party memory cannot report a nineteenth person, so anything larger is a client bug rather
    // than a bigger alliance. Mirrors the snapshot ingest cap for the same reason.
    public const int MaxMembers = 18;
}
