using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Data
{
    public class ApplicationDbContext : IdentityDbContext<AppUser>
    {
        // Singleton queues; nullable + defaulted so design-time / reflection
        // construction of the context never fails when they aren't supplied.
        private readonly DiscordTodBoardQueue? _todBoardQueue;
        private readonly DiscordAuctionChannelQueue? _auctionChannelQueue;
        private readonly DiscordEventChannelQueue? _eventChannelQueue;
        private readonly DiscordEventEndedQueue? _eventEndedQueue;
        private readonly DiscordEventCleanupQueue? _eventCleanupQueue;
        private readonly DiscordLootChannelQueue? _lootChannelQueue;
        private readonly DkpSheetPostQueue? _dkpSheetPostQueue;
        // Live change-feed bus (long-poll). Nullable + defaulted like the queues
        // so design-time / reflection construction never fails when it's absent.
        private readonly LinkshellChangeNotifier? _changeNotifier;

        public ApplicationDbContext(
            DbContextOptions<ApplicationDbContext> options,
            DiscordTodBoardQueue? todBoardQueue = null,
            DiscordAuctionChannelQueue? auctionChannelQueue = null,
            DiscordEventChannelQueue? eventChannelQueue = null,
            DiscordLootChannelQueue? lootChannelQueue = null,
            LinkshellChangeNotifier? changeNotifier = null,
            DiscordEventEndedQueue? eventEndedQueue = null,
            DiscordEventCleanupQueue? eventCleanupQueue = null,
            DkpSheetPostQueue? dkpSheetPostQueue = null)
            : base(options)
        {
            _todBoardQueue = todBoardQueue;
            _auctionChannelQueue = auctionChannelQueue;
            _eventChannelQueue = eventChannelQueue;
            _lootChannelQueue = lootChannelQueue;
            _changeNotifier = changeNotifier;
            _eventEndedQueue = eventEndedQueue;
            _eventCleanupQueue = eventCleanupQueue;
            _dkpSheetPostQueue = dkpSheetPostQueue;
        }

        // Distinct linkshell ids of Tod rows changed in the current
        // SaveChanges, captured pre-save (entry states reset afterwards) and
        // enqueued only on a successful commit so the live Discord ToD board
        // rebuilds on every mutation path without per-controller wiring.
        private List<int> CollectChangedTodLinkshellIds()
        {
            if (_todBoardQueue is null)
            {
                return new List<int>();
            }
            var ids = new HashSet<int>();
            foreach (var entry in ChangeTracker.Entries<Tod>())
            {
                var changed = entry.State is EntityState.Added
                    or EntityState.Modified
                    or EntityState.Deleted;
                if (changed && entry.Entity.LinkshellId > 0)
                {
                    ids.Add(entry.Entity.LinkshellId);
                }
            }
            return ids.ToList();
        }

        private void EnqueueTodBoardRefreshes(IReadOnlyList<int> linkshellIds)
        {
            if (_todBoardQueue is null)
            {
                return;
            }
            foreach (var id in linkshellIds)
            {
                _todBoardQueue.Enqueue(id);
            }
        }

        // Linkshells whose member DKP changed this save → refresh the live DKP sheet
        // Discord post (full table + .xlsx, edit-in-place). Any DkpLedgerEntry
        // add/edit/delete or an AppUserLinkshell whose LinkshellDkp was modified
        // counts, so every DKP path triggers it without per-controller wiring.
        // Captured pre-save; enqueued only on a successful commit. The publisher
        // no-ops for linkshells with no DKP channel configured.
        private List<int> CollectDkpChangedLinkshellIds()
        {
            if (_dkpSheetPostQueue is null)
            {
                return new List<int>();
            }
            var ids = new HashSet<int>();
            foreach (var entry in ChangeTracker.Entries<DkpLedgerEntry>())
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                    && entry.Entity.LinkshellId > 0)
                {
                    ids.Add(entry.Entity.LinkshellId);
                }
            }
            foreach (var entry in ChangeTracker.Entries<AppUserLinkshell>())
            {
                if (entry.State == EntityState.Modified
                    && entry.Property(nameof(AppUserLinkshell.LinkshellDkp)).IsModified
                    && entry.Entity.LinkshellId > 0)
                {
                    ids.Add(entry.Entity.LinkshellId);
                }
            }
            // Renaming/adding/removing a pool, or remapping an event type, changes the sheet's
            // per-pool columns without moving a single DKP amount — so watch the pool config too.
            foreach (var entry in ChangeTracker.Entries<DkpPool>())
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                    && entry.Entity.LinkshellId > 0)
                {
                    ids.Add(entry.Entity.LinkshellId);
                }
            }
            foreach (var entry in ChangeTracker.Entries<DkpPoolEventType>())
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                    && entry.Entity.LinkshellId > 0)
                {
                    ids.Add(entry.Entity.LinkshellId);
                }
            }
            return ids.ToList();
        }

        private void EnqueueDkpSheetPosts(IReadOnlyList<int> linkshellIds)
        {
            if (_dkpSheetPostQueue is null)
            {
                return;
            }
            foreach (var id in linkshellIds)
            {
                _dkpSheetPostQueue.Enqueue(id);
            }
        }

        // Newly-created auctions (→ post an "auction opened" embed) and
        // newly-created auction histories (→ post the "closed" results embed)
        // to the linkshell's Auctions webhook. Only *Added* rows are collected.
        // The publisher no-ops when no Auctions webhook is configured.
        private (List<Auction> created, List<AuctionHistory> closed) CollectAuctionChannelWork()
        {
            if (_auctionChannelQueue is null)
            {
                return (new List<Auction>(), new List<AuctionHistory>());
            }
            var created = new List<Auction>();
            foreach (var entry in ChangeTracker.Entries<Auction>())
            {
                if (entry.State == EntityState.Added && entry.Entity.LinkshellId > 0)
                {
                    created.Add(entry.Entity);
                }
            }
            var closed = new List<AuctionHistory>();
            foreach (var entry in ChangeTracker.Entries<AuctionHistory>())
            {
                if (entry.State == EntityState.Added && entry.Entity.LinkshellId > 0)
                {
                    closed.Add(entry.Entity);
                }
            }
            return (created, closed);
        }

        // Newly-created events (→ announce to the linkshell's Discord channel for
        // the event's type, with an inline job-signup component). Captured as
        // entity references pre-save because the db-generated Id isn't known
        // until after commit. The publisher no-ops when no matching channel is
        // configured (or the bot/token is unavailable).
        private List<Event> CollectAddedEvents()
        {
            if (_eventChannelQueue is null)
            {
                return new List<Event>();
            }
            var created = new List<Event>();
            foreach (var entry in ChangeTracker.Entries<Event>())
            {
                if (entry.State == EntityState.Added && entry.Entity.LinkshellId > 0)
                {
                    created.Add(entry.Entity);
                }
            }
            return created;
        }

        private void EnqueueEventPosts(IReadOnlyList<Event> created)
        {
            if (_eventChannelQueue is null)
            {
                return;
            }
            foreach (var eventEntity in created)
            {
                if (eventEntity.Id > 0)
                {
                    _eventChannelQueue.Enqueue(eventEntity.Id);
                }
            }
        }

        // Already-announced events whose details changed this save → re-render the
        // posted Discord message so edits (name, time, DKP/hour, location, party
        // setup) show immediately instead of only when a signup interaction next
        // refreshes the board. The publisher edits in place when DiscordMessageId
        // is set. Skips the publisher's OWN post-write (which sets DiscordMessageId)
        // so a fresh post doesn't immediately re-edit itself.
        private List<Event> CollectEditedEvents()
        {
            if (_eventChannelQueue is null)
            {
                return new List<Event>();
            }
            var edited = new List<Event>();
            foreach (var entry in ChangeTracker.Entries<Event>())
            {
                if (entry.State != EntityState.Modified)
                {
                    continue;
                }
                // Not posted yet → nothing to edit (a later post renders current state).
                if (string.IsNullOrEmpty(entry.Entity.DiscordMessageId))
                {
                    continue;
                }
                // The publisher assigns DiscordMessageId/DiscordChannelId on first
                // post; that save shouldn't trigger a redundant edit of the same
                // content.
                if (entry.Property(nameof(Event.DiscordMessageId)).IsModified)
                {
                    continue;
                }
                edited.Add(entry.Entity);
            }
            return edited;
        }

        // Newly-created EventHistory rows (an event closed → post an "event ended"
        // summary to the linkshell's Discord channel). Captured as entity
        // references pre-save because the db-generated Id isn't known until after
        // commit. Both the web (EndEventCoreAsync) and Activity (EventsLifecycle)
        // close paths add an EventHistory, so this fires for either without
        // per-controller wiring.
        private List<EventHistory> CollectAddedEventHistories()
        {
            if (_eventEndedQueue is null)
            {
                return new List<EventHistory>();
            }
            var created = new List<EventHistory>();
            foreach (var entry in ChangeTracker.Entries<EventHistory>())
            {
                if (entry.State == EntityState.Added)
                {
                    created.Add(entry.Entity);
                }
            }
            return created;
        }

        private void EnqueueEventEndedSummaries(IReadOnlyList<EventHistory> created)
        {
            if (_eventEndedQueue is null)
            {
                return;
            }
            foreach (var history in created)
            {
                if (history.Id > 0)
                {
                    _eventEndedQueue.Enqueue(history.Id);
                }
            }
        }

        // An event's signup/party board lives as one Discord message whose ids
        // are stored on the Event row. When the event is deleted (every end +
        // cancel path removes the Event), capture those ids pre-save so the board
        // can be deleted post-commit — otherwise it lingers in the channel.
        private List<DiscordMessageRef> CollectDeletedEventBoards()
        {
            if (_eventCleanupQueue is null)
            {
                return new List<DiscordMessageRef>();
            }
            var boards = new List<DiscordMessageRef>();
            foreach (var entry in ChangeTracker.Entries<Event>())
            {
                if (entry.State != EntityState.Deleted)
                {
                    continue;
                }
                var channelId = entry.Entity.DiscordChannelId;
                var messageId = entry.Entity.DiscordMessageId;
                if (!string.IsNullOrWhiteSpace(channelId) && !string.IsNullOrWhiteSpace(messageId))
                {
                    boards.Add(new DiscordMessageRef(channelId!, messageId!));
                }
            }
            return boards;
        }

        // An auction accumulates one bot-posted card per state change (the
        // "opened" card + one per bid), with all their message ids stored
        // comma-separated on the Auction row. When the auction is deleted (close
        // removes it, after archiving to AuctionHistory), capture every card id
        // so they can be removed from the channel — leaving just the results
        // summary that DiscordAuctionChannelPublisher posts on close.
        private List<DiscordMessageRef> CollectDeletedAuctionCards()
        {
            if (_eventCleanupQueue is null)
            {
                return new List<DiscordMessageRef>();
            }
            var cards = new List<DiscordMessageRef>();
            foreach (var entry in ChangeTracker.Entries<Auction>())
            {
                if (entry.State != EntityState.Deleted)
                {
                    continue;
                }
                var channelId = entry.Entity.DiscordChannelId;
                if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(entry.Entity.DiscordMessageIds))
                {
                    continue;
                }
                foreach (var messageId in entry.Entity.DiscordMessageIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    cards.Add(new DiscordMessageRef(channelId!, messageId));
                }
            }
            return cards;
        }

        // Enqueues Discord messages to delete (event signup boards + auction
        // cards) onto the shared cleanup queue.
        private void EnqueueMessageDeletions(IReadOnlyList<DiscordMessageRef> messages)
        {
            if (_eventCleanupQueue is null)
            {
                return;
            }
            foreach (var message in messages)
            {
                _eventCleanupQueue.Enqueue(message);
            }
        }

        // Newly-awarded loot rows (ToD or event loot with a winner) → announce to
        // the linkshell's Loot channel. Captured as entity references pre-save
        // because the db-generated Id isn't known until after commit. Only
        // *Added* rows with a winner; edits (Modified) and close-out reattaches
        // are ignored. The publisher no-ops when no Loot channel is configured.
        private (List<TodLootDetail> tod, List<EventLootDetail> evt) CollectAddedLoot()
        {
            if (_lootChannelQueue is null)
            {
                return (new List<TodLootDetail>(), new List<EventLootDetail>());
            }
            var tod = new List<TodLootDetail>();
            foreach (var entry in ChangeTracker.Entries<TodLootDetail>())
            {
                if (entry.State == EntityState.Added && !string.IsNullOrWhiteSpace(entry.Entity.ItemWinner))
                {
                    tod.Add(entry.Entity);
                }
            }
            var evt = new List<EventLootDetail>();
            foreach (var entry in ChangeTracker.Entries<EventLootDetail>())
            {
                if (entry.State == EntityState.Added && !string.IsNullOrWhiteSpace(entry.Entity.ItemWinner))
                {
                    evt.Add(entry.Entity);
                }
            }
            return (tod, evt);
        }

        private void EnqueueLootPosts(IReadOnlyList<TodLootDetail> tod, IReadOnlyList<EventLootDetail> evt)
        {
            if (_lootChannelQueue is null)
            {
                return;
            }
            foreach (var detail in tod)
            {
                if (detail.Id > 0)
                {
                    _lootChannelQueue.Enqueue(new DiscordLootJob(LootJobSource.Tod, detail.Id));
                }
            }
            foreach (var detail in evt)
            {
                if (detail.Id > 0)
                {
                    _lootChannelQueue.Enqueue(new DiscordLootJob(LootJobSource.Event, detail.Id));
                }
            }
        }

        private void EnqueueAuctionChannelWork(
            IReadOnlyList<Auction> created, IReadOnlyList<AuctionHistory> closed)
        {
            if (_auctionChannelQueue is null)
            {
                return;
            }
            foreach (var auction in created)
            {
                if (auction.Id > 0)
                {
                    _auctionChannelQueue.Enqueue(
                        new AuctionChannelJob(AuctionChannelJobKind.Create, auction.Id));
                }
            }
            foreach (var history in closed)
            {
                if (history.Id > 0)
                {
                    _auctionChannelQueue.Enqueue(
                        new AuctionChannelJob(AuctionChannelJobKind.Close, history.Id));
                }
            }
        }

        // Auction items that received a new Bid in this save → the auction's
        // posted Discord message should be edited in place to show the new high
        // bid. Only the item id is known pre-save; the auction id is resolved
        // from it post-commit (in EnqueueAuctionBidUpdatesAsync).
        private HashSet<int> CollectAddedBidAuctionItemIds()
        {
            var itemIds = new HashSet<int>();
            if (_auctionChannelQueue is null)
            {
                return itemIds;
            }
            foreach (var entry in ChangeTracker.Entries<Bid>())
            {
                if (entry.State == EntityState.Added && entry.Entity.AuctionItemId > 0)
                {
                    itemIds.Add(entry.Entity.AuctionItemId);
                }
            }
            return itemIds;
        }

        private async Task EnqueueAuctionBidUpdatesAsync(HashSet<int> bidItemIds)
        {
            if (_auctionChannelQueue is null || bidItemIds.Count == 0)
            {
                return;
            }
            try
            {
                var itemIds = bidItemIds.ToList();
                var auctionIds = await AuctionItems
                    .Where(item => itemIds.Contains(item.Id) && item.AuctionId != null)
                    .Select(item => item.AuctionId!.Value)
                    .Distinct()
                    .ToListAsync(CancellationToken.None);
                foreach (var auctionId in auctionIds)
                {
                    _auctionChannelQueue.Enqueue(new AuctionChannelJob(AuctionChannelJobKind.Update, auctionId));
                }
            }
            catch
            {
                // Best-effort: a failed resolve must never break the committed bid.
            }
        }

        // Per-save bucket of live-change work. Entities that carry LinkshellId
        // directly resolve to a (linkshell, area) pair immediately; child rows
        // (bids, sign-ups, loot) only know a parent FK pre-save, so we stash the
        // FK and resolve the linkshell with a batched lookup after the commit.
        private sealed class LiveChangeWork
        {
            public readonly HashSet<(int LinkshellId, string Area)> Direct = new();
            public readonly HashSet<int> BidAuctionItemIds = new();
            public readonly HashSet<int> ParticipationEventIds = new();
            public readonly HashSet<int> LootTodIds = new();
            public readonly HashSet<int> LootEventIds = new();

            public bool HasWork =>
                Direct.Count > 0 || BidAuctionItemIds.Count > 0 || ParticipationEventIds.Count > 0
                || LootTodIds.Count > 0 || LootEventIds.Count > 0;
        }

        // Scans the change tracker once for entities that drive the live tabs and
        // buckets them for post-commit notification. Captured pre-save (entry
        // states reset afterwards). Broad on purpose: the client refreshes the
        // active tab on ANY change to its linkshell, so coverage matters more
        // than per-area precision.
        private LiveChangeWork CollectLiveChanges()
        {
            var work = new LiveChangeWork();
            if (_changeNotifier is null)
            {
                return work;
            }

            foreach (var entry in ChangeTracker.Entries())
            {
                if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                {
                    continue;
                }

                switch (entry.Entity)
                {
                    case Event e when e.LinkshellId > 0:
                        work.Direct.Add((e.LinkshellId, LinkshellChangeNotifier.Areas.Events));
                        break;
                    case Auction a when a.LinkshellId > 0:
                        work.Direct.Add((a.LinkshellId, LinkshellChangeNotifier.Areas.Auctions));
                        break;
                    case AuctionHistory h when h.LinkshellId > 0:
                        work.Direct.Add((h.LinkshellId, LinkshellChangeNotifier.Areas.Auctions));
                        break;
                    case Tod t when t.LinkshellId > 0:
                        work.Direct.Add((t.LinkshellId, LinkshellChangeNotifier.Areas.Tods));
                        break;
                    case DkpLedgerEntry d when d.LinkshellId > 0:
                        work.Direct.Add((d.LinkshellId, LinkshellChangeNotifier.Areas.Dkp));
                        break;
                    case Invite i when i.LinkshellId > 0:
                        work.Direct.Add((i.LinkshellId, LinkshellChangeNotifier.Areas.Roster));
                        break;
                    case AppUserLinkshell m when m.LinkshellId > 0:
                        work.Direct.Add((m.LinkshellId, LinkshellChangeNotifier.Areas.Roster));
                        break;
                    case WindowEvent w when w.LinkshellId > 0:
                        work.Direct.Add((w.LinkshellId, LinkshellChangeNotifier.Areas.Windows));
                        break;
                    case PartySetup p when p.LinkshellId > 0:
                        work.Direct.Add((p.LinkshellId, LinkshellChangeNotifier.Areas.Parties));
                        break;
                    // Child rows — only a parent FK is known pre-save.
                    case Bid b when b.AuctionItemId > 0:
                        work.BidAuctionItemIds.Add(b.AuctionItemId);
                        break;
                    case AppUserEvent aue when aue.EventId > 0:
                        work.ParticipationEventIds.Add(aue.EventId);
                        break;
                    // Party-board slot signups (sign up / withdraw / move / seat /
                    // job-displacement) only touch this child row pre-start, so without
                    // this the live board changes weren't pushed — clients only caught
                    // them on the slow fallback poll. Resolve the linkshell via the Event
                    // (same bucket as participation) so every signup change is instant.
                    case EventPartySlotSignup eps when eps.EventId > 0:
                        work.ParticipationEventIds.Add(eps.EventId);
                        break;
                    case TodLootDetail tl when tl.TodId is int todId && todId > 0:
                        work.LootTodIds.Add(todId);
                        break;
                    case EventLootDetail el when el.EventId is int eventId && eventId > 0:
                        work.LootEventIds.Add(eventId);
                        break;
                }
            }

            return work;
        }

        // Resolves child-row linkshells (batched) and fires every notification.
        // Best-effort: runs after the commit, so a failure here must never bubble
        // up and fail an already-successful save — the polling timer covers any
        // missed push. Uses CancellationToken.None so a client that cancelled the
        // originating request still gets its change broadcast to everyone else.
        private async Task NotifyLiveChangesAsync(LiveChangeWork work)
        {
            if (_changeNotifier is null || !work.HasWork)
            {
                return;
            }

            try
            {
                var notifications = new HashSet<(int LinkshellId, string Area)>(work.Direct);

                if (work.BidAuctionItemIds.Count > 0)
                {
                    var itemIds = work.BidAuctionItemIds.ToList();
                    var linkshellIds = await AuctionItems
                        .Where(item => itemIds.Contains(item.Id) && item.Auction != null)
                        .Select(item => item.Auction!.LinkshellId)
                        .Distinct()
                        .ToListAsync(CancellationToken.None);
                    AddResolved(notifications, linkshellIds, LinkshellChangeNotifier.Areas.Auctions);
                }

                if (work.ParticipationEventIds.Count > 0)
                {
                    var eventIds = work.ParticipationEventIds.ToList();
                    var linkshellIds = await Events
                        .Where(e => eventIds.Contains(e.Id))
                        .Select(e => e.LinkshellId)
                        .Distinct()
                        .ToListAsync(CancellationToken.None);
                    AddResolved(notifications, linkshellIds, LinkshellChangeNotifier.Areas.Events);
                }

                if (work.LootTodIds.Count > 0)
                {
                    var todIds = work.LootTodIds.ToList();
                    var linkshellIds = await Tods
                        .Where(t => todIds.Contains(t.Id))
                        .Select(t => t.LinkshellId)
                        .Distinct()
                        .ToListAsync(CancellationToken.None);
                    AddResolved(notifications, linkshellIds, LinkshellChangeNotifier.Areas.Loot);
                }

                if (work.LootEventIds.Count > 0)
                {
                    var eventIds = work.LootEventIds.ToList();
                    var linkshellIds = await Events
                        .Where(e => eventIds.Contains(e.Id))
                        .Select(e => e.LinkshellId)
                        .Distinct()
                        .ToListAsync(CancellationToken.None);
                    AddResolved(notifications, linkshellIds, LinkshellChangeNotifier.Areas.Loot);
                }

                foreach (var (linkshellId, area) in notifications)
                {
                    _changeNotifier.Notify(linkshellId, area);
                }
            }
            catch
            {
                // Live push is an optimization; never let it fail a committed save.
            }
        }

        private static void AddResolved(
            HashSet<(int LinkshellId, string Area)> notifications, IEnumerable<int> linkshellIds, string area)
        {
            foreach (var linkshellId in linkshellIds)
            {
                if (linkshellId > 0)
                {
                    notifications.Add((linkshellId, area));
                }
            }
        }

        // Sync save path is rare (seeding/background) and never places bids or
        // sign-ups, so it only fires the directly-resolved notifications and
        // skips the async child lookups.
        private void NotifyLiveChangesDirect(LiveChangeWork work)
        {
            if (_changeNotifier is null)
            {
                return;
            }
            foreach (var (linkshellId, area) in work.Direct)
            {
                _changeNotifier.Notify(linkshellId, area);
            }
        }

        // Brand-new memberships start with an EMPTY pool attribution: any DkpLedgerEntry that
        // already exists for this (linkshell, account) predates the membership and must not be
        // resurrected as spendable pool DKP. This matters because a kicked member's ledger rows
        // SURVIVE the AppUserLinkshells delete — the ledger's only cascade is from Linkshell —
        // so a kick-then-rejoin would otherwise hand back DKP that LinkshellDkp says is gone.
        //
        // Doing it here rather than at the ~13 member-creation sites means the 14th one someone
        // adds next month can't forget it. It runs BEFORE this save assigns Ids to new ledger
        // rows, so an import that creates a placeholder membership AND its SheetImport row in a
        // single SaveChanges still attributes that row to the new membership.
        private List<AppUserLinkshell> CollectNewMembershipsNeedingDkpEpoch() =>
            ChangeTracker.Entries<AppUserLinkshell>()
                .Where(entry => entry.State == EntityState.Added
                    && entry.Entity.DkpPoolLedgerFromId == 0
                    && entry.Entity.LinkshellId > 0)
                .Select(entry => entry.Entity)
                .ToList();

        private async Task StampNewMembershipDkpEpochAsync(
            List<AppUserLinkshell> memberships, CancellationToken cancellationToken)
        {
            if (memberships.Count == 0)
            {
                return;
            }
            var linkshellIds = memberships.Select(m => m.LinkshellId).Distinct().ToList();
            var maxByLinkshell = await DkpLedgerEntries
                .Where(entry => linkshellIds.Contains(entry.LinkshellId))
                .GroupBy(entry => entry.LinkshellId)
                .Select(group => new { LinkshellId = group.Key, MaxId = group.Max(entry => entry.Id) })
                .ToDictionaryAsync(item => item.LinkshellId, item => item.MaxId, cancellationToken);
            foreach (var membership in memberships)
            {
                membership.DkpPoolLedgerFromId = maxByLinkshell.GetValueOrDefault(membership.LinkshellId);
            }
        }

        private void StampNewMembershipDkpEpoch(List<AppUserLinkshell> memberships)
        {
            if (memberships.Count == 0)
            {
                return;
            }
            var linkshellIds = memberships.Select(m => m.LinkshellId).Distinct().ToList();
            var maxByLinkshell = DkpLedgerEntries
                .Where(entry => linkshellIds.Contains(entry.LinkshellId))
                .GroupBy(entry => entry.LinkshellId)
                .Select(group => new { LinkshellId = group.Key, MaxId = group.Max(entry => entry.Id) })
                .ToDictionary(item => item.LinkshellId, item => item.MaxId);
            foreach (var membership in memberships)
            {
                membership.DkpPoolLedgerFromId = maxByLinkshell.GetValueOrDefault(membership.LinkshellId);
            }
        }

        // INV-1: every move of AppUserLinkshell.LinkshellDkp is matched, amount for amount, by
        // DkpLedgerEntry rows in the SAME save. That invariant is what lets pool balances be
        // DERIVED from the ledger (DkpPoolBalanceService) instead of cached in a second table.
        // DkpLedgerWriter upholds it by construction; this assertion catches anyone who bypasses
        // the writer and hand-rolls a balance mutation. ChangeTracker-only — no DB read.
        [System.Diagnostics.Conditional("DEBUG")]
        private void AssertDkpLedgerInvariant()
        {
            foreach (var member in ChangeTracker.Entries<AppUserLinkshell>())
            {
                if (member.State != EntityState.Modified
                    || !member.Property(nameof(AppUserLinkshell.LinkshellDkp)).IsModified)
                {
                    continue;
                }
                // An off-ledger balance overwrite is legal iff it also advances the pool epoch —
                // the placeholder-claim merge and the DKP import are the only such paths, and
                // both deliberately restate the balance rather than append to it.
                if (member.Property(nameof(AppUserLinkshell.DkpPoolLedgerFromId)).IsModified)
                {
                    continue;
                }

                var current = (double?)member.Property(nameof(AppUserLinkshell.LinkshellDkp)).CurrentValue ?? 0d;
                var original = (double?)member.Property(nameof(AppUserLinkshell.LinkshellDkp)).OriginalValue ?? 0d;
                var balanceDelta = current - original;

                var ledgerDelta = ChangeTracker.Entries<DkpLedgerEntry>()
                    .Where(entry => entry.Entity.LinkshellId == member.Entity.LinkshellId
                        && string.Equals(entry.Entity.AppUserId, member.Entity.AppUserId, StringComparison.Ordinal))
                    .Sum(entry =>
                    {
                        var originalAmount = entry.Property(nameof(DkpLedgerEntry.Amount)).OriginalValue as double? ?? 0d;
                        return entry.State switch
                        {
                            EntityState.Added => entry.Entity.Amount,
                            EntityState.Modified => entry.Entity.Amount - originalAmount,
                            EntityState.Deleted => -originalAmount,
                            _ => 0d,
                        };
                    });

                System.Diagnostics.Debug.Assert(
                    Math.Abs(balanceDelta - ledgerDelta) < 1e-6,
                    $"INV-1 violated: LinkshellDkp moved by {balanceDelta} for member {member.Entity.Id} "
                    + $"but the ledger moved by {ledgerDelta}. Every balance mutation must go through DkpLedgerWriter.");
            }
        }

        // INV-2: every JournalEntry's lines sum to zero. That is what makes "gil on hand" provable
        // rather than asserted — the balance is a single SUM over one column, and an entry that
        // moved gil out of nowhere cannot exist. TreasuryJournalWriter upholds it by construction;
        // this catches anyone who bypasses the writer and hand-builds an entry.
        //
        // ChangeTracker-only, no DB read. [Conditional("DEBUG")] to match INV-1: a release-mode
        // throw inside SaveChanges would add a new failure surface to every save in the whole app,
        // and the DB CHECK constraints already cover the cases a corrupt row could reach.
        [System.Diagnostics.Conditional("DEBUG")]
        private void AssertTreasuryEntriesBalance()
        {
            var touched = new HashSet<JournalEntry>();
            foreach (var line in ChangeTracker.Entries<JournalEntryLine>())
            {
                if (line.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                {
                    continue;
                }
                var header = line.Entity.JournalEntry;
                if (header is not null)
                {
                    touched.Add(header);
                }
            }
            foreach (var entry in ChangeTracker.Entries<JournalEntry>())
            {
                if (entry.State == EntityState.Added)
                {
                    touched.Add(entry.Entity);
                }
            }

            foreach (var header in touched)
            {
                // A zero-gil entry (a legacy sale recorded at 0) carries a single zero line rather
                // than a pair, and sums to zero either way.
                var sum = header.Lines.Sum(line => line.Amount);
                System.Diagnostics.Debug.Assert(
                    sum == 0L,
                    $"INV-2 violated: treasury entry {header.EntryNumber} (id {header.Id}) has lines summing to "
                    + $"{sum} instead of 0. Every entry must be built through TreasuryJournalWriter.");
            }
        }

        // INV-3: a Confirmed entry is never updated or deleted. A wrong entry is fixed by recording
        // an opposite one, so what was believed and when survives — that is the whole reason this
        // replaced RevenueEntries.Remove().
        //
        // Deletes are checked for MODIFICATION only, not blocked outright: deleting a linkshell
        // legitimately cascades its treasury away, and EF cascades tracked dependents itself, so a
        // blanket "no deletes" assert would fire on a legitimate linkshell deletion.
        [System.Diagnostics.Conditional("DEBUG")]
        private void AssertConfirmedTreasuryEntriesAreImmutable()
        {
            foreach (var entry in ChangeTracker.Entries<JournalEntry>())
            {
                if (entry.State != EntityState.Modified)
                {
                    continue;
                }
                // Draft -> Confirmed is the one legal transition, and it stamps the confirmer.
                var wasDraft = JournalEntryStatuses.IsDraft(
                    entry.Property(nameof(JournalEntry.Status)).OriginalValue as string);
                if (wasDraft)
                {
                    continue;
                }

                var changed = entry.Properties
                    .Where(property => property.IsModified)
                    .Select(property => property.Metadata.Name)
                    .ToArray();
                System.Diagnostics.Debug.Assert(
                    changed.Length == 0,
                    $"INV-3 violated: confirmed treasury entry {entry.Entity.EntryNumber} (id {entry.Entity.Id}) "
                    + $"was modified ({string.Join(", ", changed)}). Record a reversal or a correction instead.");
            }

            foreach (var line in ChangeTracker.Entries<JournalEntryLine>())
            {
                if (line.State != EntityState.Modified)
                {
                    continue;
                }
                var status = line.Entity.JournalEntry?.Status;
                System.Diagnostics.Debug.Assert(
                    status is null || JournalEntryStatuses.IsDraft(status),
                    $"INV-3 violated: a half of confirmed treasury entry {line.Entity.JournalEntryId} was modified. "
                    + "Record a reversal or a correction instead.");
            }
        }

        public override async Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            await StampNewMembershipDkpEpochAsync(CollectNewMembershipsNeedingDkpEpoch(), cancellationToken);
            AssertDkpLedgerInvariant();
            AssertTreasuryEntriesBalance();
            AssertConfirmedTreasuryEntriesAreImmutable();
            var affected = CollectChangedTodLinkshellIds();
            var (createdAuctions, closedAuctions) = CollectAuctionChannelWork();
            var createdEvents = CollectAddedEvents();
            var editedEvents = CollectEditedEvents();
            var (todLoot, eventLoot) = CollectAddedLoot();
            var endedEvents = CollectAddedEventHistories();
            var deletedEventBoards = CollectDeletedEventBoards();
            var deletedAuctionCards = CollectDeletedAuctionCards();
            var bidItemIds = CollectAddedBidAuctionItemIds();
            var dkpChangedLinkshells = CollectDkpChangedLinkshellIds();
            var liveChanges = CollectLiveChanges();
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            EnqueueTodBoardRefreshes(affected);
            EnqueueAuctionChannelWork(createdAuctions, closedAuctions);
            EnqueueEventPosts(createdEvents);
            EnqueueEventPosts(editedEvents);
            EnqueueLootPosts(todLoot, eventLoot);
            EnqueueEventEndedSummaries(endedEvents);
            EnqueueMessageDeletions(deletedEventBoards);
            EnqueueMessageDeletions(deletedAuctionCards);
            EnqueueDkpSheetPosts(dkpChangedLinkshells);
            await EnqueueAuctionBidUpdatesAsync(bidItemIds);
            await NotifyLiveChangesAsync(liveChanges);
            return result;
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            StampNewMembershipDkpEpoch(CollectNewMembershipsNeedingDkpEpoch());
            AssertDkpLedgerInvariant();
            AssertTreasuryEntriesBalance();
            AssertConfirmedTreasuryEntriesAreImmutable();
            var affected = CollectChangedTodLinkshellIds();
            var (createdAuctions, closedAuctions) = CollectAuctionChannelWork();
            var createdEvents = CollectAddedEvents();
            var editedEvents = CollectEditedEvents();
            var (todLoot, eventLoot) = CollectAddedLoot();
            var endedEvents = CollectAddedEventHistories();
            var deletedEventBoards = CollectDeletedEventBoards();
            var deletedAuctionCards = CollectDeletedAuctionCards();
            var dkpChangedLinkshells = CollectDkpChangedLinkshellIds();
            var liveChanges = CollectLiveChanges();
            var result = base.SaveChanges(acceptAllChangesOnSuccess);
            EnqueueTodBoardRefreshes(affected);
            EnqueueAuctionChannelWork(createdAuctions, closedAuctions);
            EnqueueEventPosts(createdEvents);
            EnqueueEventPosts(editedEvents);
            EnqueueLootPosts(todLoot, eventLoot);
            EnqueueEventEndedSummaries(endedEvents);
            EnqueueMessageDeletions(deletedEventBoards);
            EnqueueMessageDeletions(deletedAuctionCards);
            EnqueueDkpSheetPosts(dkpChangedLinkshells);
            NotifyLiveChangesDirect(liveChanges);
            return result;
        }

        public DbSet<DiscordActivityUser> DiscordActivityUsers => Set<DiscordActivityUser>();
        public DbSet<Linkshell> Linkshells => Set<Linkshell>();
        public DbSet<LinkshellBanner> LinkshellBanners => Set<LinkshellBanner>();
        public DbSet<AppUserLinkshell> AppUserLinkshells => Set<AppUserLinkshell>();
        public DbSet<Invite> Invites => Set<Invite>();
        public DbSet<Auction> Auctions => Set<Auction>();
        public DbSet<AuctionItem> AuctionItems => Set<AuctionItem>();
        public DbSet<Bid> Bids => Set<Bid>();
        public DbSet<AuctionHistory> AuctionHistories => Set<AuctionHistory>();
        public DbSet<Event> Events => Set<Event>();
        public DbSet<AppUserEvent> AppUserEvents => Set<AppUserEvent>();
        public DbSet<AppUserEventStatusLedger> AppUserEventStatusLedgers => Set<AppUserEventStatusLedger>();
        public DbSet<DkpLedgerEntry> DkpLedgerEntries => Set<DkpLedgerEntry>();
        public DbSet<DkpPool> DkpPools => Set<DkpPool>();
        public DbSet<DkpPoolEventType> DkpPoolEventTypes => Set<DkpPoolEventType>();
        public DbSet<EventHistory> EventHistories => Set<EventHistory>();
        public DbSet<AppUserEventHistory> AppUserEventHistories => Set<AppUserEventHistory>();
        public DbSet<EventLootDetail> EventLootDetails => Set<EventLootDetail>();
        public DbSet<EventPartySlotSignup> EventPartySlotSignups => Set<EventPartySlotSignup>();
        public DbSet<EventWindowRosterSnapshot> EventWindowRosterSnapshots => Set<EventWindowRosterSnapshot>();
        public DbSet<Tod> Tods => Set<Tod>();
        public DbSet<TodLootDetail> TodLootDetails => Set<TodLootDetail>();
        public DbSet<PartySetup> PartySetups => Set<PartySetup>();
        public DbSet<PartySetupAlliance> PartySetupAlliances => Set<PartySetupAlliance>();
        public DbSet<PartySetupParty> PartySetupParties => Set<PartySetupParty>();
        public DbSet<PartySetupSlot> PartySetupSlots => Set<PartySetupSlot>();
        public DbSet<Notification> Notifications => Set<Notification>();
        public DbSet<Rule> Rules => Set<Rule>();
        public DbSet<Announcement> Announcements => Set<Announcement>();
        public DbSet<Item> Items => Set<Item>();
        public DbSet<RevenueEntry> RevenueEntries => Set<RevenueEntry>();
        public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
        public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
        public DbSet<JournalEntryLine> JournalEntryLines => Set<JournalEntryLine>();
        public DbSet<LedgerPeriod> LedgerPeriods => Set<LedgerPeriod>();
        public DbSet<LinkshellRole> LinkshellRoles => Set<LinkshellRole>();
        public DbSet<JobRating> JobRatings => Set<JobRating>();
        public DbSet<EventComment> EventComments => Set<EventComment>();
        public DbSet<AddonApiToken> AddonApiTokens => Set<AddonApiToken>();
        public DbSet<AddonPairingCode> AddonPairingCodes => Set<AddonPairingCode>();
        public DbSet<EventAttendanceWindow> EventAttendanceWindows => Set<EventAttendanceWindow>();
        public DbSet<AppUserEventWindow> AppUserEventWindows => Set<AppUserEventWindow>();
        public DbSet<AttendanceSnapshot> AttendanceSnapshots => Set<AttendanceSnapshot>();
        public DbSet<AttendanceSnapshotEntry> AttendanceSnapshotEntries => Set<AttendanceSnapshotEntry>();
        public DbSet<WindowEvent> WindowEvents => Set<WindowEvent>();
        public DbSet<WindowEventMemberDkp> WindowEventMemberDkps => Set<WindowEventMemberDkp>();
        public DbSet<PendingTodSubmission> PendingTodSubmissions => Set<PendingTodSubmission>();
        public DbSet<PendingTodLootSubmission> PendingTodLootSubmissions => Set<PendingTodLootSubmission>();
        public DbSet<PendingAttendanceWindowSubmission> PendingAttendanceWindowSubmissions => Set<PendingAttendanceWindowSubmission>();
        public DbSet<PendingAttendanceWindowMemberSubmission> PendingAttendanceWindowMemberSubmissions => Set<PendingAttendanceWindowMemberSubmission>();
        public DbSet<PendingAttendanceSnapshotSubmission> PendingAttendanceSnapshotSubmissions => Set<PendingAttendanceSnapshotSubmission>();
        public DbSet<PendingAttendanceSnapshotEntry> PendingAttendanceSnapshotEntries => Set<PendingAttendanceSnapshotEntry>();
        public DbSet<ClaimShieldCapture> ClaimShieldCaptures => Set<ClaimShieldCapture>();
        public DbSet<ClaimShieldCaptureMember> ClaimShieldCaptureMembers => Set<ClaimShieldCaptureMember>();
        public DbSet<LinkshellDiscordWebhook> LinkshellDiscordWebhooks => Set<LinkshellDiscordWebhook>();
        public DbSet<LinkshellDiscordChannel> LinkshellDiscordChannels => Set<LinkshellDiscordChannel>();
        public DbSet<LinkshellChannelRoute> LinkshellChannelRoutes => Set<LinkshellChannelRoute>();
        public DbSet<HnmRecurringBoard> HnmRecurringBoards => Set<HnmRecurringBoard>();
        public DbSet<ChartPopItem> ChartPopItems => Set<ChartPopItem>();
        public DbSet<ChartPopItemCredit> ChartPopItemCredits => Set<ChartPopItemCredit>();
        public DbSet<ChartBossProgress> ChartBossProgresses => Set<ChartBossProgress>();
        public DbSet<AppSetting> AppSettings => Set<AppSetting>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<DiscordActivityUser>(entity =>
            {
                entity.ToTable("DiscordActivityUsers");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.DiscordUserId).HasMaxLength(32).IsRequired();
                entity.Property(user => user.Username).HasMaxLength(32).IsRequired();
                entity.Property(user => user.Discriminator).HasMaxLength(10).IsRequired();
                entity.Property(user => user.GlobalName).HasMaxLength(64);
                entity.Property(user => user.Avatar).HasMaxLength(128);
                entity.Property(user => user.IdentityUserId).HasMaxLength(450);
                entity.HasIndex(user => user.DiscordUserId).IsUnique();
                entity.HasOne(user => user.IdentityUser)
                    .WithMany()
                    .HasForeignKey(user => user.IdentityUserId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<LinkshellBanner>(entity =>
            {
                // 1:1 with Linkshell on a shared primary key (LinkshellId is both
                // PK and FK). Cascade-delete with the linkshell. Kept in its own
                // table so the image bytes stay off the hot dashboard/overview reads.
                entity.HasKey(banner => banner.LinkshellId);
                entity.HasOne(banner => banner.Linkshell)
                    .WithOne(linkshell => linkshell.Banner)
                    .HasForeignKey<LinkshellBanner>(banner => banner.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<Event>(entity =>
            {
                // Event.PartySetupId nullable FK. SetNull on PartySetup delete
                // keeps the Event intact (with a null FK) when an officer
                // removes the linked setup.
                entity.HasOne(item => item.PartySetup)
                    .WithMany()
                    .HasForeignKey(item => item.PartySetupId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => item.PartySetupId);
            });

            builder.Entity<HnmRecurringBoard>(entity =>
            {
                // One recurring-board template per monster per linkshell.
                entity.HasIndex(board => new { board.LinkshellId, board.MonsterName }).IsUnique();
                entity.HasOne(board => board.Linkshell)
                    .WithMany()
                    .HasForeignKey(board => board.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                // Keep the template if its referenced party setup is deleted.
                entity.HasOne(board => board.PartySetup)
                    .WithMany()
                    .HasForeignKey(board => board.PartySetupId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<AppSetting>(entity =>
            {
                entity.ToTable("AppSettings");
                entity.HasKey(setting => setting.Key);
                entity.Property(setting => setting.Key).HasMaxLength(128);
                entity.Property(setting => setting.Value).HasMaxLength(512);
            });

            builder.Entity<Invite>(entity =>
            {
                // Optional now: a Discord-roster invite has no AppUserId until the
                // invited Discord account first signs into LSM.
                entity.Property(invite => invite.AppUserId).HasMaxLength(450);
                entity.Property(invite => invite.Status).HasMaxLength(32).IsRequired();
                entity.HasOne(invite => invite.AppUser)
                    .WithMany()
                    .HasForeignKey(invite => invite.AppUserId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(invite => invite.Linkshell)
                    .WithMany()
                    .HasForeignKey(invite => invite.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<Auction>(entity =>
            {
                entity.Property(item => item.AuctionTitle).HasMaxLength(256);
                entity.Property(item => item.CreatedByUserId).HasMaxLength(450);
                entity.Property(item => item.CreatedBy).HasMaxLength(256);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                // NoAction, not Restrict: this table and DkpPools BOTH cascade from Linkshell,
                // and Restrict is checked immediately, so deleting a linkshell that ever ran an
                // auction would raise an FK violation depending on cascade order. NoAction is
                // checked at end-of-statement, by which point the sibling cascade has already
                // removed these rows. It still blocks deleting a pool that an auction points at,
                // which is what we actually want (pool deletion goes through DkpPoolEditor).
                entity.HasOne(item => item.DkpPool)
                    .WithMany()
                    .HasForeignKey(item => item.DkpPoolId)
                    .OnDelete(DeleteBehavior.NoAction);
            });

            builder.Entity<AuctionHistory>(entity =>
            {
                entity.Property(item => item.AuctionTitle).HasMaxLength(256);
                entity.Property(item => item.CreatedByUserId).HasMaxLength(450);
                entity.Property(item => item.CreatedBy).HasMaxLength(256);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(item => item.DkpPool)
                    .WithMany()
                    .HasForeignKey(item => item.DkpPoolId)
                    .OnDelete(DeleteBehavior.NoAction);
            });

            builder.Entity<AuctionItem>(entity =>
            {
                entity.Property(item => item.ItemName).HasMaxLength(256);
                entity.Property(item => item.ItemType).HasMaxLength(128);
                entity.Property(item => item.CurrentHighestBidder).HasMaxLength(256);
                entity.Property(item => item.CurrentHighestBidderAppUserId).HasMaxLength(450);
                entity.Property(item => item.Status).HasMaxLength(32);
                entity.Property(item => item.Notes).HasMaxLength(1024);
                entity.HasOne(item => item.Auction)
                    .WithMany(item => item.AuctionItems)
                    .HasForeignKey(item => item.AuctionId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(item => item.AuctionHistory)
                    .WithMany(item => item.AuctionItems)
                    .HasForeignKey(item => item.AuctionHistoryId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<Bid>(entity =>
            {
                entity.Property(item => item.AppUserId).HasMaxLength(450);
                entity.Property(item => item.CharacterName).HasMaxLength(256).IsRequired();
                entity.HasOne(item => item.AuctionItem)
                    .WithMany(item => item.Bids)
                    .HasForeignKey(item => item.AuctionItemId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(item => item.AppUser)
                    .WithMany()
                    .HasForeignKey(item => item.AppUserId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => new { item.AuctionItemId, item.CreatedAt });
            });

            builder.Entity<AppUserEventStatusLedger>(entity =>
            {
                entity.ToTable("AppUserEventStatusLedgers");
                entity.Property(item => item.ActionType).HasMaxLength(32).IsRequired();
                entity.Property(item => item.VerifiedBy).HasMaxLength(256);
                entity.Property(item => item.Source).HasMaxLength(32);
                entity.HasOne(item => item.AppUserEvent)
                    .WithMany(item => item.StatusLedgerEntries)
                    .HasForeignKey(item => item.AppUserEventId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(item => item.Event)
                    .WithMany(item => item.StatusLedgerEntries)
                    .HasForeignKey(item => item.EventId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(item => item.AppUser)
                    .WithMany()
                    .HasForeignKey(item => item.AppUserId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasOne(item => item.EventAttendanceWindow)
                    .WithMany()
                    .HasForeignKey(item => item.EventAttendanceWindowId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => new { item.AppUserEventId, item.OccurredAt });
            });

            builder.Entity<AppUserEventHistory>(entity =>
            {
                // One attendance row per member per closed event — the app's invariant
                // (every signup path adopts the existing participation rather than making a
                // second). Enforced in the DB too so two officers adding the same member to
                // a past event at the same instant can't create a duplicate: the second
                // insert violates this and AddParticipantAsync catches it. Filtered to
                // non-null AppUserId (outside/legacy rows aren't account-keyed).
                entity.HasIndex(item => new { item.EventHistoryId, item.AppUserId })
                    .IsUnique()
                    .HasFilter("\"AppUserId\" IS NOT NULL");
            });

            builder.Entity<DkpLedgerEntry>(entity =>
            {
                entity.ToTable("DkpLedgerEntries");
                entity.Property(item => item.EntryType).HasMaxLength(32).IsRequired();
                entity.Property(item => item.CharacterName).HasMaxLength(256);
                entity.Property(item => item.EventName).HasMaxLength(256);
                entity.Property(item => item.EventType).HasMaxLength(256);
                entity.Property(item => item.EventLocation).HasMaxLength(256);
                entity.Property(item => item.ItemName).HasMaxLength(256);
                entity.Property(item => item.Details).HasMaxLength(1024);
                entity.Property(item => item.EditReason).HasMaxLength(512);
                entity.HasOne(item => item.AppUser)
                    .WithMany()
                    .HasForeignKey(item => item.AppUserId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(item => item.EventHistory)
                    .WithMany()
                    .HasForeignKey(item => item.EventHistoryId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasOne(item => item.SourceAuctionHistory)
                    .WithMany()
                    .HasForeignKey(item => item.SourceAuctionHistoryId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => new { item.LinkshellId, item.AppUserId, item.OccurredAt, item.Sequence });
                // Index the loot-source FKs so the Loot History view can
                // quickly find the edit-pair ledger entries for a given
                // loot row without scanning the full ledger.
                entity.HasIndex(item => item.SourceTodLootDetailId);
                entity.HasIndex(item => item.SourceEventLootDetailId);
                entity.HasIndex(item => item.SourceWindowEventId);
                entity.HasIndex(item => item.SourceAuctionHistoryId);
                entity.HasIndex(item => item.AttInputRowNumber);
                entity.HasIndex(item => item.AuditRelatedLedgerEntryId);
                // DkpPoolId is deliberately NOT a foreign key — see DkpLedgerEntry.DkpPoolId.
                // The index carries the pool-balance derivation and the remap re-stamp.
                entity.HasIndex(item => new { item.LinkshellId, item.DkpPoolId });
            });

            builder.Entity<DkpPool>(entity =>
            {
                entity.ToTable("DkpPools");
                entity.Property(item => item.Name).HasMaxLength(64).IsRequired();
                entity.Property(item => item.Accent).HasMaxLength(16);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => item.LinkshellId);
                entity.HasIndex(item => new { item.LinkshellId, item.Name }).IsUnique();
                // Exactly one default pool per linkshell.
                entity.HasIndex(item => item.LinkshellId)
                    .IsUnique()
                    .HasDatabaseName("IX_DkpPools_LinkshellId_Default")
                    .HasFilter("\"IsDefault\" = TRUE");
            });

            builder.Entity<DkpPoolEventType>(entity =>
            {
                entity.ToTable("DkpPoolEventTypes");
                entity.Property(item => item.EventType).HasMaxLength(256).IsRequired();
                entity.Property(item => item.NormalizedEventType).HasMaxLength(256).IsRequired();
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(item => item.DkpPool)
                    .WithMany(pool => pool.EventTypes)
                    .HasForeignKey(item => item.DkpPoolId)
                    .OnDelete(DeleteBehavior.Cascade);
                // THE PARTITION GUARANTEE: an event type can only ever live in one pool.
                entity.HasIndex(item => new { item.LinkshellId, item.NormalizedEventType }).IsUnique();
                entity.HasIndex(item => item.DkpPoolId);
            });

            builder.Entity<Tod>(entity =>
            {
                entity.ToTable("Tods");
                entity.Property(item => item.MonsterName).HasMaxLength(256);
                entity.Property(item => item.Cooldown).HasMaxLength(32);
                entity.Property(item => item.Interval).HasMaxLength(32);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasMany(item => item.TodLootDetails)
                    .WithOne(item => item.Tod)
                    .HasForeignKey(item => item.TodId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.LinkshellId, item.Time });
                entity.HasIndex(item => new { item.LinkshellId, item.MonsterName });
            });

            builder.Entity<TodLootDetail>(entity =>
            {
                entity.ToTable("TodLootDetails");
                entity.Property(item => item.ItemName).HasMaxLength(256);
                entity.Property(item => item.ItemWinner).HasMaxLength(256);
                entity.Property(item => item.EditedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.EditedByCharacterName).HasMaxLength(256);
                entity.Property(item => item.LastEditReason).HasMaxLength(512);
                entity.HasOne(item => item.DkpPool)
                    .WithMany()
                    .HasForeignKey(item => item.DkpPoolId)
                    .OnDelete(DeleteBehavior.NoAction);
            });

            builder.Entity<PartySetup>(entity =>
            {
                entity.ToTable("PartySetups");
                entity.Property(item => item.Name).HasMaxLength(128).IsRequired();
                entity.Property(item => item.AssignedMonsterName).HasMaxLength(256);
                entity.Property(item => item.Notes).HasMaxLength(1024);
                entity.Property(item => item.CreatedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.CreatedByCharacterName).HasMaxLength(256);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasMany(item => item.Alliances)
                    .WithOne(item => item.PartySetup)
                    .HasForeignKey(item => item.PartySetupId)
                    .OnDelete(DeleteBehavior.Cascade);
                // Per-event snapshot ownership: deleting the event removes its private
                // copy. (Postgres permits this second Event<->PartySetup FK path; the
                // reverse Event.PartySetupId FK is SetNull, so there is no delete cycle.)
                entity.HasOne(item => item.OwnerEvent)
                    .WithMany()
                    .HasForeignKey(item => item.OwnerEventId)
                    .OnDelete(DeleteBehavior.Cascade);
                // One snapshot per event — also the concurrency guard against a racing
                // double-clone on first edit (the loser trips this unique index).
                entity.HasIndex(item => item.OwnerEventId)
                    .IsUnique()
                    .HasFilter("\"OwnerEventId\" IS NOT NULL");
                entity.HasIndex(item => new { item.LinkshellId, item.AssignedMonsterName });
                entity.HasIndex(item => new { item.LinkshellId, item.Name });
            });

            builder.Entity<PartySetupAlliance>(entity =>
            {
                entity.ToTable("PartySetupAlliances");
                entity.Property(item => item.Name).HasMaxLength(64);
                entity.HasMany(item => item.Parties)
                    .WithOne(item => item.Alliance)
                    .HasForeignKey(item => item.PartySetupAllianceId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.PartySetupId, item.SortOrder });
            });

            builder.Entity<PartySetupParty>(entity =>
            {
                entity.ToTable("PartySetupParties");
                entity.Property(item => item.Name).HasMaxLength(64);
                entity.HasMany(item => item.Slots)
                    .WithOne(item => item.Party)
                    .HasForeignKey(item => item.PartySetupPartyId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.PartySetupAllianceId, item.SortOrder });
            });

            builder.Entity<PartySetupSlot>(entity =>
            {
                entity.ToTable("PartySetupSlots");
                entity.Property(item => item.RequirementType).HasMaxLength(16).IsRequired();
                entity.Property(item => item.Role).HasMaxLength(16);
                entity.Property(item => item.MainJob).HasMaxLength(8);
                entity.Property(item => item.SubJob).HasMaxLength(8);
                entity.Property(item => item.Label).HasMaxLength(64);
                entity.HasIndex(item => new { item.PartySetupPartyId, item.SortOrder });
            });

            builder.Entity<EventLootDetail>(entity =>
            {
                entity.ToTable("EventLootDetails");
                entity.Property(item => item.ItemName).HasMaxLength(256);
                entity.Property(item => item.ItemWinner).HasMaxLength(256);
                entity.Property(item => item.EditedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.EditedByCharacterName).HasMaxLength(256);
                entity.Property(item => item.LastEditReason).HasMaxLength(512);
                // SetNull (not Cascade) so the loot row survives its parent
                // Event being deleted at event-close time. The close-out
                // flow stamps EventHistoryId before deleting the Event so
                // the row stays discoverable via the new FK below.
                entity.HasOne(item => item.Event)
                    .WithMany(evt => evt.EventLootDetails)
                    .HasForeignKey(item => item.EventId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasOne(item => item.EventHistory)
                    .WithMany()
                    .HasForeignKey(item => item.EventHistoryId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => item.EventHistoryId);
            });

            builder.Entity<EventComment>(entity =>
            {
                entity.ToTable("EventComments");
                entity.Property(item => item.AppUserId).HasMaxLength(450);
                entity.Property(item => item.CharacterName).HasMaxLength(256);
                entity.Property(item => item.Body).HasMaxLength(2000);
                entity.Property(item => item.DiscordMessageId).HasMaxLength(20);
                entity.HasOne(item => item.EventHistory)
                    .WithMany()
                    .HasForeignKey(item => item.EventHistoryId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.EventHistoryId, item.CreatedAt });
            });

            builder.Entity<JobRating>(entity =>
            {
                entity.ToTable("JobRatings");
                entity.Property(item => item.TargetAppUserId).HasMaxLength(450);
                entity.Property(item => item.RaterAppUserId).HasMaxLength(450);
                // One rating per rater per target's job + character within a
                // linkshell (upsert). CharacterSlot distinguishes main/alt rows.
                entity.HasIndex(item => new { item.LinkshellId, item.TargetAppUserId, item.RaterAppUserId, item.CharacterSlot, item.JobIndex }).IsUnique();
                // Aggregate a target's ratings across all raters.
                entity.HasIndex(item => new { item.LinkshellId, item.TargetAppUserId });
            });

            builder.Entity<EventPartySlotSignup>(entity =>
            {
                entity.ToTable("EventPartySlotSignups");
                entity.HasOne(item => item.Event)
                    .WithMany()
                    .HasForeignKey(item => item.EventId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(item => item.PartySetupSlot)
                    .WithMany()
                    .HasForeignKey(item => item.PartySetupSlotId)
                    .OnDelete(DeleteBehavior.Cascade);
                // One signup per slot per event.
                entity.HasIndex(item => new { item.EventId, item.PartySetupSlotId }).IsUnique();
                // Find a member's signup within an event (one-slot-per-event + leave).
                entity.HasIndex(item => new { item.EventId, item.AppUserId });
                // Same, for "Outside Party Signup" rows keyed by Discord user id.
                entity.HasIndex(item => new { item.EventId, item.DiscordUserId });
            });

            builder.Entity<EventWindowRosterSnapshot>(entity =>
            {
                entity.ToTable("EventWindowRosterSnapshots");
                entity.Property(item => item.AllianceName).HasMaxLength(64);
                entity.Property(item => item.PartyName).HasMaxLength(64);
                entity.Property(item => item.SlotLabel).HasMaxLength(64);
                entity.Property(item => item.AppUserId).HasMaxLength(450);
                entity.Property(item => item.CharacterName).HasMaxLength(256);
                entity.Property(item => item.Role).HasMaxLength(16);
                entity.Property(item => item.MainJob).HasMaxLength(8);
                entity.Property(item => item.SubJob).HasMaxLength(8);
                // Cascade is right here (unlike AppUserEventWindow's SetNull): a snapshot must
                // outlive the SIGNUPS it was taken from, but it's meaningless once the camp itself
                // is gone. There is deliberately NO FK to the party setup tree — see the model.
                entity.HasOne(item => item.Event)
                    .WithMany()
                    .HasForeignKey(item => item.EventId)
                    .OnDelete(DeleteBehavior.Cascade);
                // The only read path: "the roster for event E, window N", newest window first.
                entity.HasIndex(item => new { item.EventId, item.WindowNumber });
            });

            builder.Entity<Rule>(entity =>
            {
                entity.ToTable("Rules");
                entity.Property(item => item.LinkshellName).HasMaxLength(256);
                entity.Property(item => item.RuleTitle).HasMaxLength(256).IsRequired();
                entity.Property(item => item.RuleDetails).HasMaxLength(4000).IsRequired();
                entity.Property(item => item.CreatedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.CreatedByCharacterName).HasMaxLength(256);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.LinkshellId, item.CreatedAt });
            });

            builder.Entity<Announcement>(entity =>
            {
                entity.ToTable("Announcements");
                entity.Property(item => item.LinkshellName).HasMaxLength(256);
                entity.Property(item => item.AnnouncementTitle).HasMaxLength(256).IsRequired();
                entity.Property(item => item.AnnouncementDetails).HasMaxLength(4000).IsRequired();
                entity.Property(item => item.CreatedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.CreatedByCharacterName).HasMaxLength(256);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.LinkshellId, item.CreatedAt });
            });

            builder.Entity<Item>(entity =>
            {
                entity.ToTable("Items");
                entity.Property(item => item.LinkshellName).HasMaxLength(256);
                entity.Property(item => item.ItemName).HasMaxLength(256).IsRequired();
                entity.Property(item => item.ItemType).HasMaxLength(128);
                entity.Property(item => item.Notes).HasMaxLength(1024);
                entity.Property(item => item.CreatedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.CreatedByCharacterName).HasMaxLength(256);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.LinkshellId, item.ItemName });
            });

            builder.Entity<RevenueEntry>(entity =>
            {
                entity.ToTable("RevenueEntries");
                entity.Property(item => item.LinkshellName).HasMaxLength(256);
                entity.Property(item => item.EntryType).HasMaxLength(16).IsRequired();
                entity.Property(item => item.Category).HasMaxLength(128);
                entity.Property(item => item.Details).HasMaxLength(1024);
                entity.Property(item => item.CreatedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.CreatedByCharacterName).HasMaxLength(256);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.LinkshellId, item.OccurredAt });
            });

            // --- Gil treasury: categories, transactions, their two halves, and the lock. ---
            // RevenueEntry above is the shape these replace. It stays in place, readable and
            // unmodified, for a full release after the conversion so a binary rollback is a
            // non-event; RemoveRevenueEntries is a later migration.

            builder.Entity<LedgerAccount>(entity =>
            {
                entity.ToTable("LedgerAccounts");
                entity.Property(item => item.Name).HasMaxLength(96).IsRequired();
                entity.Property(item => item.Description).HasMaxLength(512);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.LinkshellId, item.AccountNumber }).IsUnique();
                entity.HasIndex(item => new { item.LinkshellId, item.SortOrder });
                // Exactly one gil-on-hand category per linkshell — the same partial-unique trick
                // as IX_DkpPools_LinkshellId_Default. Its balance IS the treasury balance, so two
                // of them would make "how much gil do we have" ambiguous again.
                entity.HasIndex(item => item.LinkshellId)
                    .HasDatabaseName("IX_LedgerAccounts_LinkshellId_Cash")
                    .IsUnique()
                    .HasFilter("\"IsCash\" = TRUE");
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_LedgerAccounts_AccountNumber_Range",
                    $"\"AccountNumber\" BETWEEN {LedgerAccountClasses.MinAccountNumber} AND {LedgerAccountClasses.MaxAccountNumber}"));
            });

            builder.Entity<JournalEntry>(entity =>
            {
                entity.ToTable("JournalEntries");
                entity.Property(item => item.LinkshellName).HasMaxLength(256);
                entity.Property(item => item.EntryNumber).HasMaxLength(24).IsRequired();
                entity.Property(item => item.Status).HasMaxLength(16).IsRequired();
                entity.Property(item => item.Kind).HasMaxLength(16).IsRequired();
                entity.Property(item => item.Source).HasMaxLength(16).IsRequired();
                entity.Property(item => item.TransactionKind).HasMaxLength(48);
                entity.Property(item => item.Memo).HasMaxLength(1024);
                entity.Property(item => item.CorrectionReason).HasMaxLength(512);
                entity.Property(item => item.CreatedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.CreatedByCharacterName).HasMaxLength(256);
                entity.Property(item => item.ConfirmedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.ConfirmedByCharacterName).HasMaxLength(256);
                entity.Property(item => item.LegacyEntryType).HasMaxLength(32);
                entity.Property(item => item.LegacyCategory).HasMaxLength(128);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.LinkshellId, item.TransactionDate });
                entity.HasIndex(item => new { item.LinkshellId, item.Sequence }).IsUnique();
                entity.HasIndex(item => new { item.LinkshellId, item.EntryNumber }).IsUnique();
                // Powers the indexed EXISTS behind "has this been reversed?", which is how the
                // reversal state is read without ever updating a confirmed row.
                entity.HasIndex(item => item.ReversesJournalEntryId);
                // Makes the one-time conversion replayable: if it aborts halfway, re-run it rather
                // than restoring a snapshot.
                entity.HasIndex(item => item.LegacyRevenueEntryId)
                    .HasDatabaseName("IX_JournalEntries_LegacyRevenueEntryId")
                    .IsUnique()
                    .HasFilter("\"LegacyRevenueEntryId\" IS NOT NULL");
                // An entry that says an earlier one was wrong has to say why.
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_JournalEntries_ReasonRequiredForFixes",
                    "\"Kind\" NOT IN ('Reversal', 'Correction')"
                    + " OR (\"CorrectionReason\" IS NOT NULL AND length(btrim(\"CorrectionReason\")) > 0)"));
            });

            builder.Entity<JournalEntryLine>(entity =>
            {
                entity.ToTable("JournalEntryLines");
                entity.Property(item => item.AccountName).HasMaxLength(96).IsRequired();
                entity.Property(item => item.LineMemo).HasMaxLength(256);
                entity.Property(item => item.CounterpartyAppUserId).HasMaxLength(450);
                entity.Property(item => item.CounterpartyCharacterName).HasMaxLength(256);
                entity.HasOne(item => item.JournalEntry)
                    .WithMany(header => header.Lines)
                    .HasForeignKey(item => item.JournalEntryId)
                    .OnDelete(DeleteBehavior.Cascade);
                // Restrict, not Cascade: a category with history can be retired but never deleted,
                // because deleting it would silently rewrite what those entries meant.
                entity.HasOne(item => item.LedgerAccount)
                    .WithMany()
                    .HasForeignKey(item => item.LedgerAccountId)
                    .OnDelete(DeleteBehavior.Restrict);
                // LinkshellId is deliberately a plain indexed int with no relationship: a second
                // cascade path from Linkshells into this table is something Postgres will refuse.
                entity.HasIndex(item => new { item.LinkshellId, item.LedgerAccountId, item.TransactionDate });
                entity.HasIndex(item => new { item.LinkshellId, item.TransactionDate });
                entity.HasIndex(item => item.JournalEntryId);
                // Deliberately NO "Amount <> 0" constraint: a sale genuinely recorded at 0 gil
                // exists in production (both mark-sold paths clamp a negative price to 0), and
                // dropping those rows would lose the fact that a sale happened at all. They convert
                // to an entry whose two halves are both zero, which still sums to zero.
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_JournalEntryLines_LineNumber_Positive", "\"LineNumber\" >= 1"));
            });

            builder.Entity<LedgerPeriod>(entity =>
            {
                entity.ToTable("LedgerPeriods");
                entity.Property(item => item.LockedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.LockedByCharacterName).HasMaxLength(256);
                entity.Property(item => item.UnlockedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.UnlockedByCharacterName).HasMaxLength(256);
                entity.Property(item => item.UnlockReason).HasMaxLength(512);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                // At most one row per linkshell; no row at all means nothing is locked.
                entity.HasIndex(item => item.LinkshellId).IsUnique();
            });

            builder.Entity<LinkshellRole>(entity =>
            {
                entity.ToTable("LinkshellRoles");
                entity.Property(item => item.Name).HasMaxLength(64).IsRequired();
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.LinkshellId, item.Name }).IsUnique();
            });

            // --- Charts: the pop items a linkshell is sitting on, and who farmed each one. ---
            // One set of tables for every board (Sky, Sea, later Dynamis / Limbus): same rows, same
            // per-item credit, same derived ledger. Board + Boss say which card a row appears on.
            builder.Entity<ChartPopItem>(entity =>
            {
                entity.ToTable("ChartPopItems");
                entity.Property(item => item.Board).HasMaxLength(16).IsRequired();
                entity.Property(item => item.Boss).HasMaxLength(64).IsRequired();
                entity.Property(item => item.ItemName).HasMaxLength(128).IsRequired();
                entity.Property(item => item.HeldByCharacterName).HasMaxLength(256);
                entity.Property(item => item.Notes).HasMaxLength(512);
                entity.Property(item => item.CreatedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.CreatedByCharacterName).HasMaxLength(256);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                // Covers the one read this feature has: every row for a linkshell's board, grouped
                // by boss, in the order the officers put them in.
                entity.HasIndex(item => new { item.LinkshellId, item.Board, item.Boss, item.SortOrder });
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_ChartPopItems_Quantity_NonNegative", "\"Quantity\" >= 0"));
            });

            builder.Entity<ChartPopItemCredit>(entity =>
            {
                entity.ToTable("ChartPopItemCredits");
                entity.Property(item => item.CharacterName).HasMaxLength(256).IsRequired();
                entity.Property(item => item.Detail).HasMaxLength(256);
                entity.Property(item => item.CreditedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.CreditedByCharacterName).HasMaxLength(256);
                entity.HasOne(item => item.ChartPopItem)
                    .WithMany(row => row.Credits)
                    .HasForeignKey(item => item.ChartPopItemId)
                    .OnDelete(DeleteBehavior.Cascade);
                // LinkshellId is deliberately a plain indexed int with no relationship: a second
                // cascade path from Linkshells into this table is something Postgres will refuse.
                // Same reason as JournalEntryLine. Rows still die with the linkshell, via
                // ChartPopItems.
                entity.HasIndex(item => new { item.LinkshellId, item.MembershipId });
                entity.HasIndex(item => item.ChartPopItemId);
                // Deliberately NO unique index on (item, person): credits are written set-wise — the
                // whole list for a row is deleted and re-inserted in one SaveChanges — so a duplicate
                // cannot survive a write, and a unique index would only add a normalized-name column
                // to maintain.
            });

            builder.Entity<ChartBossProgress>(entity =>
            {
                entity.ToTable("ChartBossProgresses");
                entity.Property(item => item.Board).HasMaxLength(16).IsRequired();
                entity.Property(item => item.Boss).HasMaxLength(64).IsRequired();
                entity.Property(item => item.ProgressNote).HasMaxLength(128);
                entity.Property(item => item.UpdatedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.UpdatedByCharacterName).HasMaxLength(256);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                // At most one note per boss per linkshell; no row at all means no note, which is the
                // normal state.
                entity.HasIndex(item => new { item.LinkshellId, item.Board, item.Boss }).IsUnique();
            });

            builder.Entity<AddonApiToken>(entity =>
            {
                entity.ToTable("AddonApiTokens");
                entity.Property(item => item.TokenHash).HasMaxLength(128).IsRequired();
                entity.Property(item => item.TokenPrefix).HasMaxLength(16).IsRequired();
                entity.Property(item => item.Label).HasMaxLength(128);
                entity.Property(item => item.IssuedToAppUserId).HasMaxLength(450);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(item => item.IssuedToAppUser)
                    .WithMany()
                    .HasForeignKey(item => item.IssuedToAppUserId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => item.TokenHash).IsUnique();
                entity.HasIndex(item => item.LinkshellId);
            });

            builder.Entity<AddonPairingCode>(entity =>
            {
                entity.ToTable("AddonPairingCodes");
                entity.Property(item => item.Code).HasMaxLength(16).IsRequired();
                entity.Property(item => item.Label).HasMaxLength(128);
                entity.Property(item => item.IssuedToAppUserId).HasMaxLength(450);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(item => item.IssuedToAppUser)
                    .WithMany()
                    .HasForeignKey(item => item.IssuedToAppUserId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => item.Code).IsUnique();
                entity.HasIndex(item => item.ExpiresAt);
            });

            builder.Entity<EventAttendanceWindow>(entity =>
            {
                entity.ToTable("EventAttendanceWindows");
                entity.Property(item => item.Label).HasMaxLength(64);
                entity.Property(item => item.PostedBySource).HasMaxLength(64);
                entity.HasOne(item => item.Event)
                    .WithMany(evt => evt.AttendanceWindows)
                    .HasForeignKey(item => item.EventId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.EventId, item.SequenceNumber }).IsUnique();
            });

            builder.Entity<AppUserEventWindow>(entity =>
            {
                entity.ToTable("AppUserEventWindows");
                entity.Property(item => item.VerifiedBy).HasMaxLength(256);
                entity.Property(item => item.Zone).HasMaxLength(64);
                entity.Property(item => item.AppUserId).HasMaxLength(450);
                entity.Property(item => item.CharacterName).HasMaxLength(256);
                // SetNull, NOT Cascade. The wyrm camps clear their roster every window, and while
                // this cascaded it deleted that window's attendance record along with the
                // participation — see the comment on AppUserEventWindow.AppUserEventId. The
                // denormalized AppUserId / CharacterName are what keep an orphaned row meaningful.
                entity.HasOne(item => item.AppUserEvent)
                    .WithMany(aue => aue.AttendedWindows)
                    .HasForeignKey(item => item.AppUserEventId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => new { item.EventAttendanceWindowId, item.AppUserId });
                entity.HasOne(item => item.EventAttendanceWindow)
                    .WithMany(window => window.Attendees)
                    .HasForeignKey(item => item.EventAttendanceWindowId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.AppUserEventId, item.EventAttendanceWindowId }).IsUnique();
            });

            // AttendanceSnapshot -> Event (optional, SetNull on delete) so that
            // removing an event leaves its linked snapshots intact but unlinked
            // instead of failing the delete or cascading the snapshot away.
            builder.Entity<AttendanceSnapshot>(entity =>
            {
                entity.HasOne(item => item.LinkedEvent)
                    .WithMany()
                    .HasForeignKey(item => item.LinkedEventId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasOne(item => item.WindowEvent)
                    .WithMany(item => item.Snapshots)
                    .HasForeignKey(item => item.WindowEventId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasOne(item => item.DuplicateOfSnapshot)
                    .WithMany()
                    .HasForeignKey(item => item.DuplicateOfSnapshotId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => new { item.LinkshellId, item.CapturedAtUtc });
                entity.HasIndex(item => item.WindowEventId);
                entity.Property(item => item.SnapshotStatus)
                    .HasMaxLength(32)
                    .HasDefaultValue(AttendanceSnapshotStatuses.Active);
            });
            builder.Entity<WindowEvent>(entity =>
            {
                entity.ToTable("WindowEvents");
                entity.Property(item => item.Name).HasMaxLength(128);
                entity.Property(item => item.NormalizedName).HasMaxLength(128);
                entity.Property(item => item.Status)
                    .HasMaxLength(32)
                    .HasDefaultValue(WindowEventStatuses.Open);
                entity.Property(item => item.CreatedByCharacterName).HasMaxLength(256);
                entity.Property(item => item.Notes).HasMaxLength(1024);
                entity.Property(item => item.CampEventType).HasMaxLength(64);
                entity.Property(item => item.CampEventLocation).HasMaxLength(256);
                entity.HasIndex(item => new { item.LinkshellId, item.Status, item.NormalizedName });
                entity.HasIndex(item => new { item.LinkshellId, item.LastCapturedAtUtc });
                // SetNull, not Cascade: a camp Event is recycled for the next pop and can be
                // deleted outright by an officer. Neither must destroy a review row whose DKP
                // hasn't been posted yet — the Camp* columns keep it self-sufficient.
                entity.HasOne(item => item.SourceEvent)
                    .WithMany()
                    .HasForeignKey(item => item.SourceEventId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => item.SourceEventId);
            });
            builder.Entity<WindowEventMemberDkp>(entity =>
            {
                entity.ToTable("WindowEventMemberDkps");
                entity.Property(item => item.CharacterName).HasMaxLength(256).IsRequired();
                entity.HasOne(item => item.WindowEvent)
                    .WithMany(item => item.MemberDkpOverrides)
                    .HasForeignKey(item => item.WindowEventId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.WindowEventId, item.CharacterName }).IsUnique();
            });
            builder.Entity<PendingAttendanceSnapshotSubmission>(entity =>
            {
                entity.HasOne(item => item.LinkedEvent)
                    .WithMany()
                    .HasForeignKey(item => item.LinkedEventId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<ClaimShieldCapture>(entity =>
            {
                entity.ToTable("ClaimShieldCaptures");
                entity.Property(item => item.MonsterName).HasMaxLength(128).IsRequired();
                entity.Property(item => item.CapturedByCharacterName).HasMaxLength(256);
                entity.Property(item => item.CapturedMessage).HasMaxLength(512);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasMany(item => item.Members)
                    .WithOne(member => member.Capture)
                    .HasForeignKey(member => member.CaptureId)
                    .OnDelete(DeleteBehavior.Cascade);
                // SetNull, not Cascade: the capture is an audit record of a real
                // lottery. Deleting the camp it happened during must not erase it.
                entity.HasOne(item => item.Event)
                    .WithMany()
                    .HasForeignKey(item => item.EventId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => new { item.LinkshellId, item.CapturedAtUtc });
                // The Activity loads captures per event; without this that's a scan.
                entity.HasIndex(item => item.EventId);
            });

            builder.Entity<ClaimShieldCaptureMember>(entity =>
            {
                entity.ToTable("ClaimShieldCaptureMembers");
                entity.Property(item => item.CharacterName).HasMaxLength(256).IsRequired();
                entity.Property(item => item.AppUserId).HasMaxLength(450);
                entity.Property(item => item.ActionMessage).HasMaxLength(512);
                entity.HasIndex(item => item.CaptureId);
            });

            builder.Entity<LinkshellDiscordWebhook>(entity =>
            {
                entity.ToTable("LinkshellDiscordWebhooks");
                entity.Property(item => item.Name).HasMaxLength(64);
                entity.Property(item => item.Url).HasMaxLength(512).IsRequired();
                entity.Property(item => item.TodBoardMessageId).HasMaxLength(32);
                entity.HasOne(item => item.Linkshell)
                    .WithMany(linkshell => linkshell.DiscordWebhooks)
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => item.LinkshellId);
            });

            builder.Entity<LinkshellChannelRoute>(entity =>
            {
                entity.ToTable("LinkshellChannelRoutes");
                entity.Property(item => item.Name).HasMaxLength(64);
                entity.Property(item => item.ChannelId).HasMaxLength(20).IsRequired();
                entity.Property(item => item.ChannelName).HasMaxLength(128);
                entity.Property(item => item.EventTypeFilter).HasMaxLength(256);
                entity.Property(item => item.TodBoardMessageId).HasMaxLength(32);
                entity.HasOne(item => item.Linkshell)
                    .WithMany(linkshell => linkshell.ChannelRoutes)
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => item.LinkshellId);
            });


            builder.Entity<Auction>()
                .Property(item => item.DiscordChannelId).HasMaxLength(32);
            builder.Entity<AuctionHistory>()
                .Property(item => item.DiscordChannelId).HasMaxLength(32);
        }
    }
}
