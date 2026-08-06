using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Xunit;

namespace LinkshellManager.Tests;

// The catalog is the only place that says what a board tracks. Boss names are stored verbatim on
// ChartPopItem.Boss, so a rename here orphans rows — these tests make the blast radius visible.
public class ChartBoardCatalogTests
{
    /// <summary>
    /// The Sky board is PROJECTED from HnmConfig — the same vocabulary that gives a Sky farm NM a
    /// 2-hour ToD cooldown and a god a 5-minute one, so the two can never disagree about who is
    /// which. SkyGodOrder already ends with Kirin, which is why the two ordered lists concatenated
    /// ARE the board's thirteen cards.
    /// </summary>
    [Fact]
    public void SkyBoard_IsProjectedFromHnmConfig()
    {
        var sky = ChartBoardCatalog.Find(ChartBoardCatalog.Sky)!;

        // Path by path: each god's two feeders, then the god. That order IS the column layout — a
        // run is a contiguous block of three cards, and the grid fills column-wise.
        Assert.Equal(
            new[]
            {
                "Faust", "Brigandish Blade", "Suzaku",
                "Zipacna", "Olla Grande", "Genbu",
                "Steam Cleaner", "Mother Globe", "Seiryu",
                "Despot", "Ullikummi", "Byakko",
                "Kirin",
            },
            sky.Bosses.Select(boss => boss.Name).ToArray());

        // Still every name both HnmConfig lists carry, and nothing else.
        Assert.Equal(
            HnmConfig.SkyFarmNmOrder.Concat(HnmConfig.SkyGodOrder).OrderBy(n => n, StringComparer.Ordinal),
            sky.Bosses.Select(boss => boss.Name).OrderBy(n => n, StringComparer.Ordinal));

        // The unordered set the ToD cooldown lookup reads is REBUILT from the ordered list, the same
        // way SkyGods is rebuilt from SkyGodOrder.
        Assert.Equal(
            HnmConfig.SkyFarmNmOrder.OrderBy(name => name, StringComparer.Ordinal),
            HnmConfig.SkyFarmNms.OrderBy(name => name, StringComparer.Ordinal));

        // Every arrow points at a name that vocabulary itself calls a Sky God.
        Assert.All(
            sky.Bosses.Where(boss => boss.LeadsTo is not null),
            boss => Assert.True(HnmConfig.IsSkyGod(boss.LeadsTo)));
    }

    /// <summary>
    /// Sky's runs are its COLUMNS: one per god, holding that god's two feeders and the god itself,
    /// then Kirin's run below them all.
    /// </summary>
    [Fact]
    public void SkyBoard_DeclaresAPathRunPerGod_ThenKirin()
    {
        var sky = ChartBoardCatalog.Find(ChartBoardCatalog.Sky)!;

        Assert.Equal(
            new[] { "Suzaku Path", "Genbu Path", "Seiryu Path", "Byakko Path", "Final stage" },
            sky.Bosses.Select(boss => boss.Group!).Distinct().ToArray());

        // The four paths are the columns, in that order; Kirin's run is NOT one, so it renders below.
        Assert.Equal(
            new[] { "Suzaku Path", "Genbu Path", "Seiryu Path", "Byakko Path" },
            sky.PathColumns.ToArray());
        Assert.Equal("Kirin", sky.Bosses.Single(boss => boss.Group == "Final stage").Name);
    }

    /// <summary>
    /// Every path column holds the SAME number of cards. The columns are one grid with explicit rows
    /// — that is what lines the tiers up sideways — so a path with a card more or less would push
    /// its neighbours' rows out of step with it, and the misalignment is the only symptom.
    ///
    /// Also checks every declared column names a real run: a typo there drops a whole column out of
    /// the grid and into the trailing block below it, silently.
    /// </summary>
    [Fact]
    public void EveryPathColumn_NamesARealRun_AndTheyAreAllTheSameHeight()
    {
        foreach (var board in ChartBoardCatalog.Boards)
        {
            var heights = new List<int>();

            foreach (var label in board.PathColumns)
            {
                var cards = board.Bosses.Count(boss => boss.Group == label);
                Assert.True(cards > 0, $"{board.Key}: path column '{label}' names no run.");
                heights.Add(cards);
            }

            Assert.True(
                heights.Distinct().Count() <= 1,
                $"{board.Key}: path columns hold {string.Join("/", heights)} cards — the tiers will not line up.");
        }
    }

    /// <summary>
    /// The whole farm layer: what each NM drops and which god that drop pops. The drop names are
    /// STORED on ChartPopItem.ItemName when an officer picks one, and the god name decides where a
    /// card's arrow points and what colour it is — so both halves are pinned here.
    /// </summary>
    [Fact]
    public void SkyFarmNms_EachDropOneItemAndFeedOneGod()
    {
        var sky = ChartBoardCatalog.Find(ChartBoardCatalog.Sky)!;

        var expected = new (string Nm, string Drop, string God)[]
        {
            ("Faust",            "Summerstone",      "Suzaku"),
            ("Brigandish Blade", "Gem of the South", "Suzaku"),
            ("Zipacna",          "Gem of the North", "Genbu"),
            ("Olla Grande",      "Winterstone",      "Genbu"),
            ("Steam Cleaner",    "Gem of the East",  "Seiryu"),
            ("Mother Globe",     "Springstone",      "Seiryu"),
            ("Despot",           "Gem of the West",  "Byakko"),
            ("Ullikummi",        "Autumnstone",      "Byakko"),
        };

        foreach (var (nm, drop, god) in expected)
        {
            var card = sky.Find(nm)!;
            // Farmed rather than popped once, so the card headlines stock, not item count.
            Assert.Equal(ChartBossKinds.MiniNm, card.Kind);
            Assert.Equal(drop, Assert.Single(card.PopItems!).Name);
            Assert.Equal(god, card.LeadsTo);

            // A feeder sits in its god's column, above the god — which is what makes the arrow badge
            // and the layout say the same thing.
            Assert.Equal($"{god} Path", card.Group);
            Assert.Equal(card.Group, sky.Find(god)!.Group);
            Assert.True(
                sky.Bosses.ToList().IndexOf(card) < sky.Bosses.ToList().IndexOf(sky.Find(god)!),
                $"{nm} must come before {god} — the god is the bottom card of its column.");
        }
    }

