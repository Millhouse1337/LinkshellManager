using LinkshellManagerDiscordApp.ViewModels;

namespace LinkshellManagerDiscordApp.Services;

// The delete confirmation's view model, built from a cascade-impact tree: what else goes with the
// row, what is unlinked, and what blocks the delete.
public static class DataAdminImpactMapper
{
    public static DataAdminDeleteViewModel ToViewModel(DataAdminModel table, DataAdminCascadeImpact impact)
    {
        var model = new DataAdminDeleteViewModel
        {
            Slug = table.Slug,
            DisplayName = table.DisplayName,
            Key = impact.Key,
            Label = impact.Label,
            IsBlocked = impact.IsBlocked,
            IsTruncated = impact.IsTruncated,
            DeletedRows = impact.DeletedRows,
            DiscordSideEffect = table.Policy.DiscordSideEffect,
        };
        foreach (var child in impact.Children)
        {
            model.Children.Add(ToNode(child));
        }
        foreach (var (dependent, rows) in impact.DeletedTotals.OrderBy(pair => pair.Key.DisplayName, StringComparer.Ordinal))
        {
            model.Totals.Add(new DataAdminImpactTotal(dependent.DisplayName, rows));
        }
        return model;
    }

    private static DataAdminImpactNode ToNode(DataAdminCascadeImpact.Node node)
    {
        var built = new DataAdminImpactNode
        {
            DisplayName = node.Table.DisplayName,
            Slug = node.Table.Slug,
            ForeignKeyDisplayName = node.Table.FindColumn(node.ForeignKeyColumn)?.DisplayName ?? node.ForeignKeyColumn,
            Effect = node.Effect switch
            {
                DataAdminCascadeImpact.Effect.Deleted => "deleted",
                DataAdminCascadeImpact.Effect.Unlinked => "unlinked",
                _ => "blocked",
            },
            Count = node.Count,
            AlreadyCounted = node.AlreadyCounted,
            Overflow = node.Overflow,
            DepthCapped = node.DepthCapped,
        };
        foreach (var child in node.Children)
        {
            built.Children.Add(ToNode(child));
        }
        return built;
    }
}
