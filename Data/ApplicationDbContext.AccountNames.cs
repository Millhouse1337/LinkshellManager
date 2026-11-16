using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace LinkshellManagerDiscordApp.Data
{
    // Round 5: Characters is the only place names, jobs and crafts are written (CharacterWriter).
    // Every save then copies the main's (slot 0) name onto AppUser.CharacterName, the account's
    // display name (null when the account has no main). Derived, the same way Rank follows the role.
    // Nothing else is copied: the old account alt, job and craft columns and the roster name are gone
    // (Round5Contract, R5.7).
    //
    // Only accounts whose Characters rows change in the save are copied to (their other rows are read
    // from the database). No SQL is run after the save: everything is part of the save itself.
    //
    // Two guards for code that still writes CharacterName itself:
    // - an EXISTING account whose CharacterName was changed in a save that does not change its
    //   Characters rows gets the old value back, and a Warning is logged (use CharacterWriter);
    // - a NEW account saved with a CharacterName and no main (slot 0) Characters row gets that name as
    //   its main row (a name over CharacterWriter.MaxNameLength is left out), and a Warning is logged.
    //   Only tests still create accounts that way.
    public partial class ApplicationDbContext
    {
        // The account's display name: a copy of the main's (slot 0) name.
        private const string MainNameColumn = nameof(AppUser.CharacterName);

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
            if (AdoptNewAccountsMainName(userEntries, characterEntries))
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
                    StampMainName(account, CharactersNow(id, saved, characterEntries));
                }
            }
        }

        // A changed row: the name trimmed (and never blank or too long, see CharacterWriter),
        // NormalizedName from it, and UpdatedAt now. A refusal is a DbUpdateException, so callers that
        // show save errors (Data Admin) show it as one.
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
        // the account was read before someone else's save.
        private static void StampMainName(EntityEntry<AppUser> account, List<Character> rows)
        {
            var main = rows.Where(row => row.Slot == Character.MainSlot).OrderBy(row => row.Id).FirstOrDefault();
            var property = account.Property(MainNameColumn);
            property.CurrentValue = main?.Name;
            if (account.State != EntityState.Added)
            {
                property.IsModified = true;
            }
        }

        // A new account saved with a CharacterName and no main (slot 0) Characters row of its own in
        // this save: that name becomes its main row (see the top of this file). Alt rows saved with it
        // are kept as they are. Returns whether any were added.
        private bool AdoptNewAccountsMainName(
            Dictionary<string, EntityEntry<AppUser>> userEntries, List<EntityEntry<Character>> characterEntries)
        {
            var adopted = false;
            foreach (var (id, account) in userEntries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (account.State != EntityState.Added
                    || characterEntries.Any(entry => entry.Entity.Slot == Character.MainSlot
                        && string.Equals(entry.Entity.AppUserId, id, StringComparison.Ordinal)))
                {
                    continue;
                }
                var name = CharacterWriter.Clean(account.Entity.CharacterName);
                if (name is null)
                {
                    continue;
                }
                if (CharacterWriter.IsTooLong(name))
                {
                    LogAccountNames("Characters: new account {AppUserId} has a name in slot {Slot} longer than {MaxLength} characters; it is left out.",
                        id, Character.MainSlot, CharacterWriter.MaxNameLength);
                    continue;
                }
                Characters.Add(new Character
                {
                    AppUserId = id,
                    Slot = Character.MainSlot,
                    Name = name,
                    NormalizedName = CharacterWriter.Normalize(name),
                    UpdatedAt = DateTime.UtcNow,
                });
                adopted = true;
                LogAccountNames("Characters: new account {AppUserId} was saved with a CharacterName and no characters; it was taken as its main. Write names with CharacterWriter.", id);
            }
            return adopted;
        }

        // An existing account whose CharacterName was changed by hand in a save that does not change
        // its Characters rows (a save that does stamps it right after this): the change is undone.
        // Compared by value, so Identity's UpdateAsync (which marks every column modified) is not a
        // change.
        private void RevertCopiesChangedByHand(Dictionary<string, EntityEntry<AppUser>> userEntries, HashSet<string> copiedAccounts)
        {
            foreach (var (id, account) in userEntries)
            {
                if (account.State != EntityState.Modified || copiedAccounts.Contains(id))
                {
                    continue;
                }
                var property = account.Property(MainNameColumn);
                var changed = !string.Equals(property.CurrentValue as string, property.OriginalValue as string, StringComparison.Ordinal);
                if (changed)
                {
                    property.CurrentValue = property.OriginalValue;
                }
                // Not written at all: Identity's UpdateAsync marks every column modified, and an account
                // loaded before another request changed its characters would otherwise write its old
                // copy back over the new one.
                property.IsModified = false;
                if (changed)
                {
                    LogAccountNames("Characters: a save changed {Columns} of account {AppUserId} directly; the change was undone. Write names, jobs and crafts with CharacterWriter.",
                        MainNameColumn, id);
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