    /// <summary>
    /// A LeadsTo names a BOSS, and the badge takes its hue from that boss's card. A name this board
    /// does not have renders no badge at all — silent, so it is pinned here — and a card pointing at
    /// ITSELF would be a column with no foot.
    ///
    /// A theme-key check, not a hue check, and on a path board those now differ on purpose: every
    /// card in a column shares its god's HUE while keeping its own KEY, so the badge is the same
    /// colour as the card it sits on. That is the design — the colour says which path you are in,
    /// and the badge's job is the NAME, which is what survives when the columns collapse on a narrow
    /// screen. See _tokens.scss beside the assignment.
    /// </summary>
    [Fact]
    public void EveryLeadsTo_NamesAnotherBossOnTheSameBoard()
    {
        foreach (var board in ChartBoardCatalog.Boards)
        {
            foreach (var boss in board.Bosses.Where(candidate => candidate.LeadsTo is not null))
            {
                var target = board.LeadsToFor(boss);
                Assert.True(
                    target is not null,
                    $"{board.Key}/{boss.Name}: LeadsTo '{boss.LeadsTo}' is not a boss on this board — the badge would vanish.");
                Assert.NotEqual(boss.Name, target!.Name);
                Assert.NotEqual(boss.ThemeKey, target.ThemeKey);
            }
        }
    }

