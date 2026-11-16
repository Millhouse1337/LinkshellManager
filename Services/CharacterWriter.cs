using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace LinkshellManagerDiscordApp.Services;

// THE ONE WAY TO CHANGE A CHARACTER (Round 5, R5.5).
//
// An account's characters (slot 0 the main, 1 and 2 the alts) live in the Characters table, and
// that table is the only place names, jobs and crafts are written. Every writer (the profile, the
// addon, the Discord sign-in and sign-up, placeholder create and claim) loads a CharacterSet here,
// changes it, and saves the context as usual. The save hook (ApplicationDbContext
// .StampAccountsFromCharactersAsync) then copies the main's name onto the account's CharacterName
// (the account's display name). Nothing else is copied: the old AspNetUsers alt, job and craft
// columns were dropped by Round5Contract (R5.7).
//
// Rules:
// - names are trimmed; a blank name removes that slot's row (and its jobs and crafts with it);
// - a change of capitals only updates the name in place;
// - any other rename removes the row and adds a new one that keeps the slot's jobs and crafts.
//   NormalizedName is never updated in place: with only deletes and inserts, EF runs the deletes
//   first, so a main and an alt can swap names in one save without tripping the one-name-per-account
//   index (IX_Characters_AppUserId_NormalizedName);
// - one account never holds a name twice (SetName refuses it; TrySetName skips it);
// - names are at most MaxNameLength characters (see MaxNameLength).
//
// Two saves of one account: when the caller has a transaction open (BeginTransactionAsync), LoadAsync
// first takes a per-account advisory lock, so the second waits for the first to commit and then
// reads its rows. Without one (the InMemory test database, or a caller that does not need it) rows
// are simply loaded.
public static class CharacterWriter
{
    // The longest name, kept while a rollback can still swap in the update B build (R5.5): its
    // rollback SQL (Round5Contract.RollbackSql) re-adds AltCharacterName1/2 as varchar(64) and copies
    // every name into them, and that build keeps copying on every save, so a longer name saved now
    // would make the rollback fail. It can go up to Character.MaxNameLength one deploy after R5.7.
    // (The game's own limit is 15, CharacterNameFormat.MaxLength.)
    public const int MaxNameLength = 64;

    // pg_advisory_xact_lock(class, key) class for "changing this account's characters", the same class
    // the old copy (CharacterSync) used. Negative, so it never meets TodRecorder's locks (whose class
    // is a linkshell id).
    public const int LockClassId = -1128808786;

    // Opens a transaction when the database has them and none is open yet, so LoadAsync can take the
    // account's lock and hold it until the caller commits. Null otherwise (nothing to commit then).
    public static async Task<IDbContextTransaction?> BeginTransactionAsync(
        ApplicationDbContext db, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
        {
            return null;
        }
        return await db.Database.BeginTransactionAsync(cancellationToken);
    }

    // The account's characters, tracked on this context, ready to change. Takes the account's lock
    // first when a transaction is open.
    public static async Task<CharacterSet> LoadAsync(
        ApplicationDbContext db, string accountId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("An account id is needed.", nameof(accountId));
        }
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({LockClassId}, hashtext({accountId}))", cancellationToken);
        }
        // Loads the saved rows into the tracker. The set itself reads the tracker, so a row this
        // context already added or removed (and has not saved yet) counts as it is now.
        await db.Characters.Where(character => character.AppUserId == accountId).LoadAsync(cancellationToken);
        return new CharacterSet(db, accountId);
    }

    // A brand-new account (not saved yet): it has no rows, and nobody else can see it, so nothing is
    // loaded or locked. Its rows are saved with it.
    public static CharacterSet ForNewAccount(ApplicationDbContext db, AppUser account)
    {
        if (string.IsNullOrWhiteSpace(account.Id))
        {
            throw new ArgumentException("The new account needs its id.", nameof(account));
        }
        return new CharacterSet(db, account.Id);
    }

    // A name as stored: trimmed, or null when blank.
    public static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    // The lower-case form the lookups and the unique index use.
    public static string Normalize(string name) => name.ToLowerInvariant();

    // Counted the way Postgres counts a varchar's characters (code points).
    public static bool IsTooLong(string name) => name.EnumerateRunes().Count() > MaxNameLength;
}

