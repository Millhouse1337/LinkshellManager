using System.Reflection;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Covers the vocabulary layer: what a category IS is derived from its number, what can happen to the
// treasury is a fixed catalog, and none of the words on screen are bookkeeping jargon.
public class TreasuryVocabularyTests
{
    // Class derived from the number, so a category whose number and behaviour disagree is not a state
    // the schema can reach.
    [Theory]
    [InlineData(1000, LedgerAccountClasses.Holds)]
    [InlineData(1999, LedgerAccountClasses.Holds)]
    [InlineData(2000, LedgerAccountClasses.Owes)]
    [InlineData(2999, LedgerAccountClasses.Owes)]
    [InlineData(3000, LedgerAccountClasses.Worth)]
    [InlineData(3999, LedgerAccountClasses.Worth)]
    [InlineData(4000, LedgerAccountClasses.MoneyIn)]
    [InlineData(4999, LedgerAccountClasses.MoneyIn)]
    [InlineData(5000, LedgerAccountClasses.MoneyOut)]
    [InlineData(5999, LedgerAccountClasses.MoneyOut)]
    public void FromNumber_MapsEveryRangeBoundary(int accountNumber, string expected)
    {
        Assert.Equal(expected, LedgerAccountClasses.FromNumber(accountNumber));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(6000)]
    [InlineData(0)]
    [InlineData(-1)]
    public void FromNumber_RejectsNumbersOutsideTheRange(int accountNumber)
    {
        Assert.Null(LedgerAccountClasses.FromNumber(accountNumber));
        // The DB CHECK constraint enforces the same range, so this can never be persisted either.
        Assert.True(
            accountNumber < LedgerAccountClasses.MinAccountNumber
            || accountNumber > LedgerAccountClasses.MaxAccountNumber);
    }

    // What we hold and what we spend grow when gil is added to them; what we owe, what we're worth and
    // gil we took in grow the other way. This is what gross totals key on, and getting it wrong makes
    // a reversal inflate both totals instead of reducing one.
    [Theory]
    [InlineData(LedgerAccountClasses.Holds, 1)]
    [InlineData(LedgerAccountClasses.MoneyOut, 1)]
    [InlineData(LedgerAccountClasses.Owes, -1)]
    [InlineData(LedgerAccountClasses.Worth, -1)]
    [InlineData(LedgerAccountClasses.MoneyIn, -1)]
    public void NormalSign_IsTheSideACategoryGrowsOn(string accountClass, int expected)
    {
        Assert.Equal(expected, LedgerAccountClasses.NormalSign(accountClass));
    }

    [Fact]
    public void SeededCategories_AreCoherent()
    {
        var accounts = LedgerAccountDefaults.BuildDefaultAccounts(7).ToList();

        Assert.All(accounts, account => Assert.Equal(7, account.LinkshellId));
        Assert.All(accounts, account => Assert.True(account.IsSystem));
        Assert.All(accounts, account => Assert.NotNull(account.AccountClass));
        Assert.All(accounts, account => Assert.False(string.IsNullOrWhiteSpace(account.Name)));
        // Names are what the UI shows, so a duplicate would be genuinely ambiguous on screen.
        Assert.Equal(accounts.Count, accounts.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(accounts.Count, accounts.Select(a => a.AccountNumber).Distinct().Count());
        Assert.Equal(accounts.Count, accounts.Select(a => a.SortOrder).Distinct().Count());

        // Exactly one gil-on-hand category: its balance IS the treasury balance, so two would make
        // "how much gil do we have" ambiguous again. A partial unique index enforces the same thing.
        var cash = Assert.Single(accounts, account => account.IsCash);
        Assert.Equal(TreasuryAccounts.GilOnHand, cash.AccountNumber);

        // "What we're worth" is worked out, never recorded against — that is what removes the whole
        // "did the close run, and did it run twice?" class of failure.
        var notPostable = Assert.Single(accounts, account => !account.IsPostable);
        Assert.Equal(TreasuryAccounts.NetWorth, notPostable.AccountNumber);
    }

    // Every number in TreasuryAccounts has to be seeded, or a call site can name a category that does
    // not exist and the writer throws at runtime.
    [Fact]
    public void EveryNamedAccountNumberIsSeeded()
    {
        var seeded = LedgerAccountDefaults.BuildDefaultAccounts(1).Select(a => a.AccountNumber).ToHashSet();
        var named = typeof(TreasuryAccounts)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(int))
            .Select(field => (Name: field.Name, Number: (int)field.GetRawConstantValue()!));

        foreach (var (name, number) in named)
        {
            Assert.True(seeded.Contains(number), $"TreasuryAccounts.{name} ({number}) is not seeded");
        }
    }

