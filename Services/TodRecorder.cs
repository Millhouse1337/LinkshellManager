using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LinkshellManagerDiscordApp.Services;

// What one recorded Time of Death says. The caller has already checked who is asking and whether
// the input makes sense; anything left null here is filled from the linkshell's Monster Setups.
//
// MonsterName / Hq: as the caller has them -- "Nidhogg", "Fafnir" + HQ ticked, or the combined
//                   "Fafnir/Nidhogg". The recorder stores every merge pair the same way.
// TimeUtc:          null only for End Camp's "nobody saw it die", which still gets a row.
// AdditionalSeconds: null = the monster's setup.
public sealed record TodRecordInput(
    int LinkshellId,
    string? MonsterName,
    bool? Hq,
    DateTime? TimeUtc,
    int? AdditionalSeconds,
    int? PopWindow,
    bool? Claim,
    bool? Killed,
    int? DayNumber,
    string Source,
    string? ActorAppUserId)
{
    // Blank = the monster's setup.
    public string? Cooldown { get; init; }

    // Stored as given; each surface has its own rule for a blank one.
    public string? Interval { get; init; }

    // Already checked to be one of our uploads.
    public string? ImagePath { get; init; }

    // A repop someone typed (a reviewer approving a member's ToD). Null = time + cooldown + seconds.
    public DateTime? RepopUtcOverride { get; init; }

    // The addon sends the previous ToD's day; a live camp for the spawn knows the real one
    // (HnmDayCycle.DayForAddonTodAsync).
    public bool DayFromLiveCamp { get; init; }
}

// Tod is the saved row: a new one, or the one already saved for this kill when MergedIntoExisting.
public sealed record TodRecordResult(Tod Tod, bool MergedIntoExisting, DateTime? RepopUtc);

// Where a ToD came from, for the log.
public static class TodSources
{
    public const string App = "app";
    public const string Addon = "addon";
    public const string Approval = "approval";
    public const string EndCamp = "end-camp";
}

// The one way a Time of Death is recorded (backend-04). The app's Log ToD, the addon's ToD post,
// approving a member's submission and End Camp each used to write the Tod row themselves, and the
// copies drifted: the app skipped the one-ToD-per-kill check, approval saved an HQ kill as the NQ
// and dropped the seconds, and only the app moved a parked board to the new repop.
//
// RecordAsync, in order:
//   1. takes a lock for this linkshell + spawn, so two posts of one kill take turns;
//   2. stores a merge pair under its combined name, with Hq saying which half died;
//   3. a kill already saved within TodSameKill.Tolerance is the same kill: the new answers are added
//      to that row and nothing else happens;
//   4. otherwise saves a new row, repop = time + cooldown + Additional Seconds;
//   5. then, for a new row only, resets the party sign-ups for the monster and moves any parked
//      board to the new repop.
public sealed class TodRecorder
{
    private readonly ApplicationDbContext _db;
    private readonly MonsterTimingResolver _monsterTimings;
    private readonly ILogger<TodRecorder> _logger;

    public TodRecorder(ApplicationDbContext db, MonsterTimingResolver monsterTimings, ILogger<TodRecorder> logger)
    {
        _db = db;
        _monsterTimings = monsterTimings;
        _logger = logger;
    }

    public async Task<TodRecordResult> RecordAsync(TodRecordInput input, CancellationToken cancellationToken)
    {
        var rawName = string.IsNullOrWhiteSpace(input.MonsterName) ? null : input.MonsterName.Trim();
        var (monsterName, hq) = Normalize(rawName, input.Hq ?? false);
        var storedName = string.IsNullOrEmpty(monsterName) ? null : monsterName;

        // Read before the lock: nothing below depends on them changing, and the lock should be
        // held for as short a time as possible.
        var (cooldown, additionalSeconds) = await ResolveTimingAsync(input, rawName, cancellationToken);
        var repopUtc = input.RepopUtcOverride
            ?? (input.TimeUtc is { } timeUtc ? RepopFor(timeUtc, cooldown, additionalSeconds) : null);
        var dayNumber = input.DayFromLiveCamp && rawName is not null
            ? await HnmDayCycle.DayForAddonTodAsync(_db, input.LinkshellId, rawName, input.DayNumber, cancellationToken)
            : input.DayNumber;
        var popWindow = input.PopWindow is > 0 ? input.PopWindow : null;

        Tod tod;
        var merged = false;
        // Two posts of one kill a moment apart (two characters at the kill, an officer re-logging what
        // the addon already sent, End Camp pressed twice) each looked for the other's row before
        // either had saved, found nothing, and both saved one. The lock makes the second wait until
        // the first has committed, so its lookup finds the first's row. Held until the transaction
        // ends; when the caller already has one open, that is the caller's commit. In-memory tests
        // have neither, and run one post at a time.
        //
        // The saves inside our own transaction are not counted as committed by ConcurrencyRetry. That
        // is safe here: re-running a record finds the row this one saved and merges into it.
        var relational = _db.Database.IsRelational();
        IDbContextTransaction? ownTransaction = null;
        try
        {
            if (relational)
            {
                if (_db.Database.CurrentTransaction is null)
                {
                    ownTransaction = await _db.Database.BeginTransactionAsync(cancellationToken);
                }
                var (linkshellKey, spawnKey) = LockKeys(input.LinkshellId, storedName);
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock({linkshellKey}, {spawnKey})", cancellationToken);
            }

            var existing = input.TimeUtc is { } killedAtUtc
                ? await TodSameKill.FindAsync(_db, input.LinkshellId, storedName, killedAtUtc, cancellationToken)
                : null;
            if (existing is not null)
            {
                // The first post's time and repop stand: they are already showing everywhere.
                var changed = TodSameKill.Merge(existing, input.Claim, popWindow);
                if (existing.ImagePath is null && !string.IsNullOrWhiteSpace(input.ImagePath))
                {
                    // A screenshot nobody else sent would otherwise sit on disk with nothing showing it.
                    existing.ImagePath = input.ImagePath.Trim();
                    changed = true;
                }
                if (changed)
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
                tod = existing;
                merged = true;
            }
            else
            {
                tod = new Tod
                {
                    LinkshellId = input.LinkshellId,
                    MonsterName = storedName,
                    Hq = hq,
                    DayNumber = dayNumber,
                    PopWindow = popWindow,
                    Claim = input.Claim,
                    Killed = input.Killed,
                    AdditionalSeconds = additionalSeconds,
                    Time = input.TimeUtc,
                    Cooldown = cooldown,
                    RepopTime = repopUtc,
                    Interval = string.IsNullOrWhiteSpace(input.Interval) ? null : input.Interval.Trim(),
                    TimeStamp = DateTime.UtcNow,
                    ImagePath = string.IsNullOrWhiteSpace(input.ImagePath) ? null : input.ImagePath.Trim(),
                };
                _db.Tods.Add(tod);
                await _db.SaveChangesAsync(cancellationToken);
            }

            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync();
            }
        }

