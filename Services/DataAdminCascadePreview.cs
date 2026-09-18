using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// What deleting one row would do to every other table, computed from the real foreign keys
// before the delete page is shown. Postgres semantics, not EF's: the Data Admin delete tracks
// only the one row, so Cascade and SetNull happen in the database and everything else (Restrict,
// NoAction and EF's client-side behaviours, which migrate as no ON DELETE clause) makes Postgres
// refuse the delete.
public sealed class DataAdminCascadeImpact
{
    public enum Effect
    {
        Deleted,
        Unlinked,
        Blocked,
    }

    public sealed class Node
    {
        public required DataAdminModel Table { get; init; }
        public required string ForeignKeyColumn { get; init; }
        public required Effect Effect { get; init; }
        // Deleted: rows first reached through this edge. Unlinked/Blocked: rows on this edge.
        public required long Count { get; init; }
        // Deleted rows this edge reaches that another path already counted (a table with two
        // cascade parents, such as AppUserEventStatusLedger under Event and AppUserEvent).
        public long AlreadyCounted { get; init; }
        // More rows than the walker fetches keys for: shown as "N+" and not expanded.
        public bool Overflow { get; init; }
        // The walk stopped here (depth limit or a cycle); rows beneath are not listed.
        public bool DepthCapped { get; init; }
        public List<Node> Children { get; } = new();
    }

    public required DataAdminModel Table { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public List<Node> Children { get; } = new();
    // Distinct rows per table that the delete removes, whichever path reached them.
    public Dictionary<DataAdminModel, long> DeletedTotals { get; } = new();

    public bool IsBlocked => Flatten().Any(node => node.Effect == Effect.Blocked && node.Count > 0);
    public bool IsTruncated => Flatten().Any(node => node.Overflow || node.DepthCapped);
    public long DeletedRows => DeletedTotals.Values.Sum();

    public IEnumerable<Node> Flatten()
    {
        var stack = new Stack<Node>(Children);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }
    }
}

public static class DataAdminCascadePreview
{
    public static DataAdminCascadeImpact.Effect Classify(DeleteBehavior behavior) => behavior switch
    {
        DeleteBehavior.Cascade => DataAdminCascadeImpact.Effect.Deleted,
        DeleteBehavior.SetNull => DataAdminCascadeImpact.Effect.Unlinked,
        _ => DataAdminCascadeImpact.Effect.Blocked,
    };

    public static async Task<DataAdminCascadeImpact> ComputeAsync(ApplicationDbContext db, DataAdminModel table, object entity, CancellationToken ct)
    {
        var impact = new DataAdminCascadeImpact
        {
            Table = table,
            Key = table.KeyToString(table.KeyOf(entity)),
            Label = table.LabelOf(entity),
        };
        var seen = new Dictionary<DataAdminModel, HashSet<string>>();
        await WalkAsync(db, table, new[] { table.KeyOf(entity) }, 1, new HashSet<DataAdminModel> { table }, impact.Children, seen, ct);
        foreach (var (dependent, keys) in seen)
        {
            impact.DeletedTotals[dependent] = keys.Count;
        }
        return impact;
    }

    // Depth-first over the referencing foreign keys (already restricted to catalog tables).
    // Deleted edges fetch their keys, drop the ones another path already counted, and descend;
    // Unlinked and Blocked edges only count. Edges that touch nothing are left out of the tree.
    private static async Task WalkAsync(
        ApplicationDbContext db,
        DataAdminModel principal,
        IReadOnlyList<object> keys,
        int depth,
        HashSet<DataAdminModel> path,
        List<DataAdminCascadeImpact.Node> into,
        Dictionary<DataAdminModel, HashSet<string>> seen,
        CancellationToken ct)
    {
        foreach (var relation in principal.ReverseRelations)
        {
            var dependent = relation.Dependent;
            var effect = Classify(relation.DeleteBehavior);
            var total = await dependent.CountReferencingAsync(db, relation.ForeignKeyColumn, keys, ct);
            if (total == 0)
            {
                continue;
            }
            if (effect != DataAdminCascadeImpact.Effect.Deleted)
            {
                into.Add(new DataAdminCascadeImpact.Node { Table = dependent, ForeignKeyColumn = relation.ForeignKeyColumn.Name, Effect = effect, Count = total });
                continue;
            }
            if (total > DataAdminDefaults.CascadeMaxKeysPerLevel)
            {
                into.Add(new DataAdminCascadeImpact.Node { Table = dependent, ForeignKeyColumn = relation.ForeignKeyColumn.Name, Effect = effect, Count = total, Overflow = true });
                continue;
            }

            var childKeys = await dependent.KeysReferencingAsync(db, relation.ForeignKeyColumn, keys, ct);
            if (!seen.TryGetValue(dependent, out var counted))
            {
                counted = new HashSet<string>(StringComparer.Ordinal);
                seen[dependent] = counted;
            }
            var fresh = new List<object>();
            long already = 0;
            foreach (var childKey in childKeys)
            {
                if (counted.Add(dependent.KeyToString(childKey)))
                {
                    fresh.Add(childKey);
                }
                else
                {
                    already++;
                }
            }

            var capped = fresh.Count > 0 && (depth >= DataAdminDefaults.CascadeMaxDepth || path.Contains(dependent));
            var node = new DataAdminCascadeImpact.Node
            {
                Table = dependent,
                ForeignKeyColumn = relation.ForeignKeyColumn.Name,
                Effect = effect,
                Count = fresh.Count,
                AlreadyCounted = already,
                DepthCapped = capped,
            };
            into.Add(node);
            if (fresh.Count == 0 || capped)
            {
                continue;
            }

            path.Add(dependent);
            await WalkAsync(db, dependent, fresh, depth + 1, path, node.Children, seen, ct);
            path.Remove(dependent);
        }
    }
}