    [Fact]
    public void EveryTransactionKindNamesRealPostableCategories()
    {
        var byNumber = LedgerAccountDefaults.BuildDefaultAccounts(1).ToDictionary(a => a.AccountNumber);

        foreach (var kind in TreasuryTransactionKinds.All)
        {
            Assert.True(byNumber.ContainsKey(kind.AddTo), $"{kind.Key} adds to a category that is not seeded");
            Assert.True(byNumber.ContainsKey(kind.TakeFrom), $"{kind.Key} takes from a category that is not seeded");
            // Nothing may post to "what we're worth" — it is derived.
            Assert.True(byNumber[kind.AddTo].IsPostable, $"{kind.Key} posts to a derived category");
            Assert.True(byNumber[kind.TakeFrom].IsPostable, $"{kind.Key} posts to a derived category");
            Assert.NotEqual(kind.AddTo, kind.TakeFrom);
            Assert.False(string.IsNullOrWhiteSpace(kind.Label));
            Assert.False(string.IsNullOrWhiteSpace(kind.Help));
            // Every kind needs a picker name, including the ones nobody can pick: Fix still renders
            // a retired kind, and a blank option is not something anyone can choose back out of.
            Assert.False(
                string.IsNullOrWhiteSpace(kind.ReasonLabel),
                $"{kind.Key} has no reason label");
            Assert.True(
                TreasuryTransactionActions.IsKnown(kind.Action),
                $"{kind.Key} sits under an action that does not exist: '{kind.Action}'");
            // The preview sentence has to have somewhere to put the amount.
            Assert.Contains("{0}", kind.PreviewTemplate);
            // A split has to land on one of the kind's own two categories. Anything else would
            // build an entry whose halves are not the pair the kind promises.
            Assert.True(
                kind.SplitAccount is null
                    || kind.SplitAccount == kind.AddTo
                    || kind.SplitAccount == kind.TakeFrom,
                $"{kind.Key} splits a category that is not one of its own two");
        }
    }

    // A split asks for several members, so the surfaces have to know to show a member field at all.
    [Fact]
    public void EverySplittableKindAsksForMembers()
    {
        Assert.All(
            TreasuryTransactionKinds.All.Where(kind => kind.IsSplittable),
            kind => Assert.True(kind.ShowsMember, $"{kind.Key} splits but does not show a member"));
    }

    // The balance sheet calls what-we-owe "Owed to members", and expands it into a list of people.
    // That is only honest while every kind touching the category names a member. The day someone
    // adds "We owe a shop", this fails instead of the label quietly becoming a lie.
    [Fact]
    public void EveryKindThatTouchesWhatWeOweNamesAMember()
    {
        var touching = TreasuryTransactionKinds.All
            .Where(kind => kind.AddTo == TreasuryAccounts.WeOwe || kind.TakeFrom == TreasuryAccounts.WeOwe)
            .ToList();

        Assert.NotEmpty(touching);
        Assert.All(touching, kind =>
            Assert.True(kind.ShowsMember, $"{kind.Key} moves what we owe but does not name a member"));
    }

