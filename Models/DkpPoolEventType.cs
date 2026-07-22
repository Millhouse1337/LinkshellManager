using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One event-type -> pool assignment. The PARTITION GUARANTEE is the unique index on
// (LinkshellId, NormalizedEventType): an event type can never sit in two pools, even under
// concurrent officer saves.
//
// An event type with NO row here falls through to the linkshell's default pool — so the
// table is EMPTY for an un-customised linkshell, and free-form custom event types (the web
// event form has an "Other" free-text field) keep working without ever being enumerated.
public class DkpPoolEventType
{
    [Key]
    public int Id { get; set; }

    // Denormalized from the pool so the unique index is per-linkshell with no join.
    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public int DkpPoolId { get; set; }

    [ForeignKey(nameof(DkpPoolId))]
    public DkpPool? DkpPool { get; set; }

    // As typed, so the settings UI can show the officer's own casing.
    [MaxLength(256)]
    public string EventType { get; set; } = string.Empty;

    // Trim().ToUpperInvariant() of EventType. Event.EventType is free-form, so matching has
    // to be case- and whitespace-insensitive; doing it in a stored column keeps the unique
    // index and the remap UPDATE honest without relying on the DB collation.
    [MaxLength(256)]
    public string NormalizedEventType { get; set; } = string.Empty;

    public static string Normalize(string? eventType) => (eventType ?? string.Empty).Trim().ToUpperInvariant();
}
