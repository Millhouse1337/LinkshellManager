namespace LinkshellManagerDiscordApp.Services;

/// <summary>How a boss card is presented. Affects layout only — the data behind every kind is identical.</summary>
public static class ChartBossKinds
{
    /// <summary>An ordinary card in the grid.</summary>
    public const string Standard = "Standard";

    /// <summary>A farmable mini-NM whose drops feed other pops. Headlines stock, not item count.</summary>
    public const string MiniNm = "MiniNm";

    /// <summary>The board's final encounter. Carries its reference notes in a reward drawer.</summary>
    public const string Final = "Final";

    /// <summary>
    /// Not a monster: the pop items a whole GROUP shares, drawn as a wide strip under the group's
    /// heading instead of as a card. HENM's tiers - one set of pop items serves every NM in the tier.
    ///
    /// An entry in Bosses rather than a separate list, so its rows are ordinary rows filed under its
    /// name ("Tier 1"): the add form, the holdings table and farming credit all work unchanged. It
    /// never takes item requests - ChartWishlistService refuses it, and the request form hides it.
    /// </summary>
    public const string Tier = "Tier";
}

/// <summary>
/// Whether a tracked row is something traded TO a boss or something that fell OFF one.
///
/// Strings on the wire and in the column, like ChartBossKinds and ChartCreditStatuses, rather than an
/// enum: the value is stored on ChartPopItem.Kind and round-trips through two JSON payloads and a
/// form post, and a string that survives all three unchanged is one fewer place to map.
/// </summary>
public static class ChartItemKinds
{
    /// <summary>Traded to the boss to pop it. The original -- and the DEFAULT, so every row written
    /// before drops existed reads correctly with no backfill.</summary>
    public const string Pop = "Pop";

    /// <summary>Fell off the boss. Same fields, same farming credit, same ledger: only the list it is
    /// picked from and the pill beside it differ.</summary>
    public const string Drop = "Drop";
}

/// <summary>
/// Which affordances a board offers.
///
/// Declared ONCE per board rather than inferred from whether it happens to declare pop items or key
/// items. Dynamis and Limbus declare no PopItems today and still had an add form, so inferring would
/// have made "this board takes no trade items" and "this board has no add form" the same fact when
/// they are not. A board carrying data for a feature it does not flag renders nothing at all,
/// silently, which is what ChartBoardCatalogTests.OnlyBoardsThatAllow* exist to catch.
/// </summary>
[Flags]
public enum ChartBoardFeatures
{
    None = 0,

    /// <summary>
    /// The "Add a pop item" form. Gates the ADD only -- see ChartBoard.AllowsPopItems.
    /// </summary>
    PopItems = 1,

    /// <summary>The "Add a drop item" form, a twin of the pop one over the same rows.</summary>
    DropItems = 2,

    /// <summary>Members may submit item requests; officers may fulfil, order and remove them.</summary>
    Wishlist = 4,

    /// <summary>Per-member key item progress, with a card badge and a roster grid.</summary>
    KeyItems = 8,
}

/// <summary>
/// One key item tracked per member on a board.
///
/// <paramref name="Name"/> is STORED verbatim on ChartMemberKeyItem.KeyItemName, so it is the same
/// kind of key a boss name is: renaming one here orphans every row spelled the old way and needs a
/// data migration. Unlike a pop item name it is also a CLOSED list -- see
/// <see cref="ChartBoardCatalog.NormalizeKeyItemName"/> for why that one rejects where the item
/// normalizer canonicalises.
/// </summary>
/// <param name="Boss">The card it is earned on, or null for a board-level prerequisite that belongs
/// to no zone at all (Vial of Shrouded Sand). A null Boss still gets a grid COLUMN; it just gets no
/// card badge, because there is no card to put one on.</param>
/// <param name="Caption">Progression note under the column header ("One of four needed for
/// Beaucedine"). Reference text only: nothing is stored against it, so it is free to be reworded.</param>
public sealed record ChartKeyItem(string Name, string? Boss = null, string? Caption = null);

/// <summary>
/// One pop item a boss is known to take.
///
/// <paramref name="Name"/> is STORED verbatim on ChartPopItem.ItemName when an officer picks it, so
/// it is the same kind of key a boss name is — renaming one here leaves existing rows spelled the
/// old way, and the ledger counts them as a different item.
/// </summary>
/// <param name="Source">Where it comes from — the mob that drops it, or the NM on this same board
/// whose card it falls off. Reference text only: nothing is stored against it, so it is free to be
/// reworded.
///
/// NO board sets this any more. Every card now lists what falls OFF it, so an item's drop source IS
/// the card it sits on, and naming it again would print "Gem of the North — Zipacna" in a picker
/// sitting beside a boss select that already says Zipacna. Kept because it is the right answer for a
/// board whose items come from somewhere it draws no card for, which is what Sea was until the six
/// trash families joined it — and <see cref="Label"/> is unit-tested for the form either way.</param>
/// <param name="Needed">How many are needed to pop the card this one's arrow points AT, when that is
/// fixed and worth saying ("12", "1–3"). A STRING because it is a caption, not arithmetic: Ix'aern
/// (MNK) takes a range. The row's own Qty column is the tracked number; this is the target stock.</param>
public sealed record ChartPopItemOption(string Name, string? Source = null, string? Needed = null)
{
    /// <summary>
    /// What the picker shows for this option. Composed here, once, the way ChartBoardService decides
    /// the ledger's "6 / 8", and read off the payload.
    /// </summary>
    public string Label
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Needed) ? Name : $"{Name} ×{Needed}";
            return string.IsNullOrWhiteSpace(Source) ? name : $"{name} — {Source}";
        }
    }
}