    [Fact]
    public void TransactionKinds_HaveUniqueKeysAndLabels()
    {
        var all = TreasuryTransactionKinds.All;

        Assert.Equal(all.Count, all.Select(k => k.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(all.Count, all.Select(k => k.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, kind => Assert.NotNull(TreasuryTransactionKinds.Find(kind.Key)));
        Assert.Null(TreasuryTransactionKinds.Find("NotAThing"));
        Assert.Null(TreasuryTransactionKinds.Find(null));
    }

    // The picker offers what an officer may choose, and nothing else. Two groups are held back:
    // the ones the APP records for you (the gil-count adjustments, and the settle-up the
    // tick-and-pay panel writes) and the ones that have been superseded.
    [Fact]
    public void Pickable_HidesTheKindsTheAppChoosesItself()
    {
        var pickable = TreasuryTransactionKinds.Pickable().Select(kind => kind.Key).ToList();

        // Recorded by "Gil on Hand" after someone counts the mule.
        Assert.DoesNotContain(TreasuryTransactionKinds.FoundExtraGil, pickable);
        Assert.DoesNotContain(TreasuryTransactionKinds.MissingGil, pickable);
        // Recorded by ticking names off the balance sheet — the only way to settle up, on either
        // half of it. Both directions are a tick, never a typed entry.
        Assert.DoesNotContain(TreasuryTransactionKinds.WePaidWhatWeOwed, pickable);
        Assert.DoesNotContain(TreasuryTransactionKinds.TheyPaidWhatTheyOwed, pickable);
        // Recorded by marking an item sold out of the stockpile, and by closing a gil auction. The
        // app knows exactly what these were, so it labels them precisely; nobody types them.
        Assert.DoesNotContain(TreasuryTransactionKinds.SoldAnItem, pickable);
        Assert.DoesNotContain(TreasuryTransactionKinds.PaidGilToMember, pickable);
        // Superseded by What We Owe, which records the same payout as obligations instead.
        Assert.DoesNotContain(TreasuryTransactionKinds.SplitGilAmongMembers, pickable);
        // Folded into What We Owe, which takes one member or several. A split of one writes the
        // identical ledger lines, so the picker loses a button and nobody loses a capability.
        Assert.DoesNotContain(TreasuryTransactionKinds.WeOweAMember, pickable);
        // The income and expense buckets: superseded by typing a sentence.
        Assert.DoesNotContain(TreasuryTransactionKinds.GotADonation, pickable);
        Assert.DoesNotContain(TreasuryTransactionKinds.GotPaidForWork, pickable);
        Assert.DoesNotContain(TreasuryTransactionKinds.BoughtSomething, pickable);

        Assert.Contains(TreasuryTransactionKinds.OtherMoneyIn, pickable);
        Assert.Contains(TreasuryTransactionKinds.OtherMoneyOut, pickable);
        Assert.Contains(TreasuryTransactionKinds.WeOweSeveralMembers, pickable);
        Assert.Contains(TreasuryTransactionKinds.SomeoneOwesUsForWork, pickable);
        // "Starting Gil" and "Gil on Hand" were removed outright: a Gil In or Gil Out with a sentence
        // in the reason box does what both of them did.
        Assert.DoesNotContain(TreasuryTransactionKinds.StartingGil, pickable);
    }

    // THE shape of the picker after the categories went. Gil In and Gil Out each offer exactly one
    // thing, which is what lets both front-ends drop the reason select and show a box to type in
    // instead. A second reason appearing under either of them silently brings the dropdown back.
    [Fact]
    public void GilInAndGilOutOfferExactlyOneThingEach()
    {
        var gilIn = TreasuryTransactionKinds.ReasonsFor(TreasuryTransactionActions.GilIn).ToList();
        var gilOut = TreasuryTransactionKinds.ReasonsFor(TreasuryTransactionActions.GilOut).ToList();

        Assert.Equal(TreasuryTransactionKinds.OtherMoneyIn, Assert.Single(gilIn).Key);
        Assert.Equal(TreasuryTransactionKinds.OtherMoneyOut, Assert.Single(gilOut).Key);

        // And they are the plain movement of gil on hand, with no category left to choose.
        Assert.Equal(TreasuryAccounts.GilOnHand, gilIn[0].AddTo);
        Assert.Equal(TreasuryAccounts.OtherMoneyIn, gilIn[0].TakeFrom);
        Assert.Equal(TreasuryAccounts.OtherMoneyOut, gilOut[0].AddTo);
        Assert.Equal(TreasuryAccounts.GilOnHand, gilOut[0].TakeFrom);
        Assert.False(gilIn[0].ShowsMember);
        Assert.False(gilOut[0].ShowsMember);
    }

    // The stockpile sale is the one arrival the app can categorise on its own, and it says so in
    // the transactions list. It is not offered in the picker: an officer who sold a stockpile item
    // marks the ITEM sold, and the gil follows.
    [Fact]
    public void AStockpileSaleNamesItselfWithoutBeingPickable()
    {
        var kind = TreasuryTransactionKinds.Find(TreasuryTransactionKinds.SoldAnItem)!;

        Assert.Equal("Gil In — Stockpile Item Sold", kind.Label);
        Assert.False(kind.IsPickable);
        // NOT retired: ItemSaleRecorder still records it on every sale, and a Fix has to reproduce it.
        Assert.False(kind.IsRetired);
        Assert.Equal(TreasuryAccounts.ItemSales, kind.TakeFrom);
    }

    // Retiring a kind must never remove it. Every row in the treasury stores the key it was recorded
    // under and the label is resolved at render time, so deleting one would relabel history with
    // whatever category the entry happens to have landed in.
    [Fact]
    public void RetiredKindsStayInTheCatalogSoHistoryStillReads()
    {
        foreach (var key in new[]
        {
            TreasuryTransactionKinds.SplitGilAmongMembers,
            TreasuryTransactionKinds.WeOweAMember,
            TreasuryTransactionKinds.WePaidWhatWeOwed,
            TreasuryTransactionKinds.FoundExtraGil,
            TreasuryTransactionKinds.MissingGil,
        })
        {
            Assert.NotNull(TreasuryTransactionKinds.Find(key));
            // Not the category-name fallback — a real sentence saying what happened.
            Assert.NotEqual("Gil paid to members", TreasuryTransactionKinds.LabelFor(key, "Gil paid to members"));
        }

        Assert.Equal(
            "Split Gil — Paid now",
            TreasuryTransactionKinds.LabelFor(TreasuryTransactionKinds.SplitGilAmongMembers, null));
    }

    // The transactions list is meant to speak the same language as the picker, so every label reads
    // as its action — either the action alone, for the two that have a single reason, or
    // "Action — Reason". Getting this wrong is invisible until someone reads the list and finds a
    // row named after a button that no longer exists.
    [Fact]
    public void EveryLabelReadsAsItsAction()
    {
        foreach (var kind in TreasuryTransactionKinds.All)
        {
            var action = TreasuryTransactionActions.LabelFor(kind.Action)!;
            Assert.True(
                kind.Label == action || kind.Label.StartsWith($"{action} — ", StringComparison.Ordinal),
                $"{kind.Key} is labelled \"{kind.Label}\", which does not read as its action \"{action}\"");
        }
    }

    // Gil promised to members — one of them or a dozen — is ONE button, and moving any of it back
    // under Gil Out is the mistake to catch ("Gil Out" is untrue while nobody has been ticked off
    // the who-we-owe panel). The preview has to keep saying so out loud.
    //
    // It was two buttons, "Split Gil" and "We Owe a Member", which asked the officer to classify a
    // payout before they had picked anybody and filed one movement under two labels. The single
    // button takes a list, and a list of one is still a list.
    [Fact]
    public void OwingMembersIsOneActionThatMovesNoGilOnHand()
    {
        var kind = TreasuryTransactionKinds.Find(TreasuryTransactionKinds.WeOweSeveralMembers)!;

        Assert.Equal(TreasuryTransactionActions.WhatWeOwe, kind.Action);
        Assert.True(kind.IsPickable);
        Assert.False(kind.IsRetired);
        // Neither half is gil on hand: the obligation is recorded, the gil stays put.
        Assert.NotEqual(TreasuryAccounts.GilOnHand, kind.AddTo);
        Assert.NotEqual(TreasuryAccounts.GilOnHand, kind.TakeFrom);
        // It lands on the same category the tick-and-pay panel draws down, so every member picked
        // appears on the who-we-owe panel and can be settled there one at a time.
        Assert.Equal(TreasuryAccounts.WeOwe, kind.TakeFrom);
        Assert.Equal(TreasuryAccounts.WeOwe, kind.SplitAccount);
        Assert.True(kind.IsSplittable);
        Assert.True(kind.ShowsMember);
        // The only reason under its action, so the form asks nothing beyond who, how much and when.
        Assert.Equal(
            kind.Key,
            Assert.Single(TreasuryTransactionKinds.ReasonsFor(TreasuryTransactionActions.WhatWeOwe)).Key);

        Assert.Contains("does not change", kind.PreviewTemplate, StringComparison.OrdinalIgnoreCase);
    }

    // The single-member kind it replaced. Same two categories, same direction, same panel — which
    // is exactly why one button can do both, and why this one is retired rather than deleted.
    [Fact]
    public void TheRetiredSingleMemberKindMovedTheSameTwoCategories()
    {
        var folded = TreasuryTransactionKinds.Find(TreasuryTransactionKinds.WeOweAMember)!;
        var merged = TreasuryTransactionKinds.Find(TreasuryTransactionKinds.WeOweSeveralMembers)!;

        Assert.False(folded.IsPickable);
        Assert.True(folded.IsRetired);
        Assert.Equal(merged.AddTo, folded.AddTo);
        Assert.Equal(merged.TakeFrom, folded.TakeFrom);
        // Its own action survives with nothing pickable behind it, so its entries keep their label.
        Assert.Equal(TreasuryTransactionActions.OweAMember, folded.Action);
        Assert.Equal("We Owe a Member", TreasuryTransactionKinds.LabelFor(folded.Key, null));
    }

    // Owed-to-us is the one balance a free-text Gil In cannot touch. Recording the arrival as
    // ordinary Gil In would count it as new income and leave the debt standing forever, so the entry
    // that clears it has to stay recordable — but NOT on the menu. Both halves of the balance sheet
    // are tick-and-record lists now, so clearing either direction is a tick, exactly like paying a
    // member off the who-we-owe list. Putting the arrival back in the picker is the mistake to catch.
    [Fact]
    public void OwedToUsIsRecordedByTypingAndClearedByTicking()
    {
        var owed = Assert.Single(TreasuryTransactionKinds.ReasonsFor(TreasuryTransactionActions.OwedToUs));
        Assert.Equal(TreasuryTransactionKinds.SomeoneOwesUsForWork, owed.Key);
        // Records the obligation and moves no gil at all.
        Assert.Equal(TreasuryAccounts.OwedToUs, owed.AddTo);
        Assert.NotEqual(TreasuryAccounts.GilOnHand, owed.TakeFrom);

        var paid = TreasuryTransactionKinds.Find(TreasuryTransactionKinds.TheyPaidWhatTheyOwed)!;
        // Draws the owed-to-us balance down, and the gil genuinely arrives.
        Assert.Equal(TreasuryAccounts.GilOnHand, paid.AddTo);
        Assert.Equal(TreasuryAccounts.OwedToUs, paid.TakeFrom);
        // Off the menu, but NOT retired: the tick panel records it and Fix has to reproduce one.
        // Exactly how the mirror on the other side of the sheet is treated.
        Assert.False(paid.IsPickable);
        Assert.False(paid.IsRetired);
        var settled = TreasuryTransactionKinds.Find(TreasuryTransactionKinds.WePaidWhatWeOwed)!;
        Assert.Equal(settled.IsPickable, paid.IsPickable);
        Assert.Equal(settled.IsRetired, paid.IsRetired);

        // Both name a party in free text rather than off the roster: whoever owes a linkshell gil is
        // usually not in it. "Member" on either of these is the label that sent officers hunting the
        // roster for a name that was never on it.
        foreach (var kind in new[] { owed, paid })
        {
            Assert.True(kind.ShowsMember);
            Assert.NotEqual(TreasuryLabels.Member, kind.CounterpartyLabel);
            Assert.False(kind.SettlesMemberDebt, $"{kind.Key} would open the roster menu");
        }
    }

    // THE property that retires the "Which one?" dropdown: no action an officer can pick has a
    // second reason behind it, so neither front-end ever renders the select. Both hide it by
    // counting the reasons under the chosen action, so one kind landing under an existing action is
    // all it would take to bring the dropdown back everywhere.
    [Fact]
    public void NoPickableActionAsksASecondQuestion()
    {
        foreach (var (key, label) in TreasuryTransactionActions.All)
        {
            var reasons = TreasuryTransactionKinds.ReasonsFor(key).ToList();
            Assert.True(reasons.Count <= 1, $"the \"{label}\" button would show a reason dropdown");
        }
    }

    // Collapsing the two would either let a stale form record a superseded kind, or stop the app
    // recording a gil count.
    [Fact]
    public void OnlySupersededKindsAreRetired()
    {
        var retired = TreasuryTransactionKinds.All.Where(kind => kind.IsRetired).Select(kind => kind.Key).ToList();

        Assert.Equal(
            new[]
            {
                TreasuryTransactionKinds.SplitGilAmongMembers,
                TreasuryTransactionKinds.WeOweAMember,
                TreasuryTransactionKinds.GotADonation,
                TreasuryTransactionKinds.GotPaidForWork,
                TreasuryTransactionKinds.BoughtSomething,
                TreasuryTransactionKinds.StartingGil,
                TreasuryTransactionKinds.FoundExtraGil,
                TreasuryTransactionKinds.MissingGil,
            }.OrderBy(key => key),
            retired.OrderBy(key => key));

        // A retired kind is never also offered.
        Assert.All(
            TreasuryTransactionKinds.All.Where(kind => kind.IsRetired),
            kind => Assert.False(kind.IsPickable, $"{kind.Key} is retired but still in the picker"));
    }

    // An action button that opens an empty reason list is a dead end — so every action that is still
    // OFFERED has to have something behind it.
    //
    // Three are history-only, and deliberately so: Setup ("Starting Gil" and "Gil on Hand" were
    // removed), Split Gil and We Owe a Member (both folded into What We Owe, which takes one member
    // or several). Nothing pickable is filed under any of them, which is precisely what makes both
    // front-ends drop them from the button row — each one renders only the actions its OWN option
    // list mentions — so they cost nothing but keep their entries reading as what they were.
    [Fact]
    public void EveryOfferedActionHasAReasonBehindIt()
    {
        var historyOnly = new[]
        {
            TreasuryTransactionActions.SplitGil,
            TreasuryTransactionActions.OweAMember,
            TreasuryTransactionActions.Setup,
        };

        foreach (var (key, label) in TreasuryTransactionActions.All)
        {
            var reasons = TreasuryTransactionKinds.ReasonsFor(key).ToList();
            if (historyOnly.Contains(key))
            {
                Assert.Empty(reasons);
                // The button hides because nothing pickable points here. A kind sneaking back onto
                // the menu under one of these would resurrect a button nobody meant to bring back.
                Assert.All(
                    TreasuryTransactionKinds.All.Where(kind => kind.Action == key),
                    kind => Assert.False(kind.IsPickable, $"{kind.Key} revives the \"{label}\" button"));
                continue;
            }
            Assert.True(reasons.Count > 0, $"the \"{label}\" button opens an empty list");
        }
    }

    // Within an action, not across it: "Something else" is deliberately both a Gil In reason and a
    // Gil Out one, because the action beside it supplies the context.
    [Fact]
    public void ReasonLabelsAreUniqueWithinAnAction()
    {
        foreach (var group in TreasuryTransactionKinds.All.GroupBy(kind => kind.Action))
        {
            var labels = group.Select(kind => kind.ReasonLabel).ToList();
            Assert.Equal(
                labels.Count,
                labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        // The collision that proved the rule was "Something else" under both Gil In and Gil Out.
        // Both are gone: those two actions now offer one thing each and the picker asks no second
        // question, so the words that used to need disambiguating are simply the action's own.
        Assert.DoesNotContain(
            TreasuryTransactionKinds.All,
            kind => kind.IsPickable
                && kind.ReasonLabel.Equals("Something else", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryDeclaredActionIsUsed()
    {
        var used = TreasuryTransactionKinds.All.Select(kind => kind.Action).Distinct().ToList();
        Assert.All(used, action => Assert.True(TreasuryTransactionActions.IsKnown(action)));
        Assert.All(
            TreasuryTransactionActions.All,
            action => Assert.Contains(action.Key, used));
        Assert.Null(TreasuryTransactionActions.LabelFor("NotAnAction"));
        Assert.Null(TreasuryTransactionActions.LabelFor(null));
    }

    // THE regression test for the sharpest bug this replaced. Given only Pickable(), the Fix form
    // for an entry whose kind is not on the menu renders a select with no matching option, the
    // browser silently selects the FIRST one, and saving re-files the entry onto two entirely
    // different categories — a gil count becomes an item sale, at the right amount, with nothing on
    // screen to say so.
    [Fact]
    public void PickableWith_AlwaysOffersTheEntrysOwnKind()
    {
        var pickable = TreasuryTransactionKinds.Pickable().Select(kind => kind.Key).ToList();

        foreach (var key in TreasuryTransactionKinds.All.Where(kind => !kind.IsPickable).Select(kind => kind.Key))
        {
            var offered = TreasuryTransactionKinds.PickableWith(key).Select(kind => kind.Key).ToList();
            Assert.Contains(key, offered);
            Assert.Equal(pickable.Count + 1, offered.Count);
        }

        // An already-pickable kind is not offered twice.
        var gilIn = TreasuryTransactionKinds.PickableWith(TreasuryTransactionKinds.OtherMoneyIn)
            .Select(kind => kind.Key).ToList();
        Assert.Equal(pickable, gilIn);

        // Nothing to add: a row with no kind, or one nobody recognises.
        Assert.Equal(pickable, TreasuryTransactionKinds.PickableWith(null).Select(kind => kind.Key));
        Assert.Equal(pickable, TreasuryTransactionKinds.PickableWith("NotAThing").Select(kind => kind.Key));
    }

    // An item merely LISTED on the auction house is not a sale: no buyer, no agreed price, nothing
    // owed. It must not be possible to record one, because there is nothing to record until it sells.
    [Fact]
    public void NothingRecordsAListingThatHasNotSold()
    {
        // Label AND ReasonLabel — both are things an officer picks from. Help is deliberately
        // exempt: "set up a payout list" is the clearest description of Split Gil there is, and it
        // describes a list of PEOPLE, not a listing on the auction house.
        Assert.DoesNotContain(
            TreasuryTransactionKinds.All,
            kind => kind.Label.Contains("list", StringComparison.OrdinalIgnoreCase)
                || kind.ReasonLabel.Contains("list", StringComparison.OrdinalIgnoreCase)
                || kind.Help.Contains("not sold", StringComparison.OrdinalIgnoreCase));
    }

    // THE test that keeps jargon off the screen. Every string a user sees comes from TreasuryLabels or
    // TreasuryTransactionKinds, and none of it may contain a bookkeeping term — the structure
    // underneath is rigorous, but the words are the words an officer would actually say.
    [Fact]
    public void NoUserVisibleStringUsesBookkeepingJargon()
    {
        var strings = typeof(TreasuryLabels)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (Source: $"TreasuryLabels.{field.Name}", Text: (string)field.GetRawConstantValue()!))
            .Concat(TreasuryTransactionKinds.All.SelectMany(kind => new[]
            {
                (Source: $"{kind.Key}.Label", Text: kind.Label),
                (Source: $"{kind.Key}.ReasonLabel", Text: kind.ReasonLabel),
                (Source: $"{kind.Key}.Help", Text: kind.Help),
                (Source: $"{kind.Key}.PreviewTemplate", Text: kind.PreviewTemplate),
                (Source: $"{kind.Key}.CounterpartyLabel", Text: kind.CounterpartyLabel),
            }))
            .Concat(LedgerAccountDefaults.BuildDefaultAccounts(1).SelectMany(account => new[]
            {
                (Source: $"category {account.AccountNumber}.Name", Text: account.Name),
                (Source: $"category {account.AccountNumber}.Description", Text: account.Description ?? string.Empty),
            }))
            .ToList();

        // Sanity: this only proves anything if it is actually looking at the strings.
        Assert.True(strings.Count > 60, $"expected to scan the whole vocabulary, scanned {strings.Count}");

        foreach (var (source, text) in strings)
        {
            // The one exemption: the label for the collapsed panel that deliberately DOES show the two
            // halves, for the one officer who wants to see them.
            if (source == $"TreasuryLabels.{nameof(TreasuryLabels.ShowDetails)}")
            {
                continue;
            }
            foreach (var forbidden in TreasuryLabels.Forbidden)
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"{source} says \"{text}\" — '{forbidden}' is bookkeeping jargon, use plain English");
            }
        }
    }

    [Fact]
    public void ClassLabels_ArePlainEnglishForEveryClass()
    {
        foreach (var accountClass in new[]
                 {
                     LedgerAccountClasses.Holds, LedgerAccountClasses.Owes, LedgerAccountClasses.Worth,
                     LedgerAccountClasses.MoneyIn, LedgerAccountClasses.MoneyOut,
                 })
        {
            var label = TreasuryLabels.ClassLabel(accountClass);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.NotEqual(accountClass, label);
        }

        Assert.Equal("Uncategorized", TreasuryLabels.ClassLabel("something else"));
    }

    [Fact]
    public void EntryReference_ReadsAsAnOfficerWouldQuoteIt()
    {
        // Padding is a storage detail, not something an officer quotes.
        Assert.Equal("#142", TreasuryLabels.EntryReference("000142"));
        Assert.Equal("#8", TreasuryLabels.EntryReference("000008"));
        Assert.Equal("#142", TreasuryLabels.EntryReference("142"));
        Assert.Equal("#", TreasuryLabels.EntryReference(null));
        // Sequence starts at 1, so this is unreachable — it just must not read as "#".
        Assert.Equal("#0", TreasuryLabels.EntryReference("000000"));
    }

    [Theory]
    [InlineData(JournalEntryKinds.Reversal, true)]
    [InlineData(JournalEntryKinds.Correction, true)]
    [InlineData(JournalEntryKinds.Standard, false)]
    [InlineData(JournalEntryKinds.Opening, false)]
    [InlineData(JournalEntryKinds.Adjustment, false)]
    [InlineData(JournalEntryKinds.Migration, false)]
    public void OnlyFixesRequireAReason(string kind, bool expected)
    {
        // Mirrored by the CK_JournalEntries_ReasonRequiredForFixes constraint, so an entry claiming an
        // earlier one was wrong cannot be stored without saying why.
        Assert.Equal(expected, JournalEntryKinds.RequiresReason(kind));
    }

    [Fact]
    public void StatusHelpers_AreCaseInsensitive()
    {
        Assert.True(JournalEntryStatuses.IsDraft("draft"));
        Assert.True(JournalEntryStatuses.IsConfirmed("CONFIRMED"));
        Assert.False(JournalEntryStatuses.IsDraft(null));
        Assert.False(JournalEntryStatuses.IsConfirmed("Draft"));
    }

    // A member's name is written onto every half that is NOT gil on hand. For an entry where
    // neither half is — "someone owes us for work", now a top-level button — that is the same name
    // twice, and the row used to read "Bob, Bob" and take the who-got-what layout meant for splits.
    [Fact]
    public void RecipientsOf_NamesEachPersonOnce()
    {
        var owedToUs = new JournalEntry
        {
            EntryNumber = "000001",
            Lines =
            {
                // 1200 and 4200 — neither is gil on hand, so both carry the name.
                new JournalEntryLine { LineNumber = 1, AccountNumber = TreasuryAccounts.OwedToUs, Amount = 300_000, CounterpartyCharacterName = "Bob" },
                new JournalEntryLine { LineNumber = 2, AccountNumber = TreasuryAccounts.PaidWork, Amount = -300_000, CounterpartyCharacterName = "Bob" },
            },
        };

        Assert.Equal(new[] { "Bob" }, ManageFinancesController.RecipientsOf(owedToUs));

        // A real split still names everyone: its recipients are distinct people, so nothing collides.
        var split = new JournalEntry
        {
            EntryNumber = "000002",
            Lines =
            {
                new JournalEntryLine { LineNumber = 1, AccountNumber = TreasuryAccounts.GilToMembers, Amount = 900_000 },
                new JournalEntryLine { LineNumber = 2, AccountNumber = TreasuryAccounts.WeOwe, Amount = -300_000, CounterpartyCharacterName = "Ashira" },
                new JournalEntryLine { LineNumber = 3, AccountNumber = TreasuryAccounts.WeOwe, Amount = -300_000, CounterpartyCharacterName = "Millhouse" },
                new JournalEntryLine { LineNumber = 4, AccountNumber = TreasuryAccounts.WeOwe, Amount = -300_000, CounterpartyCharacterName = "Zeid" },
            },
        };

        Assert.Equal(new[] { "Ashira", "Millhouse", "Zeid" }, ManageFinancesController.RecipientsOf(split));
    }

    // The mirror of EveryKindThatTouchesWhatWeOweNamesAMember, for the figure that had no names
    // behind it at all. A linkshell has no bank: gil on hand is the sum of what sits on members'
    // mules, so gil that moves without naming one cannot be found again.
    [Fact]
    public void EveryKindThatMovesGilOnHandNamesAMule()
    {
        var moving = TreasuryTransactionKinds.All.Where(kind => kind.MovesCashOnHand).ToList();

        Assert.NotEmpty(moving);
        Assert.All(moving, kind =>
            Assert.True(kind.RequiresHolder, $"{kind.Key} moves gil on hand but does not name a mule"));
    }

    // And nothing else does. A promise that has not changed hands has no mule to name — asking for
    // one would be asking where gil is that nobody has handed over yet.
    [Fact]
    public void NoKindAsksForAMuleWithoutMovingGil()
    {
        var promises = TreasuryTransactionKinds.All.Where(kind => !kind.MovesCashOnHand).ToList();

        Assert.NotEmpty(promises);
        Assert.All(promises, kind =>
            Assert.False(kind.RequiresHolder, $"{kind.Key} moves no gil but asks whose mule it is on"));
    }

    // The label has to flip with the direction, or half the form asks the wrong question: "who's
    // holding this gil" reads as though the payer keeps it when gil is going out.
    [Fact]
    public void TheMuleQuestionFlipsWithTheDirection()
    {
        var arriving = TreasuryTransactionKinds.Find(TreasuryTransactionKinds.OtherMoneyIn)!;
        var leaving = TreasuryTransactionKinds.Find(TreasuryTransactionKinds.OtherMoneyOut)!;

        Assert.True(arriving.BringsCashIn);
        Assert.False(leaving.BringsCashIn);
        Assert.NotEqual(arriving.HolderLabel, leaving.HolderLabel);
        Assert.Equal(TreasuryLabels.WhoIsHoldingIt, arriving.HolderLabel);
        Assert.Equal(TreasuryLabels.WhosePocketItLeft, leaving.HolderLabel);
    }
}
