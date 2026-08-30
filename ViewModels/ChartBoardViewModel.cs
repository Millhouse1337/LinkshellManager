using LinkshellManagerDiscordApp.Services;

namespace LinkshellManagerDiscordApp.ViewModels;

/// <summary>
/// The website's view of one Charts board (Sky, Sea, …).
///
/// Deliberately thin: every derived number on it — the per-boss totals and the whole ledger — is
/// produced by <see cref="ChartBoardService"/>, the same class the Activity API calls. The two
/// surfaces share the rules, not the transport, which is how ItemSaleRecorder is shared today.
/// </summary>
public class ChartBoardViewModel
{
    public int LinkshellId { get; set; }

    public string? LinkshellName { get; set; }

    public string BoardKey { get; set; } = ChartBoardCatalog.Sky;

    public string BoardLabel { get; set; } = "Sky";

    public string Blurb { get; set; } = string.Empty;

    /// <summary>Every board, for the page's sub-nav.</summary>
    public IReadOnlyList<ChartBoard> Boards { get; set; } = ChartBoardCatalog.Boards;

    public List<ChartBossCardViewModel> Bosses { get; set; } = new();

    public ChartLedger Ledger { get; set; } = new(Array.Empty<string>(), Array.Empty<ChartLedgerRow>());

    /// <summary>Who a credit can be attributed to. Empty unless <see cref="CanManage"/>.</summary>
    public List<ChartRosterEntry> Roster { get; set; } = new();

    public DateTime? LastUpdatedUtc { get; set; }

    /// <summary>Gates the edit affordances. The real control is each POST action re-checking.</summary>
    public bool CanManage { get; set; }

    // ---- what this board offers, straight off the catalog ------------------------
    //
    // Carried on the view model rather than read off ChartBoardCatalog in the view, so the page has
    // no logic in it - the same call PopItemOptionsJson already made.

    /// <summary>Gates the "Add a pop item" CARD. Rows already on a board that turns this off stay
    /// listed and editable; only the add affordance goes.</summary>
    public bool AllowsPopItems { get; set; } = true;

    public bool AllowsDropItems { get; set; }

    public bool AllowsWishlist { get; set; }

    public bool AllowsKeyItems { get; set; }

    /// <summary>The board's item requests and the per-card counts its badges show.</summary>
    public ChartWishlistBoard Wishlist { get; set; } =
        new(Array.Empty<ChartWishlistRow>(), new Dictionary<string, int>(), 0);

    /// <summary>Per-member key item progress. No columns on a board that tracks none.</summary>
    public ChartKeyItemGrid KeyItems { get; set; } =
        new(Array.Empty<ChartKeyItemColumn>(), Array.Empty<ChartKeyItemGridRow>());

    /// <summary>
    /// The VIEWER's own membership, so the key item grid knows which row is theirs to tick. Null for
    /// somebody with no membership row. Presentation only - SetKeyItem re-checks on every post.
    /// </summary>
    public int? ViewerMembershipId { get; set; }

    /// <summary>
    /// Whether the viewer may submit an item request. Deliberately NOT CanManage: the wishlist is
    /// the one part of Charts a plain member writes, which is why it has its own flag rather than
    /// reusing the officer one.
    /// </summary>
    public bool CanRequest { get; set; }