/// <summary>One boss on a board.</summary>
/// <param name="Name">Canonical name; also what is stored on the rows.</param>
/// <param name="ThemeKey">Lower-case slug naming a CSS class. Never carries a colour value.</param>
/// <param name="EmblemFile">File name under ffxi_assets/&lt;board&gt;/. Explicit because the shipped
/// art has inconsistent casing and one misspelling (JailerOfForitude.jpg).</param>
/// <param name="Subtitle">Static flavour under the name, or null.</param>
/// <param name="Group">Section heading this card sits under, or null for an ungrouped board.
/// Presentation only — nothing is stored against it, so renaming one is a caption change, unlike
/// renaming <paramref name="Name"/>. Cards sharing a label MUST be adjacent: the app emits a
/// heading when a card's group differs from the previous card's, so a run that restarts prints its
/// heading twice. ChartBoardCatalogTests.GroupRunsAreContiguousWithinABoard is the guard.</param>
public sealed record ChartBoss(
    string Name,
    string ThemeKey,
    string EmblemFile,
    string Kind = ChartBossKinds.Standard,
    string? Subtitle = null,
    IReadOnlyList<string>? Rewards = null,
    string? ReferenceNote = null,
    // LAST on purpose, and anything added later goes here too. An optional string? inserted before
    // Subtitle would silently capture Jailer of Fortitude's subtitle — string? into string? compiles
    // clean and renders a phantom heading on Sea, with nothing to catch it but eyes on a page.
    string? Group = null,
    /// <summary>The pop items this boss takes, or null when the board does not spell them out.
    /// A non-empty list turns the "Pop item" box from free text into a picker.</summary>
    IReadOnlyList<ChartPopItemOption>? PopItems = null,
    /// <summary>
    /// The card on THIS board whose pop this one's drops feed — Zipacna → "Genbu". Null for a card
    /// that feeds nothing, which is every card on every board but Sky's farm NMs.
    ///
    /// Names a BOSS, never a colour. It resolves through <see cref="ChartBoard.LeadsToFor"/> and
    /// the arrow badge is painted in the TARGET card's own theme key, so an arrow and the card it
    /// points at can never end up different colours, and a name this board does not have degrades to
    /// no badge rather than to a badge with no hue.
    ///
    /// Behind PopItems for the same reason Group is behind Subtitle — it is a string?, and a future
    /// optional string? inserted before it would compile clean and silently steal its value. Every
    /// caller passes it BY NAME, which is the real defence.
    /// </summary>
    string? LeadsTo = null,
    /// <summary>
    /// Start a new row after this card. Lets a board lay its cards out in rows of its own choosing
    /// rather than however many happen to fit — Dynamis puts the three nations and Jeuno on one row,
    /// the two Xarcabard-era zones on the next, and so on.
    ///
    /// Only needed WITHIN a run: a group heading is full-width and already breaks the row before it,
    /// so the last card of a group never needs this. A board that sets it anywhere is drawn centred,
    /// because a row the author chose the length of is a row that should look deliberate rather than
    /// left-aligned with a ragged gap.
    /// </summary>
    bool EndsRow = false,
    /// <summary>
    /// What falls OFF this boss and gets held, as distinct from what is traded TO it. A non-empty
    /// list turns the DROP form's item box from free text into a picker, exactly as
    /// <see cref="PopItems"/> does for the pop form.
    ///
    /// A separate list rather than more entries in PopItems, for two reasons. One, the pop list is
    /// what NormalizeDraft canonicalises a pop row's name against, and a name in both lists would
    /// make that canonicalisation depend on which form was used - a coin flip at edit time, which
    /// ChartBoardCatalogTests.NoBoardListsAnItemAsBothAPopAndADrop refuses. Two, the migrations that
    /// moved rows are pinned against these lists by set equality (Sea's and Sky's DropItems), so an
    /// addition would be a failing test rather than a silent reclassification of somebody's rows.
    ///
    /// LAST, after EndsRow, for the reason spelled out at Group and LeadsTo: a new optional member
    /// anywhere earlier compiles clean and silently captures the value meant for the one after it.
    /// </summary>
    IReadOnlyList<ChartPopItemOption>? DropItems = null,
    /// <summary>
    /// Which add buttons THIS card shows, narrowing the board's own Features: Faust records only
    /// drops, so it gets no "Add pop item" button even though Sky takes pop items. Null means every
    /// item form the board offers. Only the PopItems and DropItems flags mean anything here.
    ///
    /// Declared rather than read off PopItems/DropItems: Kirin records drops but lists none, so its
    /// drop box is free text, and HENM takes pop items with no list at all.
    /// </summary>
    ChartBoardFeatures? ItemForms = null);