        if (merged)
        {
            // Nothing else runs for a kill that was already saved. In particular not the sign-up
            // reset, which would wipe anyone who signed up for the next pop since the first post.
            _logger.LogInformation(
                "ToD from {Source} for '{Monster}' in linkshell {LinkshellId} matched ToD {TodId} for the same kill.",
                input.Source, storedName, input.LinkshellId, tod.Id);
            return new TodRecordResult(tod, true, tod.RepopTime);
        }

        // A new ToD = a new pop window, so the old roster on any party setup for this monster is for
        // the pop that just happened. Reset under the name it was posted with too: a setup assigned
        // to "Fafnir" would not match the stored "Fafnir/Nidhogg".
        foreach (var name in new[] { rawName, storedName }.Where(n => n is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await PartySetupSignupReset.ClearSignupsForMonsterAsync(_db, tod.LinkshellId, name, cancellationToken);
        }

        // A board parked waiting to re-post keeps advertising the old pop time until it re-posts.
        // Re-point it at this ToD's repop.
        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(_db, tod.LinkshellId, tod.MonsterName, cancellationToken);

        _logger.LogInformation(
            "ToD {TodId} recorded from {Source} by {Actor} for '{Monster}' in linkshell {LinkshellId}, repop {Repop:o}.",
            tod.Id, input.Source, input.ActorAppUserId ?? "(none)", storedName, input.LinkshellId, tod.RepopTime);
        return new TodRecordResult(tod, false, tod.RepopTime);
    }

    // A merge pair is stored under its COMBINED name, with Hq carrying which half it was.
    //
    // The addon posts the half that died ("Nidhogg"), the camp boards write their combined name, and
    // the app's picker held either. ResolveClaimHalf is what makes collapsing them lossless: a name
    // that is the HQ half alone IS an HQ answer even when no toggle was touched, and that is written
    // to Hq before the name collapses. Non-pair monsters come back untouched.
    public static (string Name, bool Hq) Normalize(string? monsterName, bool hq)
    {
        var (_, isHq, hasHqVariant) = HnmConfig.ResolveClaimHalf(monsterName, hq);
        return hasHqVariant
            ? (HnmConfig.ClaimGroupName(monsterName), isHq)
            : (monsterName?.Trim() ?? string.Empty, hq);
    }

    // Repop = time of death + cooldown + Additional Seconds. Everything that works out a repop for a
    // new ToD uses this.
    public static DateTime RepopFor(DateTime timeUtc, string? cooldown, int additionalSeconds) =>
        timeUtc
            .AddHours(ActivityWindowEventsController.ResolveTodCooldownHours(cooldown))
            .AddSeconds(Math.Max(0, additionalSeconds));

    // The cooldown label and seconds this ToD uses: what the caller sent, else the monster's setup.
    // Looked up under the name the caller had, as every path did before this class existed.
    private async Task<(string Cooldown, int AdditionalSeconds)> ResolveTimingAsync(
        TodRecordInput input, string? rawName, CancellationToken cancellationToken)
    {
        var hasCooldown = !string.IsNullOrWhiteSpace(input.Cooldown);
        if (hasCooldown && input.AdditionalSeconds is { } given)
        {
            return (input.Cooldown!.Trim(), Math.Max(0, given));
        }

        var timing = await _monsterTimings.ResolveAsync(input.LinkshellId, rawName, cancellationToken);
        return (
            hasCooldown ? input.Cooldown!.Trim() : TodDurationFormat.Format(timing.CooldownMinutes),
            Math.Max(0, input.AdditionalSeconds ?? timing.AdditionalSeconds));
    }

    // pg_advisory_xact_lock(int, int): the linkshell, and a hash of the spawn's stored name, so every
    // spelling of one spawn ("Fafnir", "Nidhogg", "Fafnir/Nidhogg") takes the same lock. FNV-1a
    // rather than string.GetHashCode, which changes every time the app starts.
    internal static (int LinkshellKey, int SpawnKey) LockKeys(int linkshellId, string? storedName)
    {
        var text = (storedName ?? string.Empty).Trim().ToLowerInvariant();
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in text)
            {
                hash ^= ch;
                hash *= 16777619u;
            }
            return (linkshellId, (int)hash);
        }
    }
}
