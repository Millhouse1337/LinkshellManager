using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace LinkshellManagerDiscordApp.Data
{
    // Round 5 (R5.5): Characters is the only place names, jobs and crafts are written
    // (CharacterWriter). Every save then copies the result onto the places that still hold a copy:
    //
    // 1. AppUser.CharacterName, the account's display name: always the main's (slot 0) name, or null
    //    when the account has no main. Derived, the same way Rank follows the role.
    // 2. The old AspNetUsers alt, job and craft columns: an exact mirror of the three slots, so the
    //    build before Round 5's name switch (which reads them, and copies them into Characters at
    //    start-up and on every save) sees exactly what this build wrote if it is swapped back in.
    // 3. The roster name, AppUserLinkshell.CharacterName: nothing reads it any more, but the build
    //    before still does, so every new membership, every membership that changes account, and every
    //    membership of an account whose main is renamed gets the account's main name.
    //
    // Only accounts whose Characters rows change in the save are copied to (their other rows are read
    // from the database). No SQL is run after the save: everything is part of the save itself.
    //
    // Two guards keep the copies exact when some code still writes the account columns itself:
    // - an EXISTING account whose copy columns were changed in a save that does not change its
    //   Characters rows gets the old values back, and a Warning is logged (use CharacterWriter);
    // - a NEW account saved with names in its old columns and no Characters rows gets those names as
    //   its Characters rows (CharacterSync.DesiredRows; names over CharacterWriter.MaxNameLength are
    //   left out), and a Warning is logged. Only tests still create accounts that way.
    public partial class ApplicationDbContext
    {
        // The account columns that are copies of the account's characters (the main's name first).
        private static IReadOnlyList<string> AccountNameColumns => CharacterSync.SourceProperties;

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
                    if (CopyCharactersOntoAccount(account, rows))
                    {
                        renamedAccounts.Add(id);
                    }
                }
            }

            await StampRosterNamesAsync(renamedAccounts, userEntries, async, cancellationToken);
        }

        // A changed row: the name trimmed (and never blank or too long for the mirror), NormalizedName
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

        // The account's rows as they will be after this save: the saved rows, with this save's
        // deletes, edits and inserts applied.
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
                    default:
                        if (belongs) rows[entry.Entity.Id] = entry.Entity;
                        else rows.Remove(entry.Entity.Id);
                        break;
                }
            }
            return rows.Values.Concat(added).ToList();
        }

        // Writes the account's copies of its characters. Every copy column is written, changed or not,
        // so the database ends up equal to Characters even if this context's copy of the account was
        // read before someone else's save. Returns whether the main's name changed.
        private static bool CopyCharactersOntoAccount(EntityEntry<AppUser> account, List<Character> rows)
        {
            Character? InSlot(short slot) => rows.Where(row => row.Slot == slot).OrderBy(row => row.Id).FirstOrDefault();
            var main = InSlot(Character.MainSlot);
            var alt1 = InSlot(Character.Alt1Slot);
            var alt2 = InSlot(Character.Alt2Slot);

            var oldMain = account.State == EntityState.Added
                ? null
                : account.Property(nameof(AppUser.CharacterName)).OriginalValue as string;
            var values = new object?[]
            {
                main?.Name,
                main?.JobLevels?.ToArray(), main?.StrongJobs?.ToArray(), main?.MeritJobs?.ToArray(), main?.CraftLevels?.ToArray(),
                alt1?.Name, alt1?.JobLevels?.ToArray(), alt1?.StrongJobs?.ToArray(), alt1?.MeritJobs?.ToArray(), alt1?.CraftLevels?.ToArray(),
                alt2?.Name, alt2?.JobLevels?.ToArray(), alt2?.StrongJobs?.ToArray(), alt2?.MeritJobs?.ToArray(), alt2?.CraftLevels?.ToArray(),
            };
            for (var i = 0; i < AccountNameColumns.Count; i++)
            {
                var property = account.Property(AccountNameColumns[i]);
                property.CurrentValue = values[i];
                if (account.State != EntityState.Added)
                {
                    property.IsModified = true;
                }
            }
            return account.State != EntityState.Added && !string.Equals(oldMain, main?.Name, StringComparison.Ordinal);
        }

        // The roster name (see the top of this file): new memberships, memberships moved to another
        // account (a placeholder claim), memberships whose roster name someone set by hand, and every
        // membership of an account whose main was renamed in this save.
        private async Task StampRosterNamesAsync(
            HashSet<string> renamedAccounts,
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
                if (userEntries.TryGetValue(id, out var account) && account.State != EntityState.Deleted)
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

        // An existing account whose copy columns were changed by hand, in a save that does not change
        // its Characters rows: the change is undone, so the copy stays exact. Compared by value, so
        // Identity's UpdateAsync (which marks every column modified) is not a change.
        private void RevertCopiesChangedByHand(Dictionary<string, EntityEntry<AppUser>> userEntries, HashSet<string> copiedAccounts)
        {
            foreach (var (id, account) in userEntries)
            {
                if (account.State != EntityState.Modified || copiedAccounts.Contains(id))
                {
                    continue;
                }
                var reverted = new List<string>();
                foreach (var column in AccountNameColumns)
                {
                    var property = account.Property(column);
                    if (!SameValue(property.CurrentValue, property.OriginalValue))
                    {
                        property.CurrentValue = property.OriginalValue;
                        reverted.Add(column);
                    }
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