    /// <summary>
    /// Every card, split into contiguous RUNS of the same group label. One grid, in the catalog's
    /// order, mirroring the Activity's bossGroups computed.
    ///
    /// Runs rather than a GroupBy: the catalog's order IS the display order, and grouping by key
    /// would silently reorder cards to match the first sighting of each label. A board that declares
    /// no groups collapses to ONE run with a null label, so the view emits no heading at all.
    ///
    /// EVERY kind is in here, not only Standard. Mini NMs and finales used to render in their own
    /// blocks beneath the grid, which put them outside the heading of the group they belong to the
    /// moment a board interleaved kinds with groups — Limbus does, since Apollyon and Temenos each
    /// have a chip area and a finale. Kind now picks a card's chrome, never its position, which is
    /// the call the Activity already made.
    /// </summary>
    public IReadOnlyList<ChartBossGroupViewModel> BossGroups
    {
        get
        {
            var runs = new List<(string? Label, List<ChartBossCardViewModel> Bosses)>();
            foreach (var boss in Bosses)
            {
                if (runs.Count == 0 || runs[^1].Label != boss.Group)
                {
                    runs.Add((boss.Group, new List<ChartBossCardViewModel>()));
                }
                runs[^1].Bosses.Add(boss);
            }
            return runs.Select(run => new ChartBossGroupViewModel(run.Label, run.Bosses)).ToList();
        }
    }

    /// <summary>
    /// Group labels drawn as vertical columns rather than rows of the grid. Twin of
    /// ChartBoard.PathColumns; empty for every board but Sky.
    /// </summary>
    public IReadOnlyList<string> PathColumns { get; set; } = Array.Empty<string>();

    /// <summary>
    /// The runs drawn as COLUMNS, in the order PathColumns names them — not in run order, so a
    /// column's position on the page is decided by the catalog rather than by where its first card
    /// happens to sit. Empty on every board but Sky.
    /// </summary>
    public IReadOnlyList<ChartBossGroupViewModel> PathGroups =>
        PathColumns
            .Select(label => BossGroups.FirstOrDefault(group => group.Label == label))
            .Where(group => group is not null)
            .Select(group => group!)
            .ToList();

    /// <summary>Everything else, in run order — Kirin's run on Sky, the whole board elsewhere.</summary>
    public IReadOnlyList<ChartBossGroupViewModel> TrailingGroups =>
        BossGroups.Where(group => !PathColumns.Contains(group.Label)).ToList();

    /// <summary>
    /// Draw as centred rows of fixed-width cards rather than a stretch-to-fit grid, for a board that
    /// chose its own row lengths. Twin of ChartBoard.CentersRows.
    /// </summary>
    public bool CentersRows { get; set; }

    public bool IsEmpty => Bosses.All(boss => boss.TotalItems == 0);

    /// <summary>
    /// Whether ANY card here declares pop items — i.e. whether the page needs to ship the picker
    /// machinery at all. Mirrors ChartBoard.HasPopItemOptions, which is Any for the same reason:
    /// boards MIX. Picker-or-free-text is chosen per BOSS by _ChartPopItemField; this only decides
    /// whether the boss-change swap script gets its data.
    ///
    /// Was All, which was already wrong: Sea has four bosses that take no trade item, so the
    /// &lt;script id="chart-pop-items"&gt; block at the foot of Board.cshtml was never emitted there and
    /// the swap script bailed on its first line — changing the boss on a Sea form left the previous
    /// boss's picker in place. Sky hits the same the moment Kirin declares nothing.
    /// </summary>
    public bool HasPopItemOptions => Bosses.Any(boss => boss.PopItemOptions.Count > 0);

    /// <summary>Twin of the above for the drop form, and Any for the same reason.</summary>
    public bool HasDropItemOptions => Bosses.Any(boss => boss.DropItemOptions.Count > 0);

    /// <summary>
    /// The whole board's items as { boss: [{ name, source }] }, for the one script on the page that
    /// repopulates a dropdown when its boss select changes. Serialised here rather than in the view
    /// so the page has no logic in it, and camelCased to read like every other payload this app
    /// hands the browser.
    /// </summary>
    /// <remarks>
    /// Keyed by KIND first, then by boss, because the page can carry two add forms and each has to
    /// repopulate from its own list. One island rather than two: the swap script reads the kind off
    /// the form that fired, so the shape is { Pop: { boss: [...] }, Drop: { boss: [...] } }.
    /// </remarks>
    public string ItemOptionsJson =>
        System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, Dictionary<string, IReadOnlyList<ChartPopItemOption>>>
            {
                [ChartItemKinds.Pop] = Bosses.ToDictionary(boss => boss.Boss, boss => boss.PopItemOptions),
                [ChartItemKinds.Drop] = Bosses.ToDictionary(boss => boss.Boss, boss => boss.DropItemOptions),
            },
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                // The board is inside a <script type="application/json"> block, so a boss name
                // carrying "</script>" would end it early. Nothing in the catalog does, but the
                // relaxed default encoder would let one through unescaped.
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
            });
}