    /// <summary>
    /// LinkshellCustomizeViewModel.TodMonsterGroups keeps its OWN copy of the eight farm NM names for
    /// the "Hide ToD Mobs" picker — a UI catalog rather than a timing rule, which is why it is a copy
    /// at all. A copy that DRIFTS hides a monster nothing tracks and leaves the real one visible.
    ///
    /// Compared as SETS on purpose: the picker is alphabetical for the UI and the chart is in path
    /// order, and neither should be forced into the other's order.
    ///
    /// Only the two C# copies are reachable from here. The Activity's TWO_HOUR_TOD_MONSTERS and its
    /// ToD picker group (discord-activity/src/app/home/activity-home.types.ts, twice) and the addon's
    /// constants.lua hold the same eight names, guarded by nothing but a comment.
    /// </summary>
    [Fact]
    public void TheTodPickersSkyGroup_IsTheHnmConfigFarmNmSet()
    {
        var picker = LinkshellCustomizeViewModel.TodMonsterGroups.Single(group => group.Label == "Sky NMs");

        Assert.Equal(
            HnmConfig.SkyFarmNmOrder.OrderBy(name => name, StringComparer.Ordinal),
            picker.Names.OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// Sea is drawn the way Sky is: one COLUMN per second-stage Jailer, holding everything that feeds
    /// it and then the Jailer itself, with Love and Absolute Virtue below them all. The card order IS
    /// that layout — three contiguous blocks of five, then the trailing run.
    ///
    /// The five rows are TIERS, and they line up sideways because every column spells them in the
    /// same order: the trash family farmed for the traded NM, that NM, the NM beside it that takes no
    /// trade at all, one more trash family, and the Jailer at the foot.
    /// </summary>
    [Fact]
    public void SeaBoard_IsSeventeenBosses_InPathOrder()
    {
        var sea = ChartBoardCatalog.Find(ChartBoardCatalog.Sea)!;

        Assert.Equal(
            new[]
            {
                "Ghrah", "Jailer of Fortitude", "Ix'aern (DRK)", "Xzomit", "Jailer of Justice",
                "Aern", "Ix'aern (MNK)", "Jailer of Temperance", "Phuabo", "Jailer of Hope",
                "Euvhi", "Jailer of Faith", "Ix'aern (DRG)", "Hpemde", "Jailer of Prudence",
                "Jailer of Love", "Absolute Virtue",
            },
            sea.Bosses.Select(boss => boss.Name).ToArray());

        Assert.Equal(
            new[] { "Justice Path", "Hope Path", "Prudence Path", "Final stage" },
            sea.Bosses.Select(boss => boss.Group!).Distinct().ToArray());

        Assert.Equal(
            new[] { "Justice Path", "Hope Path", "Prudence Path" },
            sea.PathColumns.ToArray());
    }

    /// <summary>
    /// Every Sea card but the two at the foot of the board feeds the card BELOW it in its own column,
    /// and its arrow badge says the same thing the column does. The two must agree: the badge is what
    /// carries the meaning once the columns collapse on a narrow screen.
    ///
    /// Note the two arrows that do NOT point at a Jailer — Ghrah feeds Jailer of Fortitude and Aern
    /// feeds Ix'aern (MNK). A trash family is farmed for the NM directly under it, not for the foot
    /// of the column, and a badge that skipped to the Jailer would send an officer to the wrong trade.
    /// </summary>
    [Fact]
    public void SeaFeeders_PointAtTheCardBelowThem_InTheirOwnColumn()
    {
        var sea = ChartBoardCatalog.Find(ChartBoardCatalog.Sea)!;

        var expected = new (string Nm, string Feeds)[]
        {
            ("Ghrah",                "Jailer of Fortitude"),
            ("Jailer of Fortitude",  "Jailer of Justice"),
            ("Ix'aern (DRK)",        "Jailer of Justice"),
            ("Xzomit",               "Jailer of Justice"),

            ("Aern",                 "Ix'aern (MNK)"),
            ("Ix'aern (MNK)",        "Jailer of Hope"),
            ("Jailer of Temperance", "Jailer of Hope"),
            ("Phuabo",               "Jailer of Hope"),

            ("Euvhi",                "Jailer of Faith"),
            ("Jailer of Faith",      "Jailer of Prudence"),
            ("Ix'aern (DRG)",        "Jailer of Prudence"),
            ("Hpemde",               "Jailer of Prudence"),
        };

        foreach (var (nm, feeds) in expected)
        {
            var card = sea.Find(nm)!;
            Assert.Equal(feeds, card.LeadsTo);
            Assert.Equal(sea.Find(feeds)!.Group, card.Group);
            Assert.True(
                sea.Bosses.ToList().IndexOf(card) < sea.Bosses.ToList().IndexOf(sea.Find(feeds)!),
                $"{nm} must come before {feeds} — a column is read downwards.");
        }

        // The Jailers at the foot carry no badge: three arrows pointing at the card below them says
        // less than the column already does. Same call Sky's gods make.
        Assert.All(
            new[] { "Jailer of Justice", "Jailer of Hope", "Jailer of Prudence", "Jailer of Love" },
            name => Assert.Null(sea.Find(name)!.LeadsTo));
    }

    /// <summary>
    /// The rule the board turns on: a card holds what falls OFF it, and that drop is the pop item for
    /// the card its arrow points at. Walk any column downwards and the chain closes — the trash family
    /// drops the organ that pops the NM below it, that NM drops the virtue or deed that pops the
    /// Jailer at the foot, and the Jailer drops the virtue Jailer of Love takes.
    ///
    /// Both halves are pinned together because they are one fact stated twice: the item NAME is stored
    /// on ChartPopItem.ItemName, and the LeadsTo decides where the card's arrow points and what colour
    /// it is. This is the test that catches an item left on the card it is SPENT on.
    /// </summary>
    [Fact]
    public void SeaCards_EachHoldOneDrop_AndItPopsTheCardTheyPointAt()
    {
        var sea = ChartBoardCatalog.Find(ChartBoardCatalog.Sea)!;

        var expected = new (string Card, string Drop, string? Pops)[]
        {
            ("Ghrah",                "Ghrah M Chip",              "Jailer of Fortitude"),
            ("Jailer of Fortitude",  "Second Virtue",             "Jailer of Justice"),
            ("Ix'aern (DRK)",        "Deed of Moderation",        "Jailer of Justice"),
            ("Xzomit",               "High-Quality Xzomit Organ", "Jailer of Justice"),
            ("Jailer of Justice",    "Fourth Virtue",             null),

            ("Aern",                 "High-Quality Aern Organ",   "Ix'aern (MNK)"),
            ("Ix'aern (MNK)",        "Deed of Placidity",         "Jailer of Hope"),
            ("Jailer of Temperance", "First Virtue",              "Jailer of Hope"),
            ("Phuabo",               "High-Quality Phuabo Organ", "Jailer of Hope"),
            ("Jailer of Hope",       "Fifth Virtue",              null),

            ("Euvhi",                "High-Quality Euvhi Organ",  "Jailer of Faith"),
            ("Jailer of Faith",      "Third Virtue",              "Jailer of Prudence"),
            ("Ix'aern (DRG)",        "Deed of Sensibility",       "Jailer of Prudence"),
            ("Hpemde",               "High-Quality Hpemde Organ", "Jailer of Prudence"),
            ("Jailer of Prudence",   "Sixth Virtue",              null),
        };

        foreach (var (card, drop, pops) in expected)
        {
            Assert.Equal(drop, Assert.Single(sea.Find(card)!.PopItems!).Name);
            Assert.Equal(pops, sea.Find(card)!.LeadsTo);
        }

        // And that is the WHOLE board: the only cards holding nothing are the two at the foot.
        Assert.Equal(
            expected.Select(row => row.Card).OrderBy(name => name, StringComparer.Ordinal),
            sea.Bosses.Where(boss => boss.PopItems is { Count: > 0 })
                .Select(boss => boss.Name)
                .OrderBy(name => name, StringComparer.Ordinal));

        // No Source anywhere on Sea any more, for the reason Sky names none: an item's drop source IS
        // the card it sits on, so naming it again would repeat the boss select the picker sits beside.
        Assert.All(
            sea.Bosses.SelectMany(boss => boss.PopItems ?? Array.Empty<ChartPopItemOption>()),
            option => Assert.Null(option.Source));
    }

    /// <summary>
    /// Sea MIXES: the two cards at the foot of the board hold nothing — Jailer of Love because it is
    /// the terminus of the chain, Absolute Virtue because it is a finale that documents rewards rather
    /// than tracking pops — so both fall back to a free-text box while the rest of the board gets a
    /// picker. This is why the choice is made per boss on both surfaces, and why
    /// ChartBoard.HasPopItemOptions asks Any rather than All.
    /// </summary>
    [Fact]
    public void SeaBoard_MixesTrackedCardsAndTheTwoAtTheFoot()
    {
        var sea = ChartBoardCatalog.Find(ChartBoardCatalog.Sea)!;

        Assert.Equal(
            new[] { "Jailer of Love", "Absolute Virtue" },
            sea.Bosses.Where(boss => boss.PopItems is null or { Count: 0 })
                .Select(boss => boss.Name)
                .ToArray());

        Assert.True(sea.HasPopItemOptions);

        // Love is popped WITH the three second-stage virtues, but holds none of them: they sit on the
        // Jailers that drop them, and its subtitle is what says so. Same shape as Kirin on Sky.
        Assert.Equal("Popped with the fourth, fifth, and sixth virtues", sea.Find("Jailer of Love")!.Subtitle);

        // How many the DOWNSTREAM pop takes rides in the label, next to the name it is a count of —
        // the target stock for a card that is farmed rather than killed once.
        Assert.Equal("Ghrah M Chip ×12", sea.PopItemsFor("Ghrah").Single().Label);
        Assert.Equal("High-Quality Aern Organ ×1–3", sea.PopItemsFor("Aern").Single().Label);

        // The rest is a bare name: a count of one says nothing a picker with one option does not.
        Assert.Equal("Second Virtue", sea.PopItemsFor("Jailer of Fortitude").Single().Label);
    }

    /// <summary>
    /// The six trash families are farmed rather than popped, so their cards headline STOCK — the
    /// question "how many organs do we have" is the only one they exist to answer. Absolute Virtue
    /// ends the board and is the only finale. Everything between them is an ordinary card.
    /// </summary>
    [Fact]
    public void SeaBoard_HasSixMiniNms_AndOneFinal()
    {
        var sea = ChartBoardCatalog.Find(ChartBoardCatalog.Sea)!;

        Assert.Equal(
            new[] { "Ghrah", "Xzomit", "Aern", "Phuabo", "Euvhi", "Hpemde" },
            sea.Bosses.Where(boss => boss.Kind == ChartBossKinds.MiniNm).Select(boss => boss.Name).ToArray());

        // Two cards per column, so no column headlines stock more often than its neighbours.
        Assert.All(
            sea.PathColumns,
            label => Assert.Equal(
                2,
                sea.Bosses.Count(boss => boss.Group == label && boss.Kind == ChartBossKinds.MiniNm)));

        // Jailer of Fortitude is NOT one any more: the twelve chips it takes are a stock problem, and
        // that problem moved to the Ghrah card when the chips did. It drops one virtue like every
        // other first-stage NM, so it counts items like them too.
        Assert.Equal(ChartBossKinds.Standard, sea.Find("Jailer of Fortitude")!.Kind);

        var final = sea.Bosses.Single(boss => boss.Kind == ChartBossKinds.Final);
        Assert.Equal("Absolute Virtue", final.Name);
        Assert.NotEmpty(final.Rewards!);
        Assert.False(string.IsNullOrWhiteSpace(final.ReferenceNote));
    }

    /// <summary>
    /// The six trash families have no portrait in the shipped Sea art and share the zone shot the
    /// folder already carries — the same call Limbus makes with two zone images over fourteen cards.
    /// Pinned so dropping in per-family art is a deliberate edit rather than a silent divergence, and
    /// so nothing else on the board quietly starts sharing it.
    /// </summary>
    [Fact]
    public void SeaTrashFamilies_ShareTheZoneShot_AndNothingElseDoes()
    {
        var sea = ChartBoardCatalog.Find(ChartBoardCatalog.Sea)!;

        Assert.Equal(
            new[] { "Ghrah", "Xzomit", "Aern", "Phuabo", "Euvhi", "Hpemde" },
            sea.Bosses.Where(boss => boss.EmblemFile == "Sea.jpg").Select(boss => boss.Name).ToArray());
    }

    /// <summary>
    /// A card's items are what falls OFF it, so a seal lives on the god that drops it rather than on
    /// Kirin, which is what it pops. That is the rule the whole board turns on — it is what lets the
    /// farm NMs hold the gems. The names are STORED on ChartPopItem.ItemName when an officer picks
    /// one, so a rename here leaves existing rows spelled the old way.
    /// </summary>
    [Fact]
    public void SkyBoard_EachGodDropsItsOwnSeal_AndKirinDropsNothing()
    {
        var sky = ChartBoardCatalog.Find(ChartBoardCatalog.Sky)!;

        static string[] Names(ChartBoard board, string boss) =>
            board.PopItemsFor(boss).Select(option => option.Name).ToArray();

        Assert.Equal(new[] { "Seal of Suzaku", "Curtana" }, Names(sky, "Suzaku"));
        Assert.Equal(new[] { "Seal of Genbu" }, Names(sky, "Genbu"));
        Assert.Equal(new[] { "Seal of Seiryu" }, Names(sky, "Seiryu"));
        Assert.Equal(new[] { "Seal of Byakko" }, Names(sky, "Byakko"));

        // Kirin is the terminus: popped WITH the four seals, but nothing is tracked as dropping FROM
        // it, so its card documents the fight in a subtitle and holds no items.
        Assert.Empty(sky.PopItemsFor("Kirin"));
        Assert.Equal("Popped with one seal from each of the four gods", sky.Find("Kirin")!.Subtitle);

        // No Source anywhere on Sky: an item's drop source IS the card it sits on, so naming it
        // again would repeat the boss select the picker sits beside.
        Assert.All(
            sky.Bosses.SelectMany(boss => boss.PopItems ?? Array.Empty<ChartPopItemOption>()),
            option => Assert.Null(option.Source));

        // Sky MIXES now, exactly as Sea does — which is why HasPopItemOptions asks Any, not All.
        Assert.True(sky.HasPopItemOptions);
    }

    /// <summary>
    /// The picker shows this, on both surfaces, off one implementation.
    ///
    /// No board sets Source any more — every card lists what falls off it, so the source IS the card —
    /// but the form is still built and still tested, because it is the right answer for a board whose
    /// items come from somewhere it draws no card for. Sea was that board until the six trash families
    /// joined it, and the next one added may be too.
    /// </summary>
    [Fact]
    public void PopItemOption_LabelsTheCountAndSourceBesideTheName()
    {
        Assert.Equal("Winterstone", new ChartPopItemOption("Winterstone").Label);
        Assert.Equal("Ghrah M Chip ×12", new ChartPopItemOption("Ghrah M Chip", Needed: "12").Label);
        Assert.Equal("Second Virtue — Jailer of Fortitude",
            new ChartPopItemOption("Second Virtue", "Jailer of Fortitude").Label);
        Assert.Equal("Ghrah M Chip ×12 — Ghrah",
            new ChartPopItemOption("Ghrah M Chip", "Ghrah", "12").Label);

        // Both live boards name no Source, so every label the catalog actually renders is a bare name
        // or a name and a count.
        var sky = ChartBoardCatalog.Find(ChartBoardCatalog.Sky)!;
        Assert.Equal("Gem of the North", sky.PopItemsFor("Zipacna").Single().Label);

        var sea = ChartBoardCatalog.Find(ChartBoardCatalog.Sea)!;
        Assert.Equal("Fourth Virtue", sea.PopItemsFor("Jailer of Justice").Single().Label);
    }

    /// <summary>
    /// HasPopItemOptions asks whether the board needs the picker AT ALL, not whether every boss has
    /// one — Sea has bosses of both kinds. Deliberately unlike ABoardGroupsEveryBossOrNone: a group
    /// heading over only half a board reads as a mistake, but a lottery NM that takes no trade item
    /// is a fact about the game.
    /// </summary>
    [Fact]
    public void HasPopItemOptions_IsTrueForAnyBossWithAList()
    {
        foreach (var board in ChartBoardCatalog.Boards)
        {
            Assert.Equal(
                board.Bosses.Any(boss => boss.PopItems is { Count: > 0 }),
                board.HasPopItemOptions);
        }

        Assert.True(ChartBoardCatalog.Find(ChartBoardCatalog.Sky)!.HasPopItemOptions);
        // Dynamis and Limbus track whatever a linkshell decides to track, so both forms stay free
        // text and neither surface ships a picker for them.
        Assert.False(ChartBoardCatalog.Find(ChartBoardCatalog.Dynamis)!.HasPopItemOptions);
        Assert.False(ChartBoardCatalog.Find(ChartBoardCatalog.Limbus)!.HasPopItemOptions);
    }

    [Theory]
    [InlineData("Olla Grande", "  winterstone ", "Winterstone")]
    [InlineData("Zipacna", "GEM OF THE NORTH", "Gem of the North")]
    [InlineData("Genbu", "seal of genbu", "Seal of Genbu")]
    // Not one of this card's items, so it passes through as typed rather than being rejected: boards
    // with no list still take free text, and rows saved before Sky was restructured have to stay
    // editable under the name they were saved with.
    [InlineData("Zipacna", "Shard of Apollyon", "Shard of Apollyon")]
    public void NormalizePopItemName_CanonicalisesWhatTheCatalogKnows(
        string boss, string typed, string expected)
    {
        Assert.Equal(expected, ChartBoardCatalog.NormalizePopItemName(ChartBoardCatalog.Sky, boss, typed));
    }

    [Fact]
    public void NormalizePopItemName_LeavesUnlistedBossesAlone()
    {
        // Jailer of Love declares no items — it is the terminus of the chain — so nothing filed under
        // it is canonicalised, only trimmed. Same for Dynamis, which lists nothing at all.
        Assert.Equal(
            "Aged Humus",
            ChartBoardCatalog.NormalizePopItemName(ChartBoardCatalog.Sea, "Jailer of Love", "  Aged Humus "));
        Assert.Equal(
            "Montiont Silverpiece",
            ChartBoardCatalog.NormalizePopItemName(ChartBoardCatalog.Dynamis, "Xarcabard", " Montiont Silverpiece "));
        Assert.Null(ChartBoardCatalog.NormalizePopItemName(ChartBoardCatalog.Sky, "Genbu", "   "));

        // Sea DOES canonicalise now, on the card that drops the item rather than the one it pops.
        Assert.Equal(
            "High-Quality Xzomit Organ",
            ChartBoardCatalog.NormalizePopItemName(ChartBoardCatalog.Sea, "Xzomit", " high-quality xzomit organ "));
    }

    // Every board card renders an <img>. A wrong file name is a broken image on a page nobody
    // notices until a linkshell opens it — and the shipped Sea art has inconsistent casing plus one
    // misspelling (JailerOfForitude.jpg), which is exactly why the catalog names files explicitly
    // instead of computing them.
    [Fact]
    public void EveryBossEmblem_ExistsOnDisk()
    {
        var publicRoot = FindRepoPath(Path.Combine("discord-activity", "public"));
        var webRoot = FindRepoPath("wwwroot");

        foreach (var board in ChartBoardCatalog.Boards)
        {
            foreach (var boss in board.Bosses)
            {
                var relative = board.EmblemPathFor(boss).Replace('/', Path.DirectorySeparatorChar);

                Assert.True(
                    File.Exists(Path.Combine(publicRoot, relative)),
                    $"Activity art missing for {board.Key}/{boss.Name}: {relative}");
                Assert.True(
                    File.Exists(Path.Combine(webRoot, relative)),
                    $"Website art missing for {board.Key}/{boss.Name}: {relative}");
            }
        }
    }

    [Theory]
    [InlineData("sea", "Sea")]
    [InlineData("SKY", "Sky")]
    [InlineData("  Sky  ", "Sky")]
    public void NormalizeBoard_IsCaseInsensitiveAndTrims(string input, string expected)
    {
        Assert.Equal(expected, ChartBoardCatalog.NormalizeBoard(input));
    }

    [Theory]
    // Endgame content with no board — the role "Dynamis" and then "Limbus" played here before they
    // were built. Every board the Charts nav names is live now, so this needs a name from outside it.
    [InlineData("Salvage")]
    [InlineData("Einherjar")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeBoard_RejectsUnknownBoards(string? input)
    {
        Assert.Null(ChartBoardCatalog.NormalizeBoard(input));
    }

    [Theory]
    [InlineData("Sea", "jailer of love", "Jailer of Love")]
    [InlineData("Sky", "  byakko ", "Byakko")]
    public void NormalizeBoss_CanonicalisesWithinItsBoard(string board, string boss, string expected)
    {
        Assert.Equal(expected, ChartBoardCatalog.NormalizeBoss(board, boss));
    }

    // The cross-board case: a Sky god is not a Sea boss. Without this, a request could file a
    // Byakko row onto the Sea board, where it would group under no card and be invisible.
    [Fact]
    public void NormalizeBoss_RejectsABossFromAnotherBoard()
    {
        Assert.Null(ChartBoardCatalog.NormalizeBoss(ChartBoardCatalog.Sea, "Byakko"));
        Assert.Null(ChartBoardCatalog.NormalizeBoss(ChartBoardCatalog.Sky, "Absolute Virtue"));

        // Dynamis is a real board now, so this line stopped meaning "unknown board" and started
        // meaning "known board, foreign boss" — which is the case that actually matters. Pinned
        // both directions so neither board can absorb the other's cast.
        Assert.Null(ChartBoardCatalog.NormalizeBoss(ChartBoardCatalog.Dynamis, "Byakko"));
        Assert.Null(ChartBoardCatalog.NormalizeBoss(ChartBoardCatalog.Sky, "Jeuno"));
    }

    [Fact]
    public void ThemeKeysAreUniqueWithinABoard()
    {
        foreach (var board in ChartBoardCatalog.Boards)
        {
            var keys = board.Bosses.Select(boss => boss.ThemeKey).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
            // They name CSS classes, so anything but a plain lower-case slug would silently not match.
            Assert.All(keys, key => Assert.Equal(key.ToLowerInvariant(), key));
        }
    }

    // Theme keys become classes in ONE global stylesheet per surface (.lsm-page .is-jeuno,
    // .panel-tab .is-jeuno), so they have to be unique across the WHOLE catalog, not just within a
    // board: two boards sharing a key can only ever get one colour, and the loser is silent.
    [Fact]
    public void ThemeKeysAreUniqueAcrossTheWholeCatalog()
    {
        var keys = ChartBoardCatalog.Boards
            .SelectMany(board => board.Bosses)
            .Select(boss => boss.ThemeKey)
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DynamisBoard_IsTenZones_SplitIntoTwoGroups()
    {
        var dynamis = ChartBoardCatalog.Find(ChartBoardCatalog.Dynamis)!;

        // Bare zone names, not "Dynamis - San d'Oria": the board already says Dynamis, and these
        // head a one-word column in the ledger. Stored verbatim on ChartPopItem.Boss, so renaming
        // one orphans rows.
        Assert.Equal(
            new[]
            {
                "San d'Oria", "Bastok", "Windurst", "Jeuno", "Beaucedine", "Xarcabard",
                "Valkurm", "Buburimu", "Qufim", "Tavnazia",
            },
            dynamis.Bosses.Select(boss => boss.Name).ToArray());

        // Every zone is an ordinary grid card: Dynamis has no farmed mini-NM and no single final
        // encounter, so neither special Kind applies.
        Assert.All(dynamis.Bosses, boss => Assert.Equal(ChartBossKinds.Standard, boss.Kind));

        Assert.Equal(
            new[] { "Original Dynamis", "Dreamworld Dynamis" },
            dynamis.Bosses.Select(boss => boss.Group!).Distinct().ToArray());
    }

    /// <summary>
    /// Dynamis lays its cards out in rows it chooses: the three nations and Jeuno, then the two
    /// Xarcabard-era zones, then the three original Dreamworld areas, then Tavnazia alone.
    ///
    /// EndsRow is only set INSIDE a run — a group heading is full-width and breaks the row before it
    /// on its own, so marking the last card of a group would emit a break that does nothing. This
    /// walks the board the way both surfaces do (break on a group change OR on EndsRow) so the rows
    /// asserted here are the rows that render.
    /// </summary>
    [Fact]
    public void DynamisBoard_BreaksIntoTheRowsItChose()
    {
        var dynamis = ChartBoardCatalog.Find(ChartBoardCatalog.Dynamis)!;
        Assert.True(dynamis.CentersRows);

        Assert.Equal(
            new[]
            {
                new[] { "San d'Oria", "Bastok", "Windurst", "Jeuno" },
                new[] { "Beaucedine", "Xarcabard" },
                new[] { "Valkurm", "Buburimu", "Qufim" },
                new[] { "Tavnazia" },
            },
            RowsOf(dynamis));
    }

    /// <summary>
    /// Limbus runs each zone as a flow: the areas that open a fight, then the fight, then the chip
    /// area on a row of its own — it is optional, and not a step toward the fight.
    /// </summary>
    [Fact]
    public void LimbusBoard_BreaksIntoTheRowsItChose()
    {
        var limbus = ChartBoardCatalog.Find(ChartBoardCatalog.Limbus)!;
        Assert.True(limbus.CentersRows);

        Assert.Equal(
            new[]
            {
                new[] { "NW", "SW", "NE", "SE" },
                new[] { "Central" },
                new[] { "CS" },
                new[] { "Western Tower", "Eastern Tower", "Northern Tower" },
                new[] { "Central 1F", "Central 2F", "Central 3F" },
                new[] { "Central 4F" },
                new[] { "Central B1" },
            },
            RowsOf(limbus));

        // The chip areas end their zone's run, so each already sits on a row of its own without a
        // break of its own — CS because the Temenos heading follows, B1 because the board ends.
        Assert.All(
            new[] { "CS", "Central B1" },
            name => Assert.False(limbus.Find(name)!.EndsRow));
    }

    /// <summary>
    /// HENM is a tier ladder: three NMs per tier, then the Council. Its GROUPS are its rows, so no
    /// card sets EndsRow — a heading is full width and breaks the row on its own. That is exactly why
    /// CentersRows is declared rather than inferred from EndsRow: this board would have had to set a
    /// redundant break just to opt into the centred layout.
    /// </summary>
    [Fact]
    public void HenmBoard_IsATierLadder_WithNoRowBreaksOfItsOwn()
    {
        var henm = ChartBoardCatalog.Find(ChartBoardCatalog.Henm)!;
        Assert.True(henm.CentersRows);
        Assert.All(henm.Bosses, boss => Assert.False(boss.EndsRow));

        Assert.Equal(
            new[]
            {
                new[] { "Ruinous Rocs", "Despotic Decapod", "Sacred Scorpions" },
                new[] { "Mammet-9999", "Tonberry Sovereign", "Ultimega" },
                new[] { "Primordial Behemoth", "Minogame", "IO" },
                new[] { "Council of the Zilart" },
            },
            RowsOf(henm));

        // The entry cost rides in the tier's own caption, where the reference art puts it — a banner
        // over the row, not a claim that each of the three NMs takes fifty shards of its own.
        Assert.Equal(
            new[] { "Tier 1 · 50 Cerulean Shards", "Tier 2", "Tier 3", "Tier 4" },
            henm.Bosses.Select(boss => boss.Group!).Distinct().ToArray());

        // Only Tier 1 says where it is camped; the ladder above it is the same NMs wherever found.
        Assert.Equal("Rolanberry Fields", henm.Find("Ruinous Rocs")!.Subtitle);
        Assert.Equal("Jugner Forest", henm.Find("Despotic Decapod")!.Subtitle);
        Assert.Equal("Sauromugue Champaign", henm.Find("Sacred Scorpions")!.Subtitle);
        Assert.All(
            henm.Bosses.Where(boss => boss.Group == "Tier 2" || boss.Group == "Tier 3"),
            boss => Assert.Null(boss.Subtitle));
    }

    // A break on the last card of the board would render an empty row. (A break on the last card of
    // a RUN is harmless — the next heading breaks the row anyway — but it is still redundant.)
    [Fact]
    public void NoBoardEndsARowOnItsLastCard()
    {
        foreach (var board in ChartBoardCatalog.Boards)
        {
            Assert.False(
                board.Bosses.Count > 0 && board.Bosses[^1].EndsRow,
                $"{board.Key}: the last card on the board ends a row, which renders an empty one.");
        }
    }

    /// <summary>
    /// Walks a board into the rows it actually renders as, the way BOTH surfaces do: a new row on a
    /// group change (the heading is full width) or on EndsRow.
    /// </summary>
    private static string[][] RowsOf(ChartBoard board)
    {
        var rows = new List<List<string>> { new() };
        string? previousGroup = null;

        foreach (var boss in board.Bosses)
        {
            if (previousGroup is not null && boss.Group != previousGroup && rows[^1].Count > 0)
            {
                rows.Add(new List<string>());
            }
            previousGroup = boss.Group;

            rows[^1].Add(boss.Name);
            if (boss.EndsRow)
            {
                rows.Add(new List<string>());
            }
        }

        rows.RemoveAll(row => row.Count == 0);
        return rows.Select(row => row.ToArray()).ToArray();
    }

    [Fact]
    public void LimbusBoard_IsFourteenAreas_SplitIntoTwoZones()
    {
        var limbus = ChartBoardCatalog.Find(ChartBoardCatalog.Limbus)!;

        // Bare area names — the group heading says Apollyon or Temenos, and fourteen ledger columns
        // leave no room for the zone word. Stored verbatim on ChartPopItem.Boss.
        Assert.Equal(
            new[]
            {
                "NW", "SW", "NE", "SE", "Central", "CS",
                "Western Tower", "Eastern Tower", "Northern Tower",
                "Central 1F", "Central 2F", "Central 3F", "Central 4F", "Central B1",
            },
            limbus.Bosses.Select(boss => boss.Name).ToArray());

        Assert.Equal(
            new[] { "Apollyon", "Temenos" },
            limbus.Bosses.Select(boss => boss.Group!).Distinct().ToArray());
    }

    // Each zone ends the same way Sea does: a farmed chip area and a finale. This is the board that
    // proves a second finale renders at all — ChartBoardViewModel.FinalBoss used to be a
    // FirstOrDefault, so Proto-Ultima's card appeared on NO surface of the website.
    [Fact]
    public void LimbusBoard_HasAChipAreaAndAFinale_InEachZone()
    {
        var limbus = ChartBoardCatalog.Find(ChartBoardCatalog.Limbus)!;

        Assert.Equal(
            new[] { "CS", "Central B1" },
            limbus.Bosses.Where(boss => boss.Kind == ChartBossKinds.MiniNm).Select(boss => boss.Name).ToArray());

        var finales = limbus.Bosses.Where(boss => boss.Kind == ChartBossKinds.Final).ToList();
        Assert.Equal(new[] { "Central", "Central 4F" }, finales.Select(boss => boss.Name).ToArray());
        Assert.Equal(new[] { "Apollyon", "Temenos" }, finales.Select(boss => boss.Group).ToArray());
    }

    /// <summary>
    /// Group labels appear in CONTIGUOUS runs. Both surfaces open a heading when a card's group
    /// differs from the previous card's — ChartBoardViewModel.BossGroups and the Activity's
    /// bossGroups computed both collapse adjacent equals — so an "A, B, A" order prints the "A"
    /// heading TWICE and splits one group across the board. Runs rather than a group-by on purpose:
    /// the catalog's order IS the display order, and grouping by key would silently reorder cards.
    /// </summary>
    [Fact]
    public void GroupRunsAreContiguousWithinABoard()
    {
        foreach (var board in ChartBoardCatalog.Boards)
        {
            var started = new HashSet<string>(StringComparer.Ordinal);
            string? previous = null;

            foreach (var boss in board.Bosses)
            {
                if (boss.Group == previous)
                {
                    continue;
                }
                if (boss.Group is not null)
                {
                    Assert.True(
                        started.Add(boss.Group),
                        $"{board.Key}: group '{boss.Group}' restarts after another group — its heading would render twice.");
                }
                previous = boss.Group;
            }
        }
    }

    // A board groups every card or none of them. Half-grouped renders a heading and then loose cards
    // under no heading at all, which reads as a heading somebody forgot rather than as a design.
    [Fact]
    public void ABoardGroupsEveryBossOrNone()
    {
        foreach (var board in ChartBoardCatalog.Boards)
        {
            var grouped = board.Bosses.Count(boss => boss.Group is not null);
            Assert.True(
                grouped == 0 || grouped == board.Bosses.Count,
                $"{board.Key}: {grouped} of {board.Bosses.Count} bosses carry a group label.");
        }
    }

    // No board declares an ungrouped run any more — Sky and Sea were the last two, and both are
    // chains now. ChartBossGroupingTests still pins that the runs transform collapses an ungrouped
    // board to ONE unlabelled run, which is what stops a view emitting a heading for it.

    // The one silent way the ChartBoss signature can go wrong: an optional string? inserted before
    // Subtitle captures the line meant for it — string? into string? compiles clean and prints a
    // subtitle as a heading, or a heading as a subtitle, with nothing to catch it but eyes on a page.
    // Sea is the board that would show it, carrying both on the same cards.
    [Fact]
    public void SeaSubtitlesAndGroupsDoNotSwap()
    {
        var sea = ChartBoardCatalog.Find(ChartBoardCatalog.Sea)!;

        var fortitude = sea.Find("Jailer of Fortitude")!;
        Assert.Equal("Popped with 12 Ghrah M Chips", fortitude.Subtitle);
        Assert.Equal("Justice Path", fortitude.Group);
        Assert.Equal("Jailer of Justice", fortitude.LeadsTo);

        // A trash family card passes Kind positionally and everything after it by name, which is the
        // arrangement the swap would show up in first.
        var ghrah = sea.Find("Ghrah")!;
        Assert.Equal("Farmed trash mob", ghrah.Subtitle);
        Assert.Equal("Justice Path", ghrah.Group);
        Assert.Equal("Jailer of Fortitude", ghrah.LeadsTo);

        var virtue = sea.Find("Absolute Virtue")!;
        Assert.Equal("Final encounter.", virtue.Subtitle);
        Assert.Equal("Final stage", virtue.Group);
    }

    // Board.cshtml's sub-nav links with asp-action="@board.Key" and ChartsController.BackTo redirects
    // to the key, so a board with no action of that name renders an anchor with NO href (a silently
    // dead button) and throws on every PRG redirect. Only one of those two failures is loud.
    // Reflection rather than a WebApplicationFactory: this project is service-level, and only the
    // method names matter.
    [Fact]
    public void EveryBoard_HasAControllerActionNamedAfterItsKey()
    {
        var actions = typeof(ChartsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var board in ChartBoardCatalog.Boards)
        {
            Assert.True(actions.Contains(board.Key), $"ChartsController has no {board.Key}() action.");
        }
    }

    /// <summary>Walks up from the test binary to the repo root, which holds the app's csproj.</summary>
    private static string FindRepoPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate '{relative}' above {AppContext.BaseDirectory}.");
    }
}
