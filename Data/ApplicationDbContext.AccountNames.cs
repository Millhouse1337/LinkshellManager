using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace LinkshellManagerDiscordApp.Data
{
    // Round 5: Characters is the only place names, jobs and crafts are written (CharacterWriter).
    // Every save then copies the result onto the places that still hold a copy:
    //
    // 1. AppUser.CharacterName, the account's display name: always the main's (slot 0) name, or null
    //    when the account has no main. Derived, the same way Rank follows the role.
    // 2. The roster name, AppUserLinkshell.CharacterName: nothing reads it any more, but the build
    //    before still does, so every new membership, every membership that changes account, and every
    //    membership of an account whose main is renamed gets the account's main name.
    //
    // The old AspNetUsers alt, job and craft columns are NOT copied to any more (R5.6): the previous
    // build (R5.5) never reads them, and R5.7 drops them. They keep whatever R5.5 last wrote.
    //
    // Only accounts whose Characters rows change in the save are copied to (their other rows are read
    // from the database). No SQL is run after the save: everything is part of the save itself.
    //
    // Two guards for code that still writes the account columns itself:
    // - an EXISTING account whose CharacterName was changed in a save that does not change its
    //   Characters rows, or whose old columns were changed in any save, gets the old values back, and
    //   a Warning is logged (use CharacterWriter);
    // - a NEW account saved with names in its old columns and no Characters rows gets those names as
    //   its Characters rows (CharacterSync.DesiredRows; names over CharacterWriter.MaxNameLength are
    //   left out), and a Warning is logged. Only tests still create accounts that way.
    public partial class ApplicationDbContext
    {
        // The account's display name: a copy of the main's (slot 0) name.
        private const string MainNameColumn = nameof(AppUser.CharacterName);

        // ROUND5-DROP: the 14 old account alt, job and craft columns, never written since R5.6. The
        // guard below keeps them unwritten; R5.7 drops them, and this list and its guard with them.
        private static readonly IReadOnlyList<string> OldAccountColumns =
            CharacterSync.SourceProperties.Where(column => column != MainNameColumn).ToArray();

        // Sync and async saves share this. With async false every query runs synchronously, so the
        // returned task is already complete (SaveChanges waits on it).
        private async Task StampAccountsFromCharactersAsync(bool async, CancellationToken cancellationToken)
        {
            var userEntries = ChangeTracker.Entries<AppUser>()
                .Where(entry => !string.IsNullOrEmpty(entry.Entity.Id))
                .GroupBy(entry => entry.Entity.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var characterEntries = ChangeTracker.Entries<Character>().ToList();

            await RemoveDeletedAccountsCharactersWithoutSqlAsync(userEntries, async, cancellationToken);
            if (AdoptNewAccountsOldColumns(userEntries, characterEntries))
            {
                characterEntries = ChangeTracker.Entries<Character>().ToList();
            }

            var accountIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in characterEntries.Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                accountIds.Add(entry.Entity.AppUserId);
                if (entry.State != EntityState.Added
                    && entry.Property(nameof(Character.AppUserId)).OriginalValue is string { Length: > 0 } before)
                {
                    accountIds.Add(before);
                }
            }
            // A deleted account takes its characters with it: nothing to copy.
            accountIds.RemoveWhere(id => userEntries.TryGetValue(id, out var user) && user.State == EntityState.Deleted);
            RevertCopiesChangedByHand(userEntries, accountIds);

            var renamedAccounts = new HashSet<string>(StringComparer.Ordinal);
            if (accountIds.Count > 0)
            {
                foreach (var entry in characterEntries.Where(entry => entry.State is EntityState.Added or EntityState.Modified))
                {
                    StampCharacterRow(entry);
                }

                var ids = accountIds.ToList();
                var saved = await ListAsync(Characters.AsNoTracking().Where(character => ids.Contains(character.AppUserId)), async, cancellationToken);
                var untracked = ids.Where(id => !userEntries.ContainsKey(id)).ToList();
                if (untracked.Count > 0)
                {
                    foreach (var user in await ListAsync(Users.Where(user => untracked.Contains(user.Id)), async, cancellationToken))
                    {
                        userEntries[user.Id] = Entry(user);
                    }
                }

                foreach (var id in ids)
                {
                    if (!userEntries.TryGetValue(id, out var account) || account.State is EntityState.Deleted or EntityState.Detached)
                    {
                        continue;
                    }
                    var rows = CharactersNow(id, saved, characterEntries);
                    if (StampMainName(account, rows))
                    {
                        renamedAccounts.Add(id);
                    }
                }
            }

            await StampRosterNamesAsync(renamedAccounts, accountIds, userEntries, async, cancellationToken);
        }

        // A changed row: the name trimmed (and never blank or too long, see CharacterWriter), NormalizedName
        // from it, and UpdatedAt now. A refusal is a DbUpdateException, so callers that show save
        // errors (Data Admin) show it as one.
        private static void StampCharacterRow(EntityEntry<Character> entry)
        {
            var name = CharacterWriter.Clean(entry.Entity.Name);
            if (name is null)
            {
                throw new DbUpdateException("A character name cannot be blank.");
            }
            if (CharacterWriter.IsTooLong(name))
            {
                throw new DbUpdateException($"Character names can be at most {CharacterWriter.MaxNameLength} characters long.");
            }
            SetDerived(entry, nameof(Character.Name), name);
            SetDerived(entry, nameof(Character.NormalizedName), CharacterWriter.Normalize(name));
            entry.Entity.UpdatedAt = DateTime.UtcNow;
        }

        // The account's rows as they will be after this save: the saved rows (read just now), with
        // this save's deletes, edits and inserts applied. An edited row takes only its CHANGED columns
        // from this context; everything else (and every unchanged row) comes from the database, so a
        // context that loaded the rows before another request changed them never copies its old
        // values (a row another request deleted is not brought back).
        private static List<Character> CharactersNow(
            string accountId, List<Character> saved, List<EntityEntry<Character>> tracked)
        {
            var rows = saved
                .Where(row => string.Equals(row.AppUserId, accountId, StringComparison.Ordinal))
                .ToDictionary(row => row.Id);
            var added = new List<Character>();
            foreach (var entry in tracked)
            {
                var belongs = string.Equals(entry.Entity.AppUserId, accountId, StringComparison.Ordinal);
                switch (entry.State)
                {
                    case EntityState.Added:
                        if (belongs) added.Add(entry.Entity);
                        break;
                    case EntityState.Deleted:
                    case EntityState.Detached:
                        rows.Remove(entry.Entity.Id);
                        break;
                    case EntityState.Modified:
                        if (!belongs) rows.Remove(entry.Entity.Id);
                        else rows[entry.Entity.Id] = rows.TryGetValue(entry.Entity.Id, out var row)
                            ? WithChangedColumns(row, entry)
                            : entry.Entity;
                        break;
                }
            }
            return rows.Values.Concat(added).ToList();
        }

        // The saved row with this context's changed columns on top (what the UPDATE will leave).
        private static Character WithChangedColumns(Character saved, EntityEntry<Character> entry)
        {
            T Pick<T>(string column, T savedValue)
            {
                var property = entry.Property(column);
                return property.IsModified ? (T)property.CurrentValue! : savedValue;
            }
            return new Character
            {
                Id = saved.Id,
                AppUserId = saved.AppUserId,
                Slot = Pick(nameof(Character.Slot), saved.Slot),
                Name = Pick(nameof(Character.Name), saved.Name),
                NormalizedName = Pick(nameof(Character.NormalizedName), saved.NormalizedName),
                JobLevels = Pick(nameof(Character.JobLevels), saved.JobLevels),
                StrongJobs = Pick(nameof(Character.StrongJobs), saved.StrongJobs),
                MeritJobs = Pick(nameof(Character.MeritJobs), saved.MeritJobs),
                CraftLevels = Pick(nameof(Character.CraftLevels), saved.CraftLevels),
                UpdatedAt = saved.UpdatedAt,
            };
        }

        // Sets the account's CharacterName to its main's (slot 0) name, or null. Written even when it
        // looks unchanged, so the database ends up equal to Characters even if this context's copy of
        // the account was read before someone else's save. Returns whether the main's name changed.
        private static bool StampMainName(EntityEntry<AppUser> account, List<Character> rows)
        {
            var main = rows.Where(row => row.Slot == Character.MainSlot).OrderBy(row => row.Id).FirstOrDefault();
            var property = account.Property(MainNameColumn);
            var oldMain = account.State == EntityState.Added ? null : property.OriginalValue as string;
            property.CurrentValue = main?.Name;
            if (account.State != EntityState.Added)
            {
                property.IsModified = true;
            }
            return account.State != EntityState.Added && !string.Equals(oldMain, main?.Name, StringComparison.Ordinal);
        }

        // The roster name (see the top of this file): new memberships, memberships moved to another
        // account (a placeholder claim), memberships whose roster name someone set by hand, and every
        // membership of an account whose main was renamed in this save.
        // The main's name comes from this context only for an account it is creating or whose
        // characters this save copied (copiedAccounts); any other account's is read from the
        // database, as this context's copy of it may be older than another request's rename.
        private async Task StampRosterNamesAsync(
            HashSet<string> renamedAccounts,
            HashSet<string> copiedAccounts,
            Dictionary<string, EntityEntry<AppUser>> userEntries,
            bool async,
            CancellationToken cancellationToken)
        {
#pragma warning disable CS0618 // Round 5: the roster name is written only here, as a copy of the main.
            const string rosterName = nameof(AppUserLinkshell.CharacterName);
#pragma warning restore CS0618
            var memberships = ChangeTracker.Entries<AppUserLinkshell>()
                .Where(entry => entry.State == EntityState.Added
                    || (entry.State == EntityState.Modified
                        && (entry.Property(nameof(AppUserLinkshell.AppUserId)).IsModified
                            || entry.Property(rosterName).IsModified)))
                .ToList();
            if (renamedAccounts.Count > 0)
            {
                var renamed = renamedAccounts.ToList();
                var seen = memberships.Select(entry => entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
                foreach (var link in await ListAsync(
                             AppUserLinkshells.Where(link => link.AppUserId != null && renamed.Contains(link.AppUserId)),
                             async, cancellationToken))
                {
                    var entry = Entry(link);
                    if (entry.State != EntityState.Deleted && seen.Add(link))
                    {
                        memberships.Add(entry);
                    }
                }
            }
            if (memberships.Count == 0)
            {
                return;
            }

            var mains = new Dictionary<string, string?>(StringComparer.Ordinal);
            var unknown = new List<string>();
            foreach (var id in memberships.Select(entry => entry.Entity.AppUserId).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (userEntries.TryGetValue(id, out var account)
                    && (account.State == EntityState.Added
                        || (account.State != EntityState.Deleted && copiedAccounts.Contains(id))))
                {
                    mains[id] = account.Entity.CharacterName;
                }
                else
                {
                    unknown.Add(id);
                }
            }
            if (unknown.Count > 0)
            {
                var query = Users.AsNoTracking()
                    .Where(user => unknown.Contains(user.Id))
                    .Select(user => new { user.Id, user.CharacterName });
                foreach (var row in async ? await query.ToListAsync(cancellationToken) : query.ToList())
                {
                    mains[row.Id] = row.CharacterName;
                }
            }

            foreach (var entry in memberships)
            {
                if (entry.Entity.AppUserId is { } id && mains.TryGetValue(id, out var main))
                {
                    SetDerived(entry, rosterName, main);
                }
            }
        }

        // A new account saved with names in its old columns and no Characters rows of its own in this
        // save: those names become its rows (see the top of this file). Returns whether any were added.
        private bool AdoptNewAccountsOldColumns(
            Dictionary<string, EntityEntry<AppUser>> userEntries, List<EntityEntry<Character>> characterEntries)
        {
            var adopted = false;
            // Account id order, then slot: the order the old SQL copy inserted rows in.
            foreach (var (id, account) in userEntries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (account.State != EntityState.Added
                    || characterEntries.Any(entry => string.Equals(entry.Entity.AppUserId, id, StringComparison.Ordinal)))
                {
                    continue;
                }
                var rows = CharacterSync.DesiredRows(account.Entity);
                if (rows.Count == 0)
                {
                    continue;
                }
                foreach (var row in rows)
                {
                    if (CharacterWriter.IsTooLong(row.Name))
                    {
                        LogAccountNames("Characters: new account {AppUserId} has a name in slot {Slot} longer than {MaxLength} characters; it is left out.",
                            id, row.Slot, CharacterWriter.MaxNameLength);
                        continue;
                    }
                    Characters.Add(new Character
                    {
                        AppUserId = id,
                        Slot = row.Slot,
                        Name = row.Name,
                        NormalizedName = row.NormalizedName,
                        JobLevels = row.JobLevels?.ToArray(),
                        StrongJobs = row.StrongJobs?.ToArray(),
                        MeritJobs = row.MeritJobs?.ToArray(),
                        CraftLevels = row.CraftLevels?.ToArray(),
                        UpdatedAt = DateTime.UtcNow,
                    });
                    adopted = true;
                }
                LogAccountNames("Characters: new account {AppUserId} was saved with names in its old columns; they were taken as its characters. Write names with CharacterWriter.", id);
            }
            return adopted;
        }

        // An existing account whose CharacterName was changed by hand in a save that does not change
        // its Characters rows (a save that does stamps it right after this), or whose old columns were
        // changed by hand in any save: the change is undone. Compared by value, so Identity's
        // UpdateAsync (which marks every column modified) is not a change.
        private void RevertCopiesChangedByHand(Dictionary<string, EntityEntry<AppUser>> userEntries, HashSet<string> copiedAccounts)
        {
            foreach (var (id, account) in userEntries)
            {
                if (account.State != EntityState.Modified)
                {
                    continue;
                }
                var columns = copiedAccounts.Contains(id)
                    ? OldAccountColumns
                    : OldAccountColumns.Prepend(MainNameColumn);
                var reverted = new List<string>();
                foreach (var column in columns)
                {
                    var property = account.Property(column);
                    if (!SameValue(property.CurrentValue, property.OriginalValue))
                    {
                        property.CurrentValue = property.OriginalValue;
                        reverted.Add(column);
                    }
                    // Not written at all: Identity's UpdateAsync marks every column modified, and an
                    // account loaded before another request changed its characters would otherwise
                    // write its old copies back over the new ones.
                    property.IsModified = false;
                }
                if (reverted.Count > 0)
                {
                    LogAccountNames("Characters: a save changed {Columns} of account {AppUserId} directly; the change was undone. Write names, jobs and crafts with CharacterWriter.",
                        string.Join(", ", reverted), id);
                }
            }
        }

        // Postgres deletes an account's Characters rows with it (the foreign key cascades). The
        // InMemory test database has no foreign keys, so there they are removed here.
        private async Task RemoveDeletedAccountsCharactersWithoutSqlAsync(
            Dictionary<string, EntityEntry<AppUser>> userEntries, bool async, CancellationToken cancellationToken)
        {
            if (Database.IsRelational())
            {
                return;
            }
            var deleted = userEntries.Where(pair => pair.Value.State == EntityState.Deleted).Select(pair => pair.Key).ToList();
            if (deleted.Count == 0)
            {
                return;
            }
            foreach (var row in await ListAsync(Characters.Where(character => deleted.Contains(character.AppUserId)), async, cancellationToken))
            {
                if (Entry(row).State != EntityState.Deleted)
                {
                    Characters.Remove(row);
                }
            }
        }

        private static bool SameValue(object? a, object? b) => (a, b) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            (int[] x, int[] y) => x.SequenceEqual(y),
            (string[] x, string[] y) => x.SequenceEqual(y),
            _ => Equals(a, b),
        };

        // A warning in the app log. Logging must not fail a save, so a context built without a
        // logger simply logs nothing.
        private void LogAccountNames(string message, params object?[] args)
        {
            try
            {
                Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                    .GetService<Microsoft.Extensions.Logging.ILoggerFactory>(this)
                    .CreateLogger(typeof(CharacterWriter).FullName!)
                    .Log(Microsoft.Extensions.Logging.LogLevel.Warning, message, args);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static async Task<List<T>> ListAsync<T>(IQueryable<T> query, bool async, CancellationToken cancellationToken) =>
            async ? await query.ToListAsync(cancellationToken) : query.ToList();
    }
}