public class ChartBossCardViewModel
{
    public string Boss { get; set; } = string.Empty;

    /// <summary>Lower-cased slug. Names a CSS class; never carries a colour value.</summary>
    public string ThemeKey { get; set; } = string.Empty;

    public string Kind { get; set; } = ChartBossKinds.Standard;

    /// <summary>Site-absolute, unlike the Activity's relative path — the web has no baseHref.</summary>
    public string EmblemPath { get; set; } = string.Empty;

    public string? Subtitle { get; set; }

    /// <summary>Section heading this card sits under, or null. Presentation only.</summary>
    public string? Group { get; set; }

    /// <summary>The card this one's drops feed ("Byakko"), or null. Twin of the DTO's LeadsTo.</summary>
    public string? LeadsTo { get; set; }

    /// <summary>That card's OWN theme key, so the arrow badge carries the TARGET's hue. Resolved
    /// from the catalog in the controller, never re-derived from a boss name in the view.</summary>
    public string? LeadsToThemeKey { get; set; }

    /// <summary>Start a new row after this card. Twin of ChartBoss.EndsRow.</summary>
    public bool EndsRow { get; set; }

    public IReadOnlyList<string> Rewards { get; set; } = Array.Empty<string>();

    public string? ReferenceNote { get; set; }

    /// <summary>The pop items this boss takes, or empty when the board does not spell them out.</summary>
    public IReadOnlyList<ChartPopItemOption> PopItemOptions { get; set; } = Array.Empty<ChartPopItemOption>();

    /// <summary>What falls OFF this boss, for the drop form. Empty leaves that box free text.</summary>
    public IReadOnlyList<ChartPopItemOption> DropItemOptions { get; set; } = Array.Empty<ChartPopItemOption>();

    /// <summary>Pending item requests tied to THIS card. Board-level requests count toward none.</summary>
    public int PendingRequestCount { get; set; }

    /// <summary>The key item earned here, or null for a card that grants none.</summary>
    public string? KeyItemName { get; set; }

    public int KeyItemHaveCount { get; set; }

    public int KeyItemTotalMembers { get; set; }

    /// <summary>Exactly who still needs it, in roster order - what the card's drawer lists.</summary>
    public IReadOnlyList<string> KeyItemMissing { get; set; } = Array.Empty<string>();

    public List<ChartPopItemViewModel> Items { get; set; } = new();

    /// <summary>
    /// The rows folded down to one line per ITEM, with the quantities summed — what the card shows.
    ///
    /// Three people each holding a Gem of the South are three rows, and that is the grain the
    /// holdings table is built on; on a card one of several in a row, they were three lines saying
    /// the same word. Twin of ChartBoardSectionComponent.consolidatedItems: first-seen spelling and
    /// first-seen position win, because officers order their own rows.
    /// </summary>
    public IReadOnlyList<ChartConsolidatedItemViewModel> ConsolidatedItems
    {
        get
        {
            var order = new List<ChartConsolidatedItemViewModel>();
            var seen = new Dictionary<string, ChartConsolidatedItemViewModel>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in Items)
            {
                if (seen.TryGetValue(item.ItemName.Trim(), out var line))
                {
                    line.Quantity += item.Quantity;
                    continue;
                }

                line = new ChartConsolidatedItemViewModel { Name = item.ItemName, Quantity = item.Quantity };
                seen[item.ItemName.Trim()] = line;
                order.Add(line);
            }

            return order;
        }
    }

    /// <summary>DISTINCT items, matching the lines on the card. <see cref="Items"/> counts rows.</summary>
    public int TotalItems => ConsolidatedItems.Count;

    public int TotalQuantity => Items.Sum(item => item.Quantity);
}