/// <summary>One board (Sky, Sea, …) and the bosses it tracks, in display order.</summary>
public sealed record ChartBoard(
    string Key,
    string Label,
    string AssetFolder,
    string Blurb,
    IReadOnlyList<ChartBoss> Bosses)
{
    /// <summary>
    /// Group labels drawn as vertical COLUMNS rather than as rows of the shared grid — Sky's four
    /// paths, where the two farm NMs that feed a god stack above it and the tiers line up sideways.
    /// Empty on every other board, which keeps them on the plain grid they have always used.
    ///
    /// Named labels rather than a bool on the board, because a board can have both: any run NOT
    /// named here renders underneath the columns, which is how Kirin sits below all four paths.
    /// Every label here must be a real run — ChartBoardCatalogTests pins that, and a typo would
    /// silently drop a column to the bottom of the page.
    /// </summary>
    public IReadOnlyList<string> PathColumns { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Draw as CENTRED rows of fixed-width cards rather than as a stretch-to-fit grid. A board says
    /// this when it has chosen its own row lengths — whether through <see cref="ChartBoss.EndsRow"/>
    /// (Dynamis, Limbus) or simply by making each group one row (HENM's tiers). A row whose length
    /// the author picked should look deliberate, not left-aligned with a ragged gap on the right.
    ///
    /// Declared rather than inferred from EndsRow: a board whose GROUPS are already its rows needs
    /// no breaks at all, and inferring would have forced it to set a redundant one just to opt in.
    /// </summary>
    public bool CentersRows { get; init; }

    /// <summary>
    /// What this board offers.
    ///
    /// An init property rather than a constructor parameter, exactly like PathColumns and
    /// CentersRows above: board-level switches are keyword-only here, which is what makes them
    /// immune to the positional hazard ChartBoss has to keep warning about.
    ///
    /// Defaulted to PopItems so a board that says nothing behaves the way every board did before
    /// drops, wishlists and key items existed.
    /// </summary>
    public ChartBoardFeatures Features { get; init; } = ChartBoardFeatures.PopItems;

    /// <summary>
    /// Gates the "Add a pop item" FORM only. Rows already on a board that turns this off stay
    /// listed, editable and deletable - ActivityDataController.AddChartPopItemAsync is the ONE
    /// place this is checked, and putting it anywhere ChartBoardService.NormalizeDraft can reach
    /// would strand every row Dynamis and Limbus already hold.
    /// </summary>
    public bool AllowsPopItems => Features.HasFlag(ChartBoardFeatures.PopItems);

    public bool AllowsDropItems => Features.HasFlag(ChartBoardFeatures.DropItems);

    public bool AllowsWishlist => Features.HasFlag(ChartBoardFeatures.Wishlist);

    public bool AllowsKeyItems => Features.HasFlag(ChartBoardFeatures.KeyItems);

    /// <summary>
    /// The key items tracked per member here, in the order the grid's columns go.
    ///
    /// ONE list on the BOARD rather than a field on each ChartBoss, for two reasons. The column
    /// order is a board-level fact - the access prerequisite first, then the zones in progression
    /// order - and deriving it from the card order would put Vial of Shrouded Sand nowhere. And a
    /// board-level key item belongs to no card at all, so there is no ChartBoss to hang it on.
    /// </summary>
    public IReadOnlyList<ChartKeyItem> KeyItems { get; init; } = Array.Empty<ChartKeyItem>();

    public ChartBoss? Find(string? boss) =>
        string.IsNullOrWhiteSpace(boss)
            ? null
            : Bosses.FirstOrDefault(item => string.Equals(item.Name, boss.Trim(), StringComparison.OrdinalIgnoreCase));

    public string EmblemPathFor(ChartBoss boss) => $"ffxi_assets/{AssetFolder}/{boss.EmblemFile}";

    /// <summary>The pop items a boss takes; empty when this board does not spell them out.</summary>
    public IReadOnlyList<ChartPopItemOption> PopItemsFor(string? boss) =>
        Find(boss)?.PopItems ?? Array.Empty<ChartPopItemOption>();

    /// <summary>
    /// The card a boss's drops feed, or null when it feeds nothing — or names a boss this board does
    /// not have, which is the failure a bad <see cref="ChartBoss.LeadsTo"/> produces: no badge,
    /// rather than an arrow painted in a hue nothing on the page carries.
    ///
    /// Returns the CARD, not just its key, so the badge's spelling and its colour both come off the
    /// thing it points at. Resolved here, once, so the website and the Activity cannot disagree
    /// about where an arrow points or what colour it is.
    /// </summary>
    public ChartBoss? LeadsToFor(ChartBoss boss) => Find(boss.LeadsTo);

    /// <summary>
    /// Whether ANY boss here declares pop items — i.e. whether this board needs the picker at all.
    ///
    /// Deliberately Any, not All. Sea mixes: Jailer of Temperance and two of the three Ix'aern are
    /// lottery or animosity spawns that take no trade item, while the rest of the board has a closed
    /// list. So the choice of picker-or-free-text is made per BOSS, and this only answers whether
    /// the board has to ship the machinery.
    /// </summary>
    public bool HasPopItemOptions => Bosses.Any(boss => boss.PopItems is { Count: > 0 });

    /// <summary>The drop items a boss is known to yield; empty when it does not spell them out.</summary>
    public IReadOnlyList<ChartPopItemOption> DropItemsFor(string? boss) =>
        Find(boss)?.DropItems ?? Array.Empty<ChartPopItemOption>();

    /// <summary>Twin of <see cref="HasPopItemOptions"/>, and Any for the same reason: boards MIX.</summary>
    public bool HasDropItemOptions => Bosses.Any(boss => boss.DropItems is { Count: > 0 });

    /// <summary>
    /// The pop or the drop list for a boss, chosen by kind. One resolver, so no caller picks a list
    /// for itself.
    ///
    /// Anything that is not Drop resolves to the pop list, which is what makes an absent or unknown
    /// kind behave exactly like the pop-only world this replaced.
    /// </summary>
    public IReadOnlyList<ChartPopItemOption> ItemOptionsFor(string? boss, string? kind) =>
        string.Equals(kind, ChartItemKinds.Drop, StringComparison.OrdinalIgnoreCase)
            ? DropItemsFor(boss)
            : PopItemsFor(boss);

    /// <summary>
    /// Whether an item of this kind may be ADDED to this card: the board must offer the form, and
    /// the card must not have narrowed it away (see <see cref="ChartBoss.ItemForms"/>). Edits never
    /// ask, for the reason AllowsPopItems gives.
    /// </summary>
    public bool TakesItemKind(ChartBoss boss, string? kind)
    {
        var form = ChartBoardCatalog.NormalizeItemKind(kind) == ChartItemKinds.Drop
            ? ChartBoardFeatures.DropItems
            : ChartBoardFeatures.PopItems;
        return Features.HasFlag(form) && (boss.ItemForms is null || boss.ItemForms.Value.HasFlag(form));
    }

    /// <summary>
    /// The key item earned on a card, or null. A board-level key item never matches: it belongs to
    /// no card, so it gets a grid column and no card badge.
    /// </summary>
    public ChartKeyItem? KeyItemFor(string? boss) =>
        string.IsNullOrWhiteSpace(boss)
            ? null
            : KeyItems.FirstOrDefault(item =>
                item.Boss is not null
                && string.Equals(item.Boss, boss.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// THE list of what the Charts boards track.
///
/// One catalog rather than a vocabulary per board, because every board is the same feature with a
/// different cast: same rows, same per-item credit, same derived ledger. A new board is an entry
/// here plus art, not another copy of the stack.
///
/// Boss names are stored verbatim on ChartPopItem.Boss, so renaming one here orphans existing rows —
/// it needs a data migration, exactly like renaming a ledger account.
/// </summary>
public static class ChartBoardCatalog
{
    public const string Sky = "Sky";
    public const string Sea = "Sea";
    public const string Dynamis = "Dynamis";
    public const string Limbus = "Limbus";
    public const string Henm = "HENM";

    // Sky is a CHAIN, and it is drawn as one COLUMN PER PATH: the two farm NMs that feed a god,
    // stacked above that god. Four columns, three cards each, so the tiers line up across them —
    // both farm rows read as rows, and the gods sit in one row underneath. Kirin, which every column
    // leads to, is the trailing run below them all.
    //
    // The runs ARE the columns here, which is why SkyBoard.Bosses below is ordered path by path
    // (Brigandish Blade, Faust, Suzaku, then Zipacna, …) rather than tier by tier: a run is still a
    // contiguous block of cards, exactly as on every other board — only its rendering differs.
    private static string SkyPathRun(string god) => $"{god} Path";
    private const string SkyFinalRun = "Final stage";

    /// <summary>A farm NM's card, minus its name: how it is themed, the ONE thing it drops, the god
    /// that drop pops, and - for the three that are popped with a trade - what is traded to it.</summary>
    private sealed record SkyFarmNmCard(
        string ThemeKey, string EmblemFile, string Drop, string Feeds, string? Pop = null);

    /// <summary>
    /// The eight Sky farm NMs. Keyed by NM so it cannot drift out of step with
    /// HnmConfig.SkyFarmNmOrder — the indexer below throws at startup if that list ever names an NM
    /// this dictionary does not, the same guard SkyGodCards has against SkyGodOrder.
    ///
    /// DECLARATION ORDER MATTERS: static field initializers run top-to-bottom, so this must stay
    /// above SkyBoard, which reads it. Below it, SkyBoard would index a null dictionary and turn
    /// startup into a TypeInitializationException a long way from the edit.
    ///
    /// No Source on any Drop. Source means "where this item comes from", and on this board the
    /// answer is the card the option is ON — naming it again would print "Gem of the South —
    /// Brigandish Blade" in a picker sitting beside a boss select that already says Brigandish
    /// Blade. What the item is FOR is a different fact, and it belongs to the card once, as LeadsTo,
    /// rather than to every option on it.
    ///
    /// Drop goes on the card's DROP list and Pop on its POP list. The other five are popped some
    /// other way, so their cards have no pop item at all.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, SkyFarmNmCard> SkyFarmNmCards =
        new Dictionary<string, SkyFarmNmCard>(StringComparer.OrdinalIgnoreCase)
        {
            // Snake_Case where the rest of the catalog is PascalCase — that is how the art was
            // delivered, and this list NAMES files rather than computing them precisely so a
            // delivery's own spelling never has to be argued with (see Sea's JailerOfForitude typo).
            // Delivered as PNG and re-encoded to JPEG: these are photos, and PNG cost ten times the
            // bytes for the same 256px thumbnail. Art dropped in later must be .jpg too, or these
            // eight lines need the new extension.
            ["Brigandish Blade"] = new("brigandish-blade", "Brigandish_Blade.jpg", "Gem of the South", "Suzaku", Pop: "Curtana"),
            ["Faust"]            = new("faust",            "Faust.jpg",            "Summerstone",      "Suzaku"),
            ["Zipacna"]          = new("zipacna",          "Zipacna.jpg",          "Gem of the North", "Genbu"),
            // Traded to the ??? that starts the Olla chain (Pequena, then Media, then Grande).
            ["Olla Grande"]      = new("olla-grande",      "Olla_Grande.jpg",      "Winterstone",      "Genbu",  Pop: "Ro'Maeve Water"),
            ["Steam Cleaner"]    = new("steam-cleaner",    "Steam_Cleaner.jpg",    "Gem of the East",  "Seiryu"),
            ["Mother Globe"]     = new("mother-globe",     "Mother_Globe.jpg",     "Springstone",      "Seiryu"),
            ["Despot"]           = new("despot",           "Despot.jpg",           "Gem of the West",  "Byakko"),
            ["Ullikummi"]        = new("ullikummi",        "Ullikummi.jpg",        "Autumnstone",      "Byakko", Pop: "Diorite"),
        };

    /// <summary>A Sky God's card, minus its name.</summary>
    /// <param name="Drops">What falls off this card and gets held. Null for Kirin: it is popped WITH
    /// the four seals, but nothing is tracked as dropping FROM it, so its card documents the fight.</param>
    private sealed record SkyGodCard(
        IReadOnlyList<ChartPopItemOption>? Drops,
        string? Subtitle = null);

    /// <summary>
    /// The five cards HnmConfig calls Sky Gods. Same keyed-by-name guard as SkyFarmNmCards, and the
    /// same DECLARATION ORDER MATTERS caveat.
    ///
    /// A seal is a DROP, and it lives on the god that drops it rather than on Kirin, which is what it
    /// pops. No god takes a tracked pop item: what pops a god is the farm NMs' drops above it.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, SkyGodCard> SkyGodCards =
        new Dictionary<string, SkyGodCard>(StringComparer.OrdinalIgnoreCase)
        {
            ["Suzaku"] = new(new[] { new ChartPopItemOption("Seal of Suzaku") }),
            ["Genbu"]  = new(new[] { new ChartPopItemOption("Seal of Genbu") }),
            ["Seiryu"] = new(new[] { new ChartPopItemOption("Seal of Seiryu") }),
            ["Byakko"] = new(new[] { new ChartPopItemOption("Seal of Byakko") }),

            // Standard rather than Final, deliberately: a Final card has no totals footer. Kirin
            // says what pops it in a subtitle and keeps the footer the four gods beside it have.
            ["Kirin"] = new(null, "Popped with one seal from each of the four gods"),
        };

    private static readonly ChartBoard SkyBoard = new(
        Sky,
        "Sky",
        "Sky",
        "Pop items, holders, and farming credit for Tu'Lia.",
        // Walked god by god in SkyGodOrder, emitting that god's feeders and then the god itself — so
        // each path is a CONTIGUOUS block of three cards, which is exactly one column. Both lists
        // come from HnmConfig, so the ToD vocabulary and the chart cannot disagree about who the farm
        // NMs and the Sky Gods are, and a card's column is decided by the LeadsTo it already carries
        // rather than by a second list saying which NM belongs to which path.
        //
        // Kirin is last in SkyGodOrder and has no feeders, so it falls out of this as a run of one.
        //
        // Every optional argument is passed BY NAME: the tail of ChartBoss is four optional string?s
        // and a list, and a positional argument there is the one edit that goes wrong silently.
        HnmConfig.SkyGodOrder
            .SelectMany(god =>
            {
                var run = god == "Kirin" ? SkyFinalRun : SkyPathRun(god);

                var feeders = HnmConfig.SkyFarmNmOrder
                    .Where(nm => string.Equals(SkyFarmNmCards[nm].Feeds, god, StringComparison.OrdinalIgnoreCase))
                    .Select(nm =>
                    {
                        var card = SkyFarmNmCards[nm];
                        return new ChartBoss(
                            nm,
                            card.ThemeKey,
                            card.EmblemFile,
                            // Farmed on a cycle rather than popped once, so the card headlines STOCK
                            // — the same call Jailer of Fortitude and Limbus's chip areas make.
                            ChartBossKinds.MiniNm,
                            Group: run,
                            PopItems: card.Pop is null ? null : new[] { new ChartPopItemOption(card.Pop) },
                            LeadsTo: card.Feeds,
                            DropItems: new[] { new ChartPopItemOption(card.Drop) },
                            ItemForms: card.Pop is null
                                ? ChartBoardFeatures.DropItems
                                : ChartBoardFeatures.PopItems | ChartBoardFeatures.DropItems);
                    });

                var godCard = SkyGodCards[god];
                return feeders.Append(new ChartBoss(
                    god,
                    god.ToLowerInvariant(),
                    $"{god}.jpg",
                    Subtitle: godCard.Subtitle,
                    Group: run,
                    DropItems: godCard.Drops,
                    // Every god records drops only - Kirin included, which lists none and so takes
                    // whatever is typed.
                    ItemForms: ChartBoardFeatures.DropItems));
            })
            .ToList())
    {
        // The four path runs are drawn as columns; anything else — Kirin's run — falls below them.
        // Pop items are what is traded TO a card (Curtana, Diorite, Ro'Maeve Water) and drop items
        // are what falls OFF it (the gems, stones and seals). ReclassifySkyChartItems moved the rows
        // written before the two were split.
        Features = ChartBoardFeatures.PopItems | ChartBoardFeatures.DropItems,
        PathColumns = HnmConfig.SkyGodOrder
            .Where(god => god != "Kirin")
            .Select(SkyPathRun)
            .ToList(),
    };

    // Sea is a CHAIN drawn as one COLUMN PER PATH, exactly like Sky: everything that feeds a
    // second-stage Jailer, stacked above that Jailer. Three columns, FIVE cards each, so the tiers
    // line up sideways — a trash family farmed for the NM below it, that NM, the NM beside it that
    // takes no trade at all, one more trash family, and the Jailer at the foot. Jailer of Love —
    // which every column leads to — and Absolute Virtue are the trailing run below them all.
    //
    // A card's items are what falls OFF it, so they are all DROP items: a virtue lives on the NM
    // that DROPS it, not on the one it pops. That rule is what lets the six trash families hold the
    // organs and chips, and it is why the ARROW BADGE — not the item list — is what says where a
    // drop is going. What POPS a card is said in its SUBTITLE, exactly as Kirin says it on Sky.
    //
    // Read a column top to bottom and you get the order you farm it in, which is the whole reason
    // the trash families earned cards: an officer holding four Xzomit organs had nowhere to record
    // them while the organ was listed under the Jailer it pops.
    //
    // The runs ARE the columns, which is why SeaBoard.Bosses below is ordered path by path rather
    // than stage by stage.
    //
    // Captions are the Jailer's short name, not its full one: three headings reading "Jailer of …"
    // over three cards reading "Jailer of …" is the same two words four times per column.
    private const string SeaJusticePath = "Justice Path";
    private const string SeaHopePath = "Hope Path";
    private const string SeaPrudencePath = "Prudence Path";
    private const string SeaFinalStage = "Final stage";

    // The six trash families have no portrait in the shipped Sea art, so they share the zone shot
    // the folder already carries — the same call Limbus makes, where fourteen cards share two zone
    // images. Named ONCE so dropping in per-family art is six files plus splitting this constant,
    // rather than six edits scattered through the board.
    private const string SeaTrashEmblem = "Sea.jpg";

    private static readonly ChartBoard SeaBoard = new(
        Sea,
        "Sea",
        "Sea",
        "Pop items, holders, and farming credit for Lumoria NMs.",
        // The items below are a CLOSED chain read DOWNWARDS: a column's trash family drops the organs
        // that pop the NM under it, that NM drops the virtue or deed that pops the Jailer at the foot,
        // and the Jailer drops the virtue Jailer of Love takes. Every card holds exactly what falls
        // off it, so nothing is listed twice and nothing an officer can hold is listed nowhere.
        //
        // No Source on any option, for the reason Sky names none: an item's drop source IS the card it
        // sits on now, and repeating it would print "High-Quality Xzomit Organ — Xzomit" in a picker
        // sitting beside a boss select that already says Xzomit.
        new[]
        {
            // ---- Justice Path ----
            //
            // Farm chips → pop Fortitude for its virtue; take Moderation off an animosity spawn; farm
            // one more organ. Those three pop Jailer of Justice, which drops what Love takes.

            // Farmed rather than popped in one trade — twelve chips is a stock problem, so its card
            // headlines how many are held rather than how many rows it has. Same call Sky's farm NMs
            // and Limbus's chip areas make.
            new ChartBoss(
                "Ghrah",
                "ghrah",
                SeaTrashEmblem,
                ChartBossKinds.MiniNm,
                "Farmed trash mob",
                Group: SeaJusticePath,
                DropItems: new[] { new ChartPopItemOption("Ghrah M Chip", Needed: "12") },
                LeadsTo: "Jailer of Fortitude"),

            new ChartBoss(
                "Jailer of Fortitude",
                "fortitude",
                // Misspelled in the shipped asset. Fixing the file would break the published build
                // output that already references it; the typo lives here instead of in a path helper.
                "JailerOfForitude.jpg",
                Subtitle: "Popped with 12 Ghrah M Chips",
                Group: SeaJusticePath,
                DropItems: new[] { new ChartPopItemOption("Second Virtue") },
                LeadsTo: "Jailer of Justice"),

            // One mob in three jobs, but three emblems: the shipped art draws each job separately, so
            // the cards do too rather than sharing one file the way the trash families share a zone's.
            new ChartBoss(
                "Ix'aern (DRK)",
                "ixaern-drk",
                "IxDRK.jpg",
                Subtitle: "Animosity spawn",
                Group: SeaJusticePath,
                DropItems: new[] { new ChartPopItemOption("Deed of Moderation") },
                LeadsTo: "Jailer of Justice"),

            new ChartBoss(
                "Xzomit",
                "xzomit",
                SeaTrashEmblem,
                ChartBossKinds.MiniNm,
                "Farmed trash mob",
                Group: SeaJusticePath,
                DropItems: new[] { new ChartPopItemOption("High-Quality Xzomit Organ") },
                LeadsTo: "Jailer of Justice"),

            new ChartBoss(
                "Jailer of Justice",
                "justice",
                "JailerofJustice.jpg",
                Subtitle: "Popped with Second Virtue + Deed of Moderation + HQ Xzomit Organ",
                Group: SeaJusticePath,
                DropItems: new[] { new ChartPopItemOption("Fourth Virtue") }),

            // ---- Hope Path ----

            new ChartBoss(
                "Aern",
                "aern",
                SeaTrashEmblem,
                ChartBossKinds.MiniNm,
                "Farmed trash mob",
                Group: SeaHopePath,
                DropItems: new[] { new ChartPopItemOption("High-Quality Aern Organ", Needed: "1–3") },
                LeadsTo: "Ix'aern (MNK)"),

            new ChartBoss(
                "Ix'aern (MNK)",
                "ixaern-mnk",
                "IxMNK.jpg",
                Subtitle: "3 organs guarantees a deed or vice",
                Group: SeaHopePath,
                DropItems: new[] { new ChartPopItemOption("Deed of Placidity") },
                LeadsTo: "Jailer of Hope"),

            new ChartBoss(
                "Jailer of Temperance",
                "temperance",
                "JailerOfTemperance.jpg",
                Subtitle: "Lottery spawn",
                Group: SeaHopePath,
                DropItems: new[] { new ChartPopItemOption("First Virtue") },
                LeadsTo: "Jailer of Hope"),

            new ChartBoss(
                "Phuabo",
                "phuabo",
                SeaTrashEmblem,
                ChartBossKinds.MiniNm,
                "Farmed trash mob",
                Group: SeaHopePath,
                DropItems: new[] { new ChartPopItemOption("High-Quality Phuabo Organ") },
                LeadsTo: "Jailer of Hope"),

            new ChartBoss(
                "Jailer of Hope",
                "hope",
                "JailerOfHope.jpg",
                Subtitle: "Popped with First Virtue + Deed of Placidity + HQ Phuabo Organ",
                Group: SeaHopePath,
                DropItems: new[] { new ChartPopItemOption("Fifth Virtue") }),

            // ---- Prudence Path ----

            new ChartBoss(
                "Euvhi",
                "euvhi",
                SeaTrashEmblem,
                ChartBossKinds.MiniNm,
                "Farmed trash mob",
                Group: SeaPrudencePath,
                DropItems: new[] { new ChartPopItemOption("High-Quality Euvhi Organ", Needed: "1") },
                LeadsTo: "Jailer of Faith"),

            new ChartBoss(
                "Jailer of Faith",
                "faith",
                "JailerOfFaith.jpg",
                Subtitle: "Popped with one HQ Euvhi Organ",
                Group: SeaPrudencePath,
                DropItems: new[] { new ChartPopItemOption("Third Virtue") },
                LeadsTo: "Jailer of Prudence"),

            new ChartBoss(
                "Ix'aern (DRG)",
                "ixaern-drg",
                "IxDRG.jpg",
                Subtitle: "Lottery from Aw'aern placeholders",
                Group: SeaPrudencePath,
                DropItems: new[] { new ChartPopItemOption("Deed of Sensibility") },
                LeadsTo: "Jailer of Prudence"),

            new ChartBoss(
                "Hpemde",
                "hpemde",
                SeaTrashEmblem,
                ChartBossKinds.MiniNm,
                "Farmed trash mob",
                Group: SeaPrudencePath,
                DropItems: new[] { new ChartPopItemOption("High-Quality Hpemde Organ") },
                LeadsTo: "Jailer of Prudence"),

            new ChartBoss(
                "Jailer of Prudence",
                "prudence",
                "JailerofPrudence.jpg",
                Subtitle: "Popped with Third Virtue + Deed of Sensibility + HQ Hpemde Organ",
                Group: SeaPrudencePath,
                DropItems: new[] { new ChartPopItemOption("Sixth Virtue") }),

            // ---- Final stage: what all three columns lead to ----
            //
            // The three Jailers above carry no LeadsTo, exactly as Sky's four gods do not: three
            // badges all pointing at the card directly below them says less than the column already
            // does. The arrow is a FEEDER's affordance — it earns its place when the columns collapse
            // on a narrow screen and nothing else says which path a card belongs to.

            // The terminus, and the one card on a path that holds nothing: it is popped WITH the three
            // second-stage virtues, but nothing is tracked as dropping FROM it, so it documents its
            // own pop in a subtitle and keeps the totals footer. Same shape as Kirin at the foot of
            // Sky. The virtues it takes are held on the three Jailers that drop them.
            new ChartBoss(
                "Jailer of Love",
                "love",
                "JailerofLove.jpg",
                Subtitle: "Popped with the fourth, fifth, and sixth virtues",
                Group: SeaFinalStage),

            new ChartBoss(
                "Absolute Virtue",
                "absolute",
                "AbsoluteVirtue.jpg",
                ChartBossKinds.Final,
                "Final encounter.",
                new[]
                {
                    "Virtue Stone Pouch",
                    "Luminion Chip",
                    "Merit Point Bonus",
                    "NQ Organ drops",
                },
                "All sins must be traded to Yurin in the Name of Science.",
                SeaFinalStage),
        })
    {
        // The three path runs are drawn as columns; the Final stage run falls below them.
        //
        // DROPS ONLY, on every card: everything listed here falls off the card it sits on, so the
        // pop form has nothing to record. Set on the board rather than per card - no Sea card is an
        // exception. DropOnlyChartCards turned the rows saved as pop items into drops.
        Features = ChartBoardFeatures.DropItems,
        PathColumns = new[] { SeaJusticePath, SeaHopePath, SeaPrudencePath },
    };

    // Display captions, not keys — nothing is stored against them, so renaming one is a caption
    // change. The two runs must stay CONTIGUOUS in DynamisBoard.Bosses below: the app opens a
    // heading when a card's group differs from the previous card's, so interleaving them would print
    // a heading twice and split one group across the board.
    private const string DynamisOriginal = "Original Dynamis";
    private const string DynamisDreamworld = "Dreamworld Dynamis";

    private static readonly ChartBoard DynamisBoard = new(
        Dynamis,
        "Dynamis",
        "Dynamis",
        "Pop items, holders, and farming credit for the Dynamis zones.",
        // Per-zone entrance screenshots, PNG rather than Sky/Sea's JPG and carrying a redundant
        // "Dynamis-" prefix inside a folder already called Dynamis — both kept exactly as the art
        // was delivered, because this list names files rather than computing them. Live in
        // discord-activity/public/ffxi_assets/Dynamis/; ChartBoardCatalogTests.EveryBossEmblem_ExistsOnDisk
        // fails if one is missed. Note "San-dOria" — the apostrophe is dropped in the file name, not
        // in the zone's.
        new[]
        {
            // Bare zone names rather than "Dynamis - San d'Oria": the board already says Dynamis,
            // and these head a one-word column in the ledger. Stored verbatim on ChartPopItem.Boss,
            // so renaming one here orphans rows.
            //
            // No Subtitle on any card. Sea's subtitles exist only to explain its two special Kinds;
            // every Dynamis zone is an ordinary card, and a static subtitle would compete for the
            // line the officers' progress note uses.
            // Laid out in rows the board picks rather than however many happen to fit: the three
            // nations and Jeuno together, then the two Xarcabard-era zones, then the three original
            // Dreamworld areas, then Tavnazia alone. Each row is centred.
            //
            // EndsRow is only needed INSIDE a run — the Dreamworld heading is full-width and already
            // breaks the row after Xarcabard, and Tavnazia is the last card on the board.
            new ChartBoss("San d'Oria", "sandoria", "Dynamis-San-dOria.png", Group: DynamisOriginal),
            new ChartBoss("Bastok", "bastok", "Dynamis-Bastok.png", Group: DynamisOriginal),
            new ChartBoss("Windurst", "windurst", "Dynamis-Windurst.png", Group: DynamisOriginal),
            new ChartBoss("Jeuno", "jeuno", "Dynamis-Jeuno.png", Group: DynamisOriginal, EndsRow: true),
            new ChartBoss("Beaucedine", "beaucedine", "Dynamis-Beaucedine.png", Group: DynamisOriginal),
            new ChartBoss("Xarcabard", "xarcabard", "Dynamis-Xarcabard.png", Group: DynamisOriginal),

            new ChartBoss("Valkurm", "valkurm", "Dynamis-Valkurm.png", Group: DynamisDreamworld),
            new ChartBoss("Buburimu", "buburimu", "Dynamis-Buburimu.png", Group: DynamisDreamworld),
            new ChartBoss("Qufim", "qufim", "Dynamis-Qufim.png", Group: DynamisDreamworld, EndsRow: true),
            new ChartBoss("Tavnazia", "tavnazia", "Dynamis-Tavnazia.png", Group: DynamisDreamworld),
        })
    {
        CentersRows = true,

        // NO PopItems flag. Every zone here took free text and nobody agreed on what to type, so the
        // add form is replaced by the wishlist: on this board the useful fact is what a member WANTS
        // out of a zone, not what the linkshell is sitting on. Rows already entered stay listed,
        // editable and deletable - see ChartBoard.AllowsPopItems for where that is enforced.
        Features = ChartBoardFeatures.Wishlist | ChartBoardFeatures.KeyItems,

        // The eleven permanent Dynamis key items, in PROGRESSION order, which is the order the grid
        // draws its columns in: the access prerequisite, the four city clears that open Beaucedine,
        // Beaucedine's own key to Xarcabard, Xarcabard's completion key, then the three Dreamworld
        // Slivers that open Tavnazia and Tavnazia's own.
        //
        // Vial of Shrouded Sand names NO boss. It is earned outside the zones, so it gets a grid
        // column and no card badge - ChartBoard.KeyItemFor is what makes that fall out rather than
        // needing a special case.
        //
        // Plain ASCII hyphens in the Sliver names, never an en dash. These strings are STORED on
        // ChartMemberKeyItem.KeyItemName and compared, and an en dash that survives a copy-paste
        // into one file and not another renders as an empty column with no error anywhere.
        // ChartBoardCatalogTests.KeyItemNamesAreAsciiOnly is the guard.
        KeyItems = new[]
        {
            new ChartKeyItem("Vial of Shrouded Sand", Caption: "Opens the original Dynamis zones."),

            new ChartKeyItem("Hydra Corps Command Scepter", "San d'Oria", "One of four needed for Beaucedine."),
            new ChartKeyItem("Hydra Corps Eyeglass", "Bastok", "One of four needed for Beaucedine."),
            new ChartKeyItem("Hydra Corps Lantern", "Windurst", "One of four needed for Beaucedine."),
            new ChartKeyItem("Hydra Corps Tactical Map", "Jeuno", "One of four needed for Beaucedine."),
            new ChartKeyItem("Hydra Corps Insignia", "Beaucedine", "Needed for Xarcabard."),
            new ChartKeyItem("Hydra Corps Battle Standard", "Xarcabard"),

            new ChartKeyItem("Dynamis - Valkurm Sliver", "Valkurm", "One of three needed for Tavnazia."),
            new ChartKeyItem("Dynamis - Buburimu Sliver", "Buburimu", "One of three needed for Tavnazia."),
            new ChartKeyItem("Dynamis - Qufim Sliver", "Qufim", "One of three needed for Tavnazia."),
            new ChartKeyItem("Dynamis - Tavnazia Sliver", "Tavnazia"),
        },
    };

    // Same contiguity rule as Dynamis: the two runs must not interleave in LimbusBoard.Bosses.
    private const string LimbusApollyon = "Apollyon";
    private const string LimbusTemenos = "Temenos";

    private static readonly ChartBoard LimbusBoard = new(
        Limbus,
        "Limbus",
        "Limbus",
        "Pop items, holders, and farming credit for the Limbus areas.",
        // TWO images for fourteen cards, one per zone — the areas share a zone's art because that is
        // the distinction that matters here, and the group heading above the card already names it.
        // Both live in discord-activity/public/ffxi_assets/Limbus/.
        new[]
        {
            // Bare area names: the group heading already says Apollyon or Temenos, and this board
            // puts FOURTEEN columns in the ledger, where a one-word header is the difference between
            // a readable table and a horizontal scroll. Stored verbatim on ChartPopItem.Boss.
            //
            // Laid out in rows the board picks, each centred: the four outer areas, then the fight
            // they open, then the optional chip area on a row of its own. Temenos runs the same
            // shape one step longer — three towers, three central floors, the fight, the chip area.
            //
            // EndsRow is only needed INSIDE a run: the Temenos heading is full-width and already
            // breaks the row after CS, and Central B1 is the last card on the board.
            //
            // Each area that rewards a chip lists it as the card's drop, so the card shows it at 0
            // until somebody holds one (the four outer chips open Central). Central and CS reward
            // none and stay free text.
            new ChartBoss("NW", "apollyon-nw", "Apollyon.png", Group: LimbusApollyon,
                DropItems: new[] { new ChartPopItemOption("Magenta Chip") }),
            new ChartBoss("SW", "apollyon-sw", "Apollyon.png", Group: LimbusApollyon,
                DropItems: new[] { new ChartPopItemOption("Charcoal Chip") }),
            new ChartBoss("NE", "apollyon-ne", "Apollyon.png", Group: LimbusApollyon,
                DropItems: new[] { new ChartPopItemOption("Smoky Chip") }),
            new ChartBoss("SE", "apollyon-se", "Apollyon.png", Group: LimbusApollyon, EndsRow: true,
                DropItems: new[] { new ChartPopItemOption("Smalt Chip") }),

            new ChartBoss(
                "Central",
                "apollyon-central",
                "Apollyon.png",
                ChartBossKinds.Final,
                "Proto-Omega.",
                Group: LimbusApollyon,
                EndsRow: true),

            // Metal Chips are farmed here and combine into the passes that open the other areas, so
            // this card headlines stock rather than a one-off pop — the same reason Jailer of
            // Fortitude is a Mini NM on Sea. Optional, and on a row of its own: it is not a step on
            // the way to Proto-Omega, it is the farm that pays for the steps.
            new ChartBoss(
                "CS",
                "apollyon-cs",
                "Apollyon.png",
                ChartBossKinds.MiniNm,
                "Optional · Metal Chip area.",
                Group: LimbusApollyon),

            // The towers' chips open the central floors (Emerald 1F, Scarlet 2F, Ivory 3F), and the
            // floors' three open 4F. Central 4F and B1 reward none and stay free text.
            new ChartBoss("Western Tower", "temenos-west", "Temenos.png", Group: LimbusTemenos,
                DropItems: new[] { new ChartPopItemOption("Emerald Chip") }),
            new ChartBoss("Eastern Tower", "temenos-east", "Temenos.png", Group: LimbusTemenos,
                DropItems: new[] { new ChartPopItemOption("Scarlet Chip") }),
            new ChartBoss("Northern Tower", "temenos-north", "Temenos.png", Group: LimbusTemenos, EndsRow: true,
                DropItems: new[] { new ChartPopItemOption("Ivory Chip") }),

            new ChartBoss("Central 1F", "temenos-1f", "Temenos.png", Group: LimbusTemenos,
                DropItems: new[] { new ChartPopItemOption("Orchid Chip") }),
            new ChartBoss("Central 2F", "temenos-2f", "Temenos.png", Group: LimbusTemenos,
                DropItems: new[] { new ChartPopItemOption("Cerulean Chip") }),
            new ChartBoss("Central 3F", "temenos-3f", "Temenos.png", Group: LimbusTemenos, EndsRow: true,
                DropItems: new[] { new ChartPopItemOption("Silver Chip") }),

            new ChartBoss(
                "Central 4F",
                "temenos-4f",
                "Temenos.png",
                ChartBossKinds.Final,
                "Proto-Ultima.",
                Group: LimbusTemenos,
                EndsRow: true),

            new ChartBoss(
                "Central B1",
                "temenos-b1",
                "Temenos.png",
                ChartBossKinds.MiniNm,
                "Optional · Metal Chip area.",
                Group: LimbusTemenos),
        })
    {
        CentersRows = true,

        // Requests, plus DROP items on every card. The ten areas that reward a chip list it (a picker
        // and a 0 line on the card); Central, CS, Central 4F and B1 list none and keep free text. Still
        // no pop form: nothing on this board is popped with a tracked trade. No key items here -
        // Limbus passes are consumed rather than kept, so there is no permanent per-member state.
        Features = ChartBoardFeatures.Wishlist | ChartBoardFeatures.DropItems,
    };

    // HENM : Zilart is a TIER ladder — three NMs per tier, each tier unlocking the next, ending at
    // the Council. The tiers are the rows, so no card needs to say where its row ends: a group
    // heading is full width and breaks the row on its own. CentersRows is what makes those rows sit
    // centred at card width rather than stretching three cards across the page.
    private const string HenmTier1 = "Tier 1 · 50 Cerulean Shards";
    private const string HenmTier2 = "Tier 2";
    private const string HenmTier3 = "Tier 3";
    private const string HenmTier4 = "Tier 4";

    // A tier's SHARED pop items: one set opens every NM in the tier, so they belong to the tier, not
    // to any one card. First in its run, so the strip sits right under the tier heading. Rows are
    // filed under its name ("Tier 1"), which is a stored key like any boss name.
    //
    // HENM.jpg is never drawn - a strip has no emblem - but every entry names a real file, which
    // ChartBoardCatalogTests.EveryBossEmblem_ExistsOnDisk checks. Each tier's colour matches its
    // NMs' (see _tokens.scss).
    //
    // popItem is the tier's pop item, listed on the strip at 0 until held and offered in its add
    // picker. Its Source is what it costs, shown beside the name. Null for a tier not released yet,
    // whose strip keeps a free-text box.
    private static ChartBoss HenmTierItems(
        string name, string themeKey, string group, ChartPopItemOption? popItem = null) =>
        new(name, themeKey, "HENM.jpg", ChartBossKinds.Tier, Group: group,
            PopItems: popItem is null ? null : new[] { popItem });

    // An NM card. It records no items of its own - its tier's strip holds them - so it shows no
    // item table and no add button.
    private static ChartBoss HenmNm(
        string name, string themeKey, string emblemFile, string group, string? subtitle = null) =>
        new(name, themeKey, emblemFile, Subtitle: subtitle, Group: group, ItemForms: ChartBoardFeatures.None);

    private static readonly ChartBoard HenmBoard = new(
        Henm,
        "HENM",
        "HENM",
        "Pop items, holders, and farming credit for the Zilart HENM ladder.",
        // Emblem file names are Snake_Case as delivered, and "Ruinous_Rocks.jpg" carries a k the NM's
        // own name does not — this list names files rather than computing them precisely so a
        // delivery's spelling never has to be argued with.
        new[]
        {
            // Tier 1 carries the zone it is camped in; the ladder above it is the same NMs wherever
            // they are found, so only this tier says where.
            // Only Tiers 1 and 2 are released; 3 and 4 get their pop items when they are.
            HenmTierItems("Tier 1", "henm-tier-1", HenmTier1,
                new ChartPopItemOption("Faded Stone", Source: "50,000 gil")),
            HenmNm("Ruinous Rocs", "henm-rocs", "Ruinous_Rocks.jpg", HenmTier1, "Rolanberry Fields"),
            HenmNm("Despotic Decapod", "henm-decapod", "Despotic_Decapod.jpg", HenmTier1, "Jugner Forest"),
            HenmNm("Sacred Scorpions", "henm-scorpions", "Sacred_Scorpions.jpg", HenmTier1, "Sauromugue Champaign"),

            HenmTierItems("Tier 2", "henm-tier-2", HenmTier2,
                new ChartPopItemOption("Faded Gem", Source: "100,000 gil")),
            HenmNm("Mammet-9999", "henm-mammet", "Mammet_9999.jpg", HenmTier2),
            HenmNm("Tonberry Sovereign", "henm-tonberry", "Tonberry_Sovereign.jpg", HenmTier2),
            HenmNm("Ultimega", "henm-ultimega", "Ultimega.jpg", HenmTier2),

            HenmTierItems("Tier 3", "henm-tier-3", HenmTier3),
            HenmNm("Primordial Behemoth", "henm-behemoth", "Primordial_Behemoth.jpg", HenmTier3),
            HenmNm("Minogame", "henm-minogame", "Minogame.jpg", HenmTier3),
            HenmNm("IO", "henm-io", "IO.jpg", HenmTier3),

            // Standard rather than Final: a Final card with no Rewards list renders a reward strip
            // reading "Rewards 0". Same call Kirin makes.
            HenmTierItems("Tier 4", "henm-tier-4", HenmTier4),
            HenmNm("Council of the Zilart", "henm-council", "Council_of_the_Zilart.jpg", HenmTier4,
                "The ladder ends here"),
        })
    {
        CentersRows = true,

        // The only board that keeps its pop form AND takes requests. The ladder really is popped
        // with tracked items, and it is also the board people queue up wanting things off, so
        // neither affordance replaces the other. The pop form only reaches the four tier strips:
        // every NM card narrows it away with ItemForms.
        Features = ChartBoardFeatures.PopItems | ChartBoardFeatures.Wishlist,
    };

    /// <summary>Every board, in the order the Charts sub-nav shows them.</summary>
    public static readonly IReadOnlyList<ChartBoard> Boards =
        new[] { SkyBoard, SeaBoard, DynamisBoard, LimbusBoard, HenmBoard };

    /// <summary>The board for a key, however it was cased. Null for anything unknown.</summary>
    public static ChartBoard? Find(string? board) =>
        string.IsNullOrWhiteSpace(board)
            ? null
            : Boards.FirstOrDefault(item => string.Equals(item.Key, board.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Canonical board key ("sea" → "Sea"), or null when it is not a board.</summary>
    public static string? NormalizeBoard(string? board) => Find(board)?.Key;

    /// <summary>
    /// Canonical boss name within a board ("jailer of love" → "Jailer of Love"), or null when the
    /// boss does not belong to that board. Callers reject rather than store a row that would group
    /// under no card and be invisible.
    /// </summary>
    public static string? NormalizeBoss(string? board, string? boss) => Find(board)?.Find(boss)?.Name;

    /// <summary>
    /// The catalog's spelling of a pop item when the name given is one it knows ("winterstone" →
    /// "Winterstone"), and the trimmed name unchanged otherwise. Null only for a blank name.
    ///
    /// Canonicalises rather than rejects, deliberately. Boards that declare no items still take free
    /// text, and rows written before Sky had a list must stay editable under the name they were
    /// saved with — a picker that refused them would strand them. The gain is that ItemName, which
    /// nothing else canonicalises, cannot end up holding two spellings of one item.
    /// </summary>
    /// <remarks>Kept as a Pop-kind shim over <see cref="NormalizeItemName"/> rather than deleted:
    /// it is the spelling every caller that predates drop items uses, and the contract is
    /// unchanged.</remarks>
    public static string? NormalizePopItemName(string? board, string? boss, string? itemName) =>
        NormalizeItemName(board, boss, itemName, ChartItemKinds.Pop);

    /// <summary>
    /// Canonical item kind. Anything that is not Drop, INCLUDING null and blank, is Pop.
    ///
    /// Defaults rather than rejects, deliberately: a form or a payload that says nothing about kind
    /// is one written before drops existed, and the right reading of silence is the original
    /// behaviour. The board still decides whether a kind may be ADDED at all - see
    /// <see cref="ChartBoard.AllowsDropItems"/> - so this being permissive costs nothing.
    /// </summary>
    public static string NormalizeItemKind(string? kind) =>
        string.Equals(kind?.Trim(), ChartItemKinds.Drop, StringComparison.OrdinalIgnoreCase)
            ? ChartItemKinds.Drop
            : ChartItemKinds.Pop;

    /// <summary>
    /// The catalog's spelling of an item when the name given is one the boss's list for that KIND
    /// knows ("winterstone" to "Winterstone"), and the trimmed name unchanged otherwise. Null only
    /// for a blank name.
    ///
    /// Canonicalises rather than rejects, deliberately. Boards that declare no items still take free
    /// text, and rows written before Sky had a list must stay editable under the name they were
    /// saved with - a picker that refused them would strand them. The gain is that ItemName, which
    /// nothing else canonicalises, cannot end up holding two spellings of one item.
    /// </summary>
    public static string? NormalizeItemName(string? board, string? boss, string? itemName, string? kind)
    {
        var typed = itemName?.Trim();
        if (string.IsNullOrWhiteSpace(typed))
        {
            return null;
        }

        var known = Find(board)?
            .ItemOptionsFor(boss, NormalizeItemKind(kind))
            .FirstOrDefault(option => string.Equals(option.Name, typed, StringComparison.OrdinalIgnoreCase));

        return known?.Name ?? typed;
    }

    /// <summary>
    /// Canonical key item name on a board, or null when the board does not track that one.
    ///
    /// REJECTS unknown names, unlike <see cref="NormalizeItemName"/> directly above. A key item list
    /// is CLOSED - it is progression, not inventory - and the grid draws one column per catalog
    /// entry, so a name the catalog does not have would write a row that appears in no column at
    /// all. That reads as data loss rather than as a typo, which is why this refuses instead.
    /// </summary>
    public static string? NormalizeKeyItemName(string? board, string? keyItemName)
    {
        var typed = keyItemName?.Trim();
        if (string.IsNullOrWhiteSpace(typed))
        {
            return null;
        }

        return Find(board)?
            .KeyItems
            .FirstOrDefault(item => string.Equals(item.Name, typed, StringComparison.OrdinalIgnoreCase))?
            .Name;
    }
}
