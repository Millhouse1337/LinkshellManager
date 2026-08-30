using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// One desired monster row from a save request (Activity or web). Id is null for a row the client
// just added. Durations arrive as the value + unit the officer actually typed and are normalized to
// canonical minutes in exactly one place — here — so the two surfaces cannot disagree about whether
// "1" meant an hour or a minute.
public sealed record MonsterTimingEdit(
    int? Id,
    string? MonsterName,
    int? Windows,
    double? CadenceValue,
    string? CadenceUnit,
    double? CooldownValue,
    string? CooldownUnit,
    string? Category,
    // Null = leave the stored value as it is. See ActivityMonsterTimingInput.
    bool? ClaimShieldEnabled = null);

// Shared save logic for a linkshell's monster setups: full replace with validation, used by both
// the Activity API and the web Customize page so there is exactly one implementation of "save the
// monster setups".
public sealed class MonsterTimingEditor
{
    private readonly ApplicationDbContext _db;
    private readonly LinkshellMonsterTimingProvisioner _provisioner;
    private readonly MonsterTimingResolver _resolver;

    // A cadence beyond a day stops being a spawn grid and starts being a data-entry slip.
    private const int MaxCadenceMinutes = 24 * 60;
    // 100 days. Generous enough for anything in the game, tight enough that a stray keypress in
    // the minutes unit can't push a repop past the end of the ToD tracker.
    private const int MaxCooldownMinutes = 100 * 24 * 60;

    public MonsterTimingEditor(
        ApplicationDbContext db,
        LinkshellMonsterTimingProvisioner provisioner,
        MonsterTimingResolver resolver)
    {
        _db = db;
        _provisioner = provisioner;
        _resolver = resolver;
    }

    // Persists the desired rows. Returns null on success, or a human-readable error.
    public async Task<string?> SaveAsync(
        int linkshellId,
        IReadOnlyList<MonsterTimingEdit> edits,
        CancellationToken cancellationToken)
    {
        // Seed first, so a linkshell saving before it ever loaded the editor still ends up with the
        // full catalog rather than only the rows the client happened to post.
        await _provisioner.EnsureSeededAsync(linkshellId, cancellationToken);

        var existing = await _db.LinkshellMonsterTimings
            .Where(row => row.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        var named = new List<(MonsterTimingEdit Edit, string Name)>();
        foreach (var edit in edits)
        {
            var name = edit.MonsterName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                // A blank name on an existing row is a delete; on a new row it is an empty form row.
                continue;
            }
            if (name.Length > 128)
            {
                return $"\"{name[..40]}…\" is too long for a monster name.";
            }
            // Pop-only mobs come from an item, not a repop timer, so they have no cooldown to set.
            // The old blob editor filtered these on both read and write; keep that contract.
            if (HnmConfig.MonsterSegments(name).Any(HnmConfig.PopOnlyNms.Contains))
            {
                return $"{name} pops from an item instead of a repop timer, so it has no cooldown to set.";
            }
            named.Add((edit, name));
        }

        // Two rows must not claim ONE spawn. The unique index only catches identical normalized
        // names; "Nidhogg" beside "Fafnir/Nidhogg" are different strings for the same monster, and
        // would make every lookup depend on which row the alias index happened to see first.
        var claimedAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, name) in named)
        {
            foreach (var alias in HnmConfig.MonsterMatchNames(name))
            {
                if (claimedAliases.TryGetValue(alias, out var owner)
                    && !owner.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{owner} and {name} are the same spawn — keep one row for them.";
                }
                claimedAliases[alias] = name;
            }
        }

        var now = DateTime.UtcNow;
        var keptIds = new HashSet<int>();
        var sortOrder = 0;

        foreach (var (edit, editedName) in named)
        {
            var name = editedName;
            var row = edit.Id is int id ? existing.FirstOrDefault(candidate => candidate.Id == id) : null;
            if (row is null)
            {
                row = new LinkshellMonsterTiming
                {
                    LinkshellId = linkshellId,
                    // A row the client invented is custom by definition — a built-in always arrives
                    // with the Id it was seeded under.
                    IsCustom = true,
                    CreatedAtUtc = now,
                };
                _db.LinkshellMonsterTimings.Add(row);
            }
            else
            {
                keptIds.Add(row.Id);
                // A built-in's name is not the client's to change; letting it through would orphan
                // the row from the catalog it was seeded from.
                if (!row.IsCustom)
                {
                    name = row.MonsterName;
                }
            }

            row.MonsterName = name;
            row.NormalizedMonsterName = MonsterTimingDefaults.Normalize(name);
            row.WindowCount = NormalizeWindows(edit.Windows);
            row.WindowCadenceMinutes = NormalizeCadence(edit);
            row.CooldownMinutes = NormalizeCooldown(edit, name);
            row.Category = MonsterTimingDefaults.NormalizeCategory(edit.Category);
            // Only when the client actually sent one. An older client omits the field entirely, and
            // a full-replace save that read null as false would switch Claim Shield off for every
            // monster the first time anyone touched the editor.
            if (edit.ClaimShieldEnabled is { } claimShield)
            {
                row.ClaimShieldEnabled = claimShield;
            }
            row.SortOrder = sortOrder++;
            row.UpdatedAtUtc = now;
        }

        foreach (var row in existing.Where(candidate => !keptIds.Contains(candidate.Id)))
        {
            // A built-in row is RESET, not removed: it is part of the catalog, and deleting it would
            // just make it reappear on the next seed with its defaults anyway. Only a monster the
            // linkshell added itself can actually go.
            if (!row.IsCustom)
            {
                return $"{row.MonsterName} is a built-in monster — reset its values instead of removing it.";
            }
            _db.LinkshellMonsterTimings.Remove(row);
        }

        await _db.SaveChangesAsync(cancellationToken);
        // Drop the memo so anything resolving timings later in this same request sees the new rows.
        _resolver.Invalidate(linkshellId);
        return null;
    }

    // Blank = "this monster has no spawn grid", which is a real answer and not the same as 1.
    // HnmConfig.MaxWindow is a hard ceiling everywhere downstream (the board, the attendance post
    // count, the addon's clamping), so a bigger number would be silently truncated rather than
    // honoured — clamp it here where it can still be seen.
    private static int? NormalizeWindows(int? windows) =>
        windows is null or <= 0 ? null : Math.Min(windows.Value, HnmConfig.MaxWindow);

    private static int? NormalizeCadence(MonsterTimingEdit edit)
    {
        if (edit.CadenceValue is not { } value || value <= 0)
        {
            return null;
        }
        var minutes = TodDurationFormat.FromValueAndUnit(value, edit.CadenceUnit);
        return minutes <= 0 ? null : Math.Min(minutes, MaxCadenceMinutes);
    }

    // Cooldown is required — every monster on the ToD tracker repops. A blank falls back to the
    // built-in default rather than to zero, which would make RepopTime equal to the time of death.
    private static int NormalizeCooldown(MonsterTimingEdit edit, string monsterName)
    {
        if (edit.CooldownValue is not { } value || value <= 0)
        {
            return MonsterTimingDefaults.DefaultCooldownMinutes(monsterName);
        }
        var minutes = TodDurationFormat.FromValueAndUnit(value, edit.CooldownUnit);
        return minutes <= 0
            ? MonsterTimingDefaults.DefaultCooldownMinutes(monsterName)
            : Math.Min(minutes, MaxCooldownMinutes);
    }
}