/// <summary>One line of a boss card: an item name and how many of it the linkshell holds, all told.</summary>
public class ChartConsolidatedItemViewModel
{
    public string Name { get; set; } = string.Empty;

    public int Quantity { get; set; }
}

/// <summary>
/// What the boss-card partials need: the boss itself, plus the board around it for the roster,
/// the permission and the board key that every edit form has to post back.
/// </summary>
public sealed record ChartBossCardContext(ChartBoardViewModel Board, ChartBossCardViewModel Boss);

/// <summary>
/// One heading and the cards under it. Twin of ChartBossGroup in the Activity's
/// chart-board-section.component.ts. A null <paramref name="Label"/> means the board declares no
/// groups, and the view renders no heading at all.
/// </summary>
public sealed record ChartBossGroupViewModel(string? Label, IReadOnlyList<ChartBossCardViewModel> Bosses);

/// <summary>
/// What _ChartPopItemField needs: the pop items of the boss the form OPENS on, and the name already
/// on the row when one is being edited.
/// </summary>
/// <param name="Options">The opening boss's items. Empty renders a free-text box instead of a picker
/// — Sea has bosses of both kinds, so this is decided per boss, not per board.</param>
/// <param name="Current">The row's saved item name when editing; null on the add form.</param>
/// <param name="FieldId">id for the control a &lt;label for&gt; points at, or null when nothing does.</param>
/// <param name="Kind">ChartItemKinds.Pop or Drop. Decides the placeholder wording and rides on the
/// control as a data attribute; LAST, because this record's tail is two optional string?s and a new
/// one inserted before them would compile clean and silently steal a value.</param>
public sealed record ChartPopItemFieldContext(
    IReadOnlyList<ChartPopItemOption> Options,
    string? Current = null,
    string? FieldId = null,
    string Kind = ChartItemKinds.Pop);

/// <summary>
/// What _ChartAddItemForm needs to draw ONE add card. The page renders it TWICE on Sky and Sea - a
/// pop card and a drop card, stacked.
/// </summary>
/// <param name="IdPrefix">Every element id in the form is built from this. Two forms sharing a
/// literal id would break every &lt;label for&gt; on the second AND let the picker-swap script move
/// the id off the control the first one's label points at, because that script sets picker.id from
/// data-chart-item-id whenever a boss changes.</param>
public sealed record ChartAddItemFormContext(
    ChartBoardViewModel Board,
    string Kind,
    string Title,
    string ItemLabel,
    string SubmitLabel,
    string IdPrefix)
{
    /// <summary>The options the form OPENS on: the first card's list, for this kind.</summary>
    public IReadOnlyList<ChartPopItemOption> OpeningOptions =>
        Board.Bosses.Count == 0
            ? Array.Empty<ChartPopItemOption>()
            : Kind == ChartItemKinds.Drop
                ? Board.Bosses[0].DropItemOptions
                : Board.Bosses[0].PopItemOptions;
}

public class ChartPopItemViewModel
{
    public int Id { get; set; }

    public string Boss { get; set; } = string.Empty;

    public string ItemName { get; set; } = string.Empty;

    /// <summary>ChartItemKinds.Pop or Drop. Picks the pill in the holdings table and which option
    /// list the row's edit drawer offers.</summary>
    public string Kind { get; set; } = ChartItemKinds.Pop;

    public string? HeldByCharacterName { get; set; }

    public int Quantity { get; set; }

    public string? Notes { get; set; }

    public List<string> CreditedTo { get; set; } = new();

    /// <summary>What the card's "Farmers Credited" column shows.</summary>
    public int CreditCount => CreditedTo.Count;
}