// One account's three character slots, read from (and changed on) the context's tracker.
public sealed class CharacterSet
{
    private readonly ApplicationDbContext _db;

    internal CharacterSet(ApplicationDbContext db, string accountId)
    {
        _db = db;
        AccountId = accountId;
    }

    public string AccountId { get; }

    // The row in a slot as it is now (saved or not), or null when the slot has no name.
    public Character? this[short slot] => Entry(slot)?.Entity;

    public string? Name(short slot) => this[slot]?.Name;

    // The rows as they are now, slot order.
    public IReadOnlyList<Character> Rows =>
        Live().Select(entry => entry.Entity).OrderBy(character => character.Slot).ToList();

    // Whether a slot other than `exceptSlot` already holds this name (trimmed, ignoring capitals).
    public bool HasName(string? name, short? exceptSlot = null)
    {
        var clean = CharacterWriter.Clean(name);
        if (clean is null)
        {
            return false;
        }
        var normalized = CharacterWriter.Normalize(clean);
        return Live().Any(entry => entry.Entity.Slot != exceptSlot
            && string.Equals(entry.Entity.NormalizedName, normalized, StringComparison.Ordinal));
    }

    // The first alt slot with no name, or null when both alts are taken.
    public short? FirstFreeAltSlot() =>
        this[Character.Alt1Slot] is null ? Character.Alt1Slot
        : this[Character.Alt2Slot] is null ? Character.Alt2Slot
        : null;

    // Sets one slot's name. Returns whether anything changed. Refuses (throws) a name another slot of
    // this account holds, or one longer than CharacterWriter.MaxNameLength: callers check first (the
    // profile through CharacterNameValidator, system paths through TrySetName).
    public bool SetName(short slot, string? name)
    {
        CheckSlot(slot);
        var clean = CharacterWriter.Clean(name);
        if (clean is not null)
        {
            CheckLength(clean);
            if (HasName(clean, exceptSlot: slot))
            {
                throw new InvalidOperationException($"This account already has a character named \"{clean}\".");
            }
        }
        return Apply(slot, clean);
    }

    // For the system paths (placeholder create and claim, Discord sign-in, the addon): sets the name
    // unless another slot already holds it or it is too long, in which case nothing changes and false
    // is returned. A blank name is skipped too: these paths never clear a name.
    public bool TrySetName(short slot, string? name)
    {
        CheckSlot(slot);
        var clean = CharacterWriter.Clean(name);
        if (clean is null || CharacterWriter.IsTooLong(clean) || HasName(clean, exceptSlot: slot))
        {
            return false;
        }
        return Apply(slot, clean);
    }

