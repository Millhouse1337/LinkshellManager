namespace LinkshellManagerDiscordApp.Services;

// Groups tables by cascade delete for the Data Admin "Choose tables" page: a table nests under
// the table whose deletion would delete its rows. Built from an edge list (dependent -> principal
// for every Cascade foreign key) rather than from EF metadata directly so the shape rules can be
// unit-tested with plain strings, including the cycle guard the real model never exercises.
//
// Parent choice when a table has several cascade principals (DkpPoolEventType cascades from both
// Linkshell and DkpPool): the DEEPEST principal wins, so the table sits under the most specific
// owner; a tie goes to the item `preferOnTie` marks (Linkshell, the tenant root), then to the
// name. The other principals are kept as AlsoDeletedWith so the page can still say so.
public sealed class DataAdminCascadeForest<T> where T : notnull
{
    public sealed class Node
    {
        public required T Item { get; init; }
        public Node? Parent { get; internal set; }
        public int Depth { get; internal set; }
        public required IReadOnlyList<T> AlsoDeletedWith { get; init; }
        // Every table reachable through Cascade edges, transitively -- what Postgres will actually
        // remove when one row of Item goes.
        public required IReadOnlyList<T> CascadeDescendants { get; init; }
        public List<Node> Children { get; } = new();
    }

    private DataAdminCascadeForest(IReadOnlyList<Node> roots, IReadOnlyDictionary<T, Node> byItem)
    {
        Roots = roots;
        ByItem = byItem;
    }

    public IReadOnlyList<Node> Roots { get; }
    public IReadOnlyDictionary<T, Node> ByItem { get; }

    public static DataAdminCascadeForest<T> Build(
        IReadOnlyList<T> items,
        IEnumerable<(T Dependent, T Principal)> edges,
        Func<T, string> nameOf,
        Func<T, bool> preferOnTie)
    {
        var comparer = EqualityComparer<T>.Default;
        var principals = items.ToDictionary(item => item, _ => new List<T>());
        var dependents = items.ToDictionary(item => item, _ => new List<T>());
        foreach (var (dependent, principal) in edges)
        {
            if (comparer.Equals(dependent, principal)
                || !principals.TryGetValue(dependent, out var ps)
                || !dependents.TryGetValue(principal, out var ds)
                || ps.Contains(principal))
            {
                continue;
            }
            ps.Add(principal);
            ds.Add(dependent);
        }

        // Cascade depth: 0 for a root, else one more than the deepest principal. The path set
        // breaks a mutual cascade (A -> B -> A) instead of recursing forever.
        var depth = new Dictionary<T, int>();
        int DepthOf(T item, HashSet<T> path)
        {
            if (depth.TryGetValue(item, out var known))
            {
                return known;
            }
            if (!path.Add(item))
            {
                return 0;
            }
            var result = 0;
            foreach (var principal in principals[item])
            {
                result = Math.Max(result, 1 + DepthOf(principal, path));
            }
            path.Remove(item);
            depth[item] = result;
            return result;
        }
        foreach (var item in items)
        {
            DepthOf(item, new HashSet<T>());
        }

        var parentOf = new Dictionary<T, T>();
        var alsoDeletedWith = new Dictionary<T, IReadOnlyList<T>>();
        foreach (var item in items)
        {
            var ordered = principals[item]
                .OrderByDescending(principal => depth[principal])
                .ThenByDescending(preferOnTie)
                .ThenBy(nameOf, StringComparer.Ordinal)
                .ToList();
            if (ordered.Count > 0)
            {
                parentOf[item] = ordered[0];
            }
            alsoDeletedWith[item] = ordered.Skip(1).ToList();
        }

        // A parent chain that loops back on itself can only come from a cascade cycle; cut the
        // link that closes the loop so every chain ends at a root and rendering terminates.
        foreach (var item in items)
        {
            var seen = new HashSet<T> { item };
            var cursor = item;
            while (parentOf.TryGetValue(cursor, out var up))
            {
                if (!seen.Add(up))
                {
                    parentOf.Remove(cursor);
                    break;
                }
                cursor = up;
            }
        }

        var descendants = new Dictionary<T, IReadOnlyList<T>>();
        foreach (var item in items)
        {
            var visited = new HashSet<T>();
            var stack = new Stack<T>(dependents[item]);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                if (comparer.Equals(next, item) || !visited.Add(next))
                {
                    continue;
                }
                foreach (var further in dependents[next])
                {
                    stack.Push(further);
                }
            }
            descendants[item] = visited.OrderBy(nameOf, StringComparer.Ordinal).ToList();
        }

        var nodes = items.ToDictionary(
            item => item,
            item => new Node { Item = item, AlsoDeletedWith = alsoDeletedWith[item], CascadeDescendants = descendants[item] });
        var roots = new List<Node>();
        foreach (var item in items.OrderBy(nameOf, StringComparer.Ordinal))
        {
            var node = nodes[item];
            if (parentOf.TryGetValue(item, out var parent))
            {
                node.Parent = nodes[parent];
                nodes[parent].Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        static void AssignDepth(Node node, int level)
        {
            node.Depth = level;
            foreach (var child in node.Children)
            {
                AssignDepth(child, level + 1);
            }
        }
        foreach (var root in roots)
        {
            AssignDepth(root, 0);
        }

        return new DataAdminCascadeForest<T>(roots, nodes);
    }
}