    // Sets all three names at once (the profile). A main and an alt may swap names: only the final
    // three are checked against each other. Returns whether anything changed.
    public bool SetNames(string? main, string? alt1, string? alt2)
    {
        var names = new[] { CharacterWriter.Clean(main), CharacterWriter.Clean(alt1), CharacterWriter.Clean(alt2) };
        foreach (var name in names.OfType<string>())
        {
            CheckLength(name);
        }
        var repeated = names.OfType<string>()
            .GroupBy(CharacterWriter.Normalize, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (repeated is not null)
        {
            throw new InvalidOperationException($"One account cannot have two characters named \"{repeated.First()}\".");
        }
        var changed = false;
        for (short slot = 0; slot < names.Length; slot++)
        {
            changed |= Apply(slot, names[slot]);
        }
        return changed;
    }

    // Sets the slot's jobs: each argument that is not null replaces that list. Nothing happens (and
    // false comes back) when the slot has no name: jobs belong to a character.
    public bool SetJobs(short slot, int[]? jobLevels = null, int[]? strongJobs = null, string[]? meritJobs = null)
    {
        CheckSlot(slot);
        var row = this[slot];
        if (row is null)
        {
            return false;
        }
        var changed = false;
        if (jobLevels is not null && !Same(row.JobLevels, jobLevels)) { row.JobLevels = jobLevels; changed = true; }
        if (strongJobs is not null && !Same(row.StrongJobs, strongJobs)) { row.StrongJobs = strongJobs; changed = true; }
        if (meritJobs is not null && !Same(row.MeritJobs, meritJobs)) { row.MeritJobs = meritJobs; changed = true; }
        return changed;
    }

    // Sets the slot's craft levels (null leaves them alone). False when the slot has no name.
    public bool SetCrafts(short slot, int[]? craftLevels)
    {
        CheckSlot(slot);
        var row = this[slot];
        if (row is null || craftLevels is null || Same(row.CraftLevels, craftLevels))
        {
            return false;
        }
        row.CraftLevels = craftLevels;
        return true;
    }

    // Forgets every change made through this set that is not saved yet (a create that failed).
    public void DiscardChanges()
    {
        foreach (var entry in Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Deleted:
                case EntityState.Modified:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
            }
        }
    }

    private bool Apply(short slot, string? clean)
    {
        var existing = Entry(slot);
        if (clean is null)
        {
            if (existing is null)
            {
                return false;
            }
            // Removing an added row just forgets it; a saved row is deleted.
            _db.Characters.Remove(existing.Entity);
            return true;
        }
        var normalized = CharacterWriter.Normalize(clean);
        if (existing is null)
        {
            _db.Characters.Add(NewRow(slot, clean, normalized, from: null));
            return true;
        }
        var row = existing.Entity;
        if (string.Equals(row.Name, clean, StringComparison.Ordinal))
        {
            return false;
        }
        if (existing.State == EntityState.Added || string.Equals(row.NormalizedName, normalized, StringComparison.Ordinal))
        {
            // Not saved yet, or only the capitals change: the row keeps its place in the index.
            row.Name = clean;
            row.NormalizedName = normalized;
            return true;
        }
        // A new name: delete and insert (see the class comment), keeping the slot's jobs and crafts.
        _db.Characters.Remove(row);
        _db.Characters.Add(NewRow(slot, clean, normalized, from: row));
        return true;
    }

    private Character NewRow(short slot, string name, string normalized, Character? from) => new()
    {
        AppUserId = AccountId,
        Slot = slot,
        Name = name,
        NormalizedName = normalized,
        JobLevels = from?.JobLevels?.ToArray(),
        StrongJobs = from?.StrongJobs?.ToArray(),
        MeritJobs = from?.MeritJobs?.ToArray(),
        CraftLevels = from?.CraftLevels?.ToArray(),
        UpdatedAt = DateTime.UtcNow,
    };

    private IEnumerable<EntityEntry<Character>> Entries() =>
        _db.ChangeTracker.Entries<Character>()
            .Where(entry => string.Equals(entry.Entity.AppUserId, AccountId, StringComparison.Ordinal));

    private IEnumerable<EntityEntry<Character>> Live() =>
        Entries().Where(entry => entry.State is EntityState.Added or EntityState.Unchanged or EntityState.Modified);

    private EntityEntry<Character>? Entry(short slot) => Live().FirstOrDefault(entry => entry.Entity.Slot == slot);

    private static void CheckSlot(short slot)
    {
        if (slot is < Character.MainSlot or > Character.Alt2Slot)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "A character slot is 0, 1 or 2.");
        }
    }

    private static void CheckLength(string name)
    {
        if (CharacterWriter.IsTooLong(name))
        {
            throw new ArgumentException(
                $"Character names can be at most {CharacterWriter.MaxNameLength} characters long.", nameof(name));
        }
    }

    private static bool Same<T>(T[]? a, T[]? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);
}
