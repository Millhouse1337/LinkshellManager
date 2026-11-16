using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace LinkshellManagerDiscordApp.Data
{
    // IdentityUserContext, not IdentityDbContext: the app has no Identity roles (permissions are
    // per-linkshell LinkshellRoles), so the model keeps only the user tables (AspNetUsers,
    // AspNetUserClaims, AspNetUserLogins, AspNetUserTokens). The three role tables were always
    // empty and were dropped by the DropIdentityRoleTables migration.
    public partial class ApplicationDbContext : IdentityUserContext<AppUser>
    {
        // Length of the Identity login/token key columns (AspNetUserLogins, AspNetUserTokens).
        // Identity reads it from IdentityOptions.Stores while building the model, and every
        // migration so far created those columns as varchar(128). Since EF Core 9, Migrate() refuses
        // to start when the model and the last migration disagree, so any context built without
        // this setting (a test, or a future AddIdentityCore without it) would stop the app booting.
        public const int IdentityKeyMaxLength = 128;

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
        // to the linkshell's Auctions channel. Also posted auctions whose start or
        // end just changed (Start, End early, a re-timed auction) → refresh the card,
        // whose Queued / Live / Ended line reads those times.
        // The publisher no-ops when no Auctions channel route is configured.
        private (List<Auction> created, List<AuctionHistory> closed, List<int> retimed) CollectAuctionChannelWork()
        {
            if (_auctionChannelQueue is null)
            {
                return (new List<Auction>(), new List<AuctionHistory>(), new List<int>());
            }
            var created = new List<Auction>();
            var retimed = new List<int>();
            foreach (var entry in ChangeTracker.Entries<Auction>())
            {
                if (entry.State == EntityState.Added && entry.Entity.LinkshellId > 0)
                {
                    created.Add(entry.Entity);
                }
                else if (entry.State == EntityState.Modified
                    && !string.IsNullOrEmpty(entry.Entity.DiscordMessageId)
                    && (entry.Property(a => a.StartedAt).IsModified
                        || entry.Property(a => a.StartTime).IsModified
                        || entry.Property(a => a.EndTime).IsModified))
                {
                    retimed.Add(entry.Entity.Id);
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
            return (created, closed, retimed);
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
        // commit. Both the EventEndService (EndEventCoreAsync) and Activity
        // (EventsLifecycle) close paths add an EventHistory, so this fires for either
        // without per-controller wiring.
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
                if (string.IsNullOrWhiteSpace(channelId))
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(entry.Entity.DiscordMessageId))
                {
                    boards.Add(new DiscordMessageRef(channelId!, entry.Entity.DiscordMessageId!));
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

        // Newly-awarded event loot rows (with a winner) → announce to the
        // linkshell's Loot channel. Captured as entity references pre-save
        // because the db-generated Id isn't known until after commit. Only
        // *Added* rows with a winner; edits (Modified) and close-out reattaches
        // are ignored. The publisher no-ops when no Loot channel is configured.
        // Only event loot is watched: nothing creates ToD loot rows any more.
        private List<EventLootDetail> CollectAddedLoot()
        {
            var evt = new List<EventLootDetail>();
            if (_lootChannelQueue is null)
            {
                return evt;
            }
            foreach (var entry in ChangeTracker.Entries<EventLootDetail>())
            {
                if (entry.State == EntityState.Added && !string.IsNullOrWhiteSpace(entry.Entity.ItemWinner))
                {
                    evt.Add(entry.Entity);
                }
            }
            return evt;
        }

        private void EnqueueLootPosts(IReadOnlyList<EventLootDetail> evt)
        {
            if (_lootChannelQueue is null)
            {
                return;
            }
            foreach (var detail in evt)
            {
                if (detail.Id > 0)
                {
                    if (_eventLootPostsHeld is not null)
                    {
                        // Not committed yet (see HoldEventLootPosts).
                        _eventLootPostsHeld.Add(detail.Id);
                        continue;
                    }
                    _lootChannelQueue.Enqueue(new DiscordLootJob(detail.Id));
                }
            }
        }

        // Event loot rows saved but not committed yet; null when nothing is being held.
        private List<int>? _eventLootPostsHeld;

        // A loot post on an event saves inside its own transaction (it holds the event row until
        // COMMIT -- EventRunState.HoldForLootAsync). The Discord loot post reads the new row back by
        // id, and queued right after the save -- before the commit -- the in-process worker could
        // look for it first, find nothing, and skip the post for good. While held, those posts wait
        // for ReleaseHeldEventLootPosts after COMMIT; DropHeldEventLootPosts forgets them when the
        // loot did not commit.
        internal void HoldEventLootPosts() => _eventLootPostsHeld ??= new List<int>();

        internal void ReleaseHeldEventLootPosts()
        {
            var held = _eventLootPostsHeld;
            _eventLootPostsHeld = null;
            if (held is null || _lootChannelQueue is null)
            {
                return;
            }
            foreach (var lootId in held)
            {
                _lootChannelQueue.Enqueue(new DiscordLootJob(lootId));
            }
        }

        internal void DropHeldEventLootPosts() => _eventLootPostsHeld = null;

        private void EnqueueAuctionChannelWork(
            IReadOnlyList<Auction> created, IReadOnlyList<AuctionHistory> closed, IReadOnlyList<int> retimed)
        {
            if (_auctionChannelQueue is null)
            {
                return;
            }
            foreach (var auctionId in retimed)
            {
                _auctionChannelQueue.Enqueue(new AuctionChannelJob(AuctionChannelJobKind.Update, auctionId));
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
                    if (_auctionClosesHeld is not null)
                    {
                        // Not committed yet (see HoldAuctionCloseAnnouncements).
                        _auctionClosesHeld.Add(history.Id);
                        continue;
                    }
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

        // Auction item ids of bids saved but not committed yet; null when nothing is being held.
        private HashSet<int>? _bidAnnouncementsHeld;

        // AuctionBidService saves a bid inside its own transaction (it holds row locks until COMMIT).
        // A save normally edits the Discord auction card and pushes the live refresh right after it
        // runs, which here is BEFORE the commit: the in-process Discord worker could read the item
        // first and post the old high bid, and the card would stay wrong until the next bid. While
        // held, those two are kept back; ReleaseHeldBidAnnouncementsAsync sends them after COMMIT,
        // and DropHeldBidAnnouncements forgets them when the bid did not commit.
        internal void HoldBidAnnouncements() => _bidAnnouncementsHeld ??= new HashSet<int>();

        internal async Task ReleaseHeldBidAnnouncementsAsync()
        {
            var held = _bidAnnouncementsHeld;
            _bidAnnouncementsHeld = null;
            if (held is null || held.Count == 0)
            {
                return;
            }

            await EnqueueAuctionBidUpdatesAsync(held);
            var work = new LiveChangeWork();
            work.BidAuctionItemIds.UnionWith(held);
            await NotifyLiveChangesAsync(work);
        }

        internal void DropHeldBidAnnouncements() => _bidAnnouncementsHeld = null;

        // Auction histories saved but not committed yet; null when nothing is being held.
        private List<int>? _auctionClosesHeld;

        // Closing an auction saves inside its own transaction (it holds the items' row locks until
        // COMMIT). The "auction closed" Discord post reads the new history row back by id, and
        // queued right after the save -- before the commit -- the in-process worker could look for
        // it first, find nothing, and skip the post for good. While held, those posts wait for
        // ReleaseHeldAuctionCloseAnnouncements after COMMIT; DropHeldAuctionCloseAnnouncements
        // forgets them when the close did not commit.
        internal void HoldAuctionCloseAnnouncements() => _auctionClosesHeld ??= new List<int>();

        internal void ReleaseHeldAuctionCloseAnnouncements()
        {
            var held = _auctionClosesHeld;
            _auctionClosesHeld = null;
            if (held is null || _auctionChannelQueue is null)
            {
                return;
            }
            foreach (var historyId in held)
            {
                _auctionChannelQueue.Enqueue(new AuctionChannelJob(AuctionChannelJobKind.Close, historyId));
            }
        }

        internal void DropHeldAuctionCloseAnnouncements() => _auctionClosesHeld = null;

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
            public readonly HashSet<int> LootEventIds = new();

            public bool HasWork =>
                Direct.Count > 0 || BidAuctionItemIds.Count > 0 || ParticipationEventIds.Count > 0
                || LootEventIds.Count > 0;
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

        // Keeps a membership's role id (AppUserLinkshell.LinkshellRoleId, the source) and its Rank
        // text (a copy of the role's name) in step. Done here, like the epoch above, so every writer
        // gets it -- the seven join paths, the rank change, a placeholder claim and Data Admin --
        // and the next one added cannot forget it. For each membership that is new, or whose Rank,
        // linkshell or role id changed:
        //   1. The id was set (a new row with an id, or the id changed to a value): Rank becomes
        //      that role's Name. An id that is not one of the member's own linkshell's roles is left
        //      alone, and the two-column foreign key refuses the save.
        //   2. Otherwise the role is looked up from Rank (MembershipRoleMatch): the role whose Name
        //      equals the trimmed Rank exactly; else Leader for "leader" in other capitals, and the
        //      Member role for anything else, with a warning in the log. Either way the id is set
        //      and Rank becomes the role's exact Name. Permissions already gave such a Rank that same
        //      role, so nobody's rights change.
        //   3. A linkshell missing any of its built-in roles (a brand new one has none) gets the
        //      missing ones added in this same save, the way LinkshellPermissions.EnsureDefaultRoles
        //      adds them. EF inserts them first and fills in the member's id.
        // A role rename needs nothing here: holders keep the same id, and the rename endpoint moves
        // their Rank text itself. Roles are found among the tracked rows first (one added or renamed
        // in this save), then in one query.
        private List<EntityEntry<AppUserLinkshell>> CollectMembershipsNeedingRoleStamp() =>
            ChangeTracker.Entries<AppUserLinkshell>()
                .Where(entry => entry.State == EntityState.Added
                    || (entry.State == EntityState.Modified
                        && (entry.Property(nameof(AppUserLinkshell.Rank)).IsModified
                            || entry.Property(nameof(AppUserLinkshell.LinkshellId)).IsModified
                            || entry.Property(nameof(AppUserLinkshell.LinkshellRoleId)).IsModified)))
                .ToList();

        private async Task StampMembershipRolesAsync(
            List<EntityEntry<AppUserLinkshell>> memberships, CancellationToken cancellationToken)
        {
            if (memberships.Count == 0)
            {
                return;
            }
            var (linkshellIds, roleIds) = RoleLookupKeys(memberships);
            var saved = linkshellIds.Count == 0 && roleIds.Count == 0
                ? new List<LinkshellRole>()
                : await SavedRolesQuery(linkshellIds, roleIds).ToListAsync(cancellationToken);
            ApplyRoleStamps(memberships, saved);
        }

        private void StampMembershipRoles(List<EntityEntry<AppUserLinkshell>> memberships)
        {
            if (memberships.Count == 0)
            {
                return;
            }
            var (linkshellIds, roleIds) = RoleLookupKeys(memberships);
            var saved = linkshellIds.Count == 0 && roleIds.Count == 0
                ? new List<LinkshellRole>()
                : SavedRolesQuery(linkshellIds, roleIds).ToList();
            ApplyRoleStamps(memberships, saved);
        }

        private IQueryable<LinkshellRole> SavedRolesQuery(List<int> linkshellIds, List<int> roleIds) =>
            LinkshellRoles.AsNoTracking()
                .Where(role => linkshellIds.Contains(role.LinkshellId) || roleIds.Contains(role.Id));

        // A membership whose save names its role by id (rule 1 above): a new row with an id, or the
        // id itself changed to a value.
        private static bool SetsRoleById(EntityEntry<AppUserLinkshell> entry)
        {
            var id = entry.Property(nameof(AppUserLinkshell.LinkshellRoleId));
            return id.CurrentValue is not null
                && (entry.State == EntityState.Added || id.IsModified);
        }

        private static int LinkshellKeyOf(EntityEntry entry) =>
            (int)entry.Property(nameof(AppUserLinkshell.LinkshellId)).CurrentValue!;

        // One query for the whole save: every role of the linkshells whose members are looked up by
        // Rank (to find the name, the Member role, or that a built-in role is missing), and the
        // roles named by id. Keys EF has not given a real value yet are skipped: nothing is saved
        // under them.
        private static (List<int> LinkshellIds, List<int> RoleIds) RoleLookupKeys(
            List<EntityEntry<AppUserLinkshell>> memberships)
        {
            var linkshellIds = new HashSet<int>();
            var roleIds = new HashSet<int>();
            foreach (var entry in memberships)
            {
                if (SetsRoleById(entry))
                {
                    var id = entry.Property(nameof(AppUserLinkshell.LinkshellRoleId));
                    if (!id.IsTemporary && (int)id.CurrentValue! > 0)
                    {
                        roleIds.Add((int)id.CurrentValue!);
                    }
                    continue;
                }
                var linkshell = entry.Property(nameof(AppUserLinkshell.LinkshellId));
                if (!linkshell.IsTemporary && LinkshellKeyOf(entry) > 0)
                {
                    linkshellIds.Add(LinkshellKeyOf(entry));
                }
            }
            return (linkshellIds.ToList(), roleIds.ToList());
        }

        // A role as this save sees it: its id and linkshell as EF holds them (temporary for a role
        // or linkshell added in this save), and whether it is a tracked row.
        private sealed record RoleInSave(int Id, int LinkshellKey, LinkshellRole Role, bool Tracked);

        private void ApplyRoleStamps(List<EntityEntry<AppUserLinkshell>> memberships, List<LinkshellRole> saved)
        {
            // The tracked rows win over what the query read: one may be renamed or deleted in this
            // save, or added and not in the database at all.
            var tracked = ChangeTracker.Entries<LinkshellRole>()
                .Where(entry => entry.State != EntityState.Detached)
                .ToList();
            var trackedIds = tracked
                .Where(entry => !entry.Property(nameof(LinkshellRole.Id)).IsTemporary)
                .Select(entry => (int)entry.Property(nameof(LinkshellRole.Id)).CurrentValue!)
                .ToHashSet();
            var roles = tracked
                .Where(entry => entry.State != EntityState.Deleted)
                .Select(entry => new RoleInSave(
                    (int)entry.Property(nameof(LinkshellRole.Id)).CurrentValue!,
                    LinkshellKeyOf(entry),
                    entry.Entity,
                    Tracked: true))
                .Concat(saved
                    .Where(role => !trackedIds.Contains(role.Id))
                    .Select(role => new RoleInSave(role.Id, role.LinkshellId, role, Tracked: false)))
                .ToList();

            foreach (var entry in memberships)
            {
                var membership = entry.Entity;
                var linkshellKey = LinkshellKeyOf(entry);

                if (SetsRoleById(entry))
                {
                    var roleId = (int)entry.Property(nameof(AppUserLinkshell.LinkshellRoleId)).CurrentValue!;
                    var held = roles.FirstOrDefault(role => role.Id == roleId && role.LinkshellKey == linkshellKey);
                    if (held is not null)
                    {
                        SetDerived(entry, nameof(AppUserLinkshell.Rank), held.Role.Name);
                    }
                    continue;
                }

                var linkshellRoles = roles.Where(role => role.LinkshellKey == linkshellKey).ToList();
                foreach (var added in AddMissingDefaultRoles(entry, linkshellKey, linkshellRoles))
                {
                    var addedEntry = Entry(added);
                    var role = new RoleInSave(
                        (int)addedEntry.Property(nameof(LinkshellRole.Id)).CurrentValue!,
                        linkshellKey,
                        added,
                        Tracked: true);
                    roles.Add(role);
                    linkshellRoles.Add(role);
                }

                var (picked, fellBack) = MembershipRoleMatch.Pick(
                    membership.Rank, linkshellRoles.Select(role => role.Role).ToList());
                if (fellBack)
                {
                    LogMembershipRole(
                        "Membership {MembershipId} in linkshell {LinkshellId}: rank '{Rank}' does not name a role exactly, so it was given the {Role} role.",
                        membership.Id, linkshellKey, membership.Rank, picked?.Name ?? "(none: the linkshell has no Member role)");
                }
                if (picked is null)
                {
                    continue;
                }
                var chosen = linkshellRoles.First(role => ReferenceEquals(role.Role, picked));
                if (chosen.Tracked && Entry(picked).Property(nameof(LinkshellRole.Id)).IsTemporary)
                {
                    // Through the navigation, so a role added in this save is linked before it has
                    // a real id; EF fills the id in when it inserts the role.
                    entry.Reference(nameof(AppUserLinkshell.Role)).CurrentValue = picked;
                }
                else
                {
                    // Through the entry, so an unchanged id is not marked modified.
                    SetDerived(entry, nameof(AppUserLinkshell.LinkshellRoleId), (int?)chosen.Id);
                }
                SetDerived(entry, nameof(AppUserLinkshell.Rank), picked.Name);
            }
        }

        // Adds the built-in roles this linkshell is missing (all four for a brand new one), the way
        // LinkshellPermissions.EnsureDefaultRolesForLinkshellsAsync does: a role counts as present
        // when one has the same name in any capitals. A linkshell added in this same save is linked
        // through its navigation, as it has no id yet.
        private List<LinkshellRole> AddMissingDefaultRoles(
            EntityEntry<AppUserLinkshell> membership, int linkshellKey, List<RoleInSave> linkshellRoles)
        {
            var present = new HashSet<string>(
                linkshellRoles.Select(role => role.Role.Name), StringComparer.OrdinalIgnoreCase);
            var missing = LinkshellRoleDefaults.BuildDefaultRoles(linkshellKey)
                .Where(role => !present.Contains(role.Name))
                .ToList();
            if (missing.Count == 0)
            {
                return missing;
            }
            if (membership.Property(nameof(AppUserLinkshell.LinkshellId)).IsTemporary)
            {
                var linkshell = ChangeTracker.Entries<Linkshell>()
                    .FirstOrDefault(entry => Equals(entry.Property(nameof(Linkshell.Id)).CurrentValue, linkshellKey))
                    ?.Entity;
                if (linkshell is null)
                {
                    return new List<LinkshellRole>();
                }
                foreach (var role in missing)
                {
                    role.LinkshellId = 0;
                    role.Linkshell = linkshell;
                }
            }
            LinkshellRoles.AddRange(missing);
            return missing;
        }

        // A warning in the app log. Logging must not fail a save, so a context built without a
        // logger simply logs nothing.
        private void LogMembershipRole(string message, params object?[] args)
        {
            try
            {
                Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                    .GetService<Microsoft.Extensions.Logging.ILoggerFactory>(this)
                    .CreateLogger(typeof(MembershipRoleMatch).FullName!)
                    .Log(Microsoft.Extensions.Logging.LogLevel.Warning, message, args);
            }
            catch (InvalidOperationException)
            {
            }
        }

        // Keeps the derived ToD columns (AddTodDurationsAndSpawnKeys) in step with their text on every
        // save that adds or changes a row: SpawnKey from MonsterName (HnmConfig.SpawnKey), and
        // CooldownMinutes / IntervalMinutes from Cooldown / Interval (TodDurationFormat
        // .StoredMinutes; a bare number is hours for a cooldown, minutes for an interval). Done here
        // so every writer gets it -- TodRecorder, the ToD forms, End Camp, the approval queue, the
        // addon and Data Admin -- and the next one cannot forget it. Always re-derived from the text,
        // so a value set by hand never sticks. No database work: both helpers are plain C#.
        // Since Round 5 it also stamps SpawnKey on the camp tables (AddCampSpawnKeys): Events,
        // PartySetups, ClaimShieldCaptures and LinkshellMonsterTimings.
        private void StampDerivedColumns()
        {
            foreach (var entry in ChangeTracker.Entries<Tod>().Where(IsAddedOrModified))
            {
                var tod = entry.Entity;
                SetDerived(entry, nameof(Tod.SpawnKey), HnmConfig.SpawnKey(tod.MonsterName));
                SetDerived(entry, nameof(Tod.CooldownMinutes),
                    TodDurationFormat.StoredMinutes(tod.Cooldown, TodDurationFormat.HoursUnit));
                SetDerived(entry, nameof(Tod.IntervalMinutes),
                    TodDurationFormat.StoredMinutes(tod.Interval, TodDurationFormat.MinutesUnit));
            }
            foreach (var entry in ChangeTracker.Entries<PendingTodSubmission>().Where(IsAddedOrModified))
            {
                var pending = entry.Entity;
                SetDerived(entry, nameof(PendingTodSubmission.SpawnKey), HnmConfig.SpawnKey(pending.MonsterName));
                SetDerived(entry, nameof(PendingTodSubmission.CooldownMinutes),
                    TodDurationFormat.StoredMinutes(pending.Cooldown, TodDurationFormat.HoursUnit));
                SetDerived(entry, nameof(PendingTodSubmission.IntervalMinutes),
                    TodDurationFormat.StoredMinutes(pending.Interval, TodDurationFormat.MinutesUnit));
            }
            foreach (var entry in ChangeTracker.Entries<HnmRecurringBoard>().Where(IsAddedOrModified))
            {
                SetDerived(entry, nameof(HnmRecurringBoard.SpawnKey), HnmConfig.SpawnKey(entry.Entity.MonsterName));
            }

            // The camp tables (AddCampSpawnKeys, Round 5). An event and a party setup take the key
            // from AssignedMonsterName, never EventName: only the assigned monster is a spawn.
            foreach (var entry in ChangeTracker.Entries<Event>().Where(IsAddedOrModified))
            {
                SetDerived(entry, nameof(Event.SpawnKey), HnmConfig.SpawnKey(entry.Entity.AssignedMonsterName));
            }
            foreach (var entry in ChangeTracker.Entries<PartySetup>().Where(IsAddedOrModified))
            {
                SetDerived(entry, nameof(PartySetup.SpawnKey), HnmConfig.SpawnKey(entry.Entity.AssignedMonsterName));
            }
            foreach (var entry in ChangeTracker.Entries<ClaimShieldCapture>().Where(IsAddedOrModified))
            {
                SetDerived(entry, nameof(ClaimShieldCapture.SpawnKey), HnmConfig.SpawnKey(entry.Entity.MonsterName));
            }
            foreach (var entry in ChangeTracker.Entries<LinkshellMonsterTiming>().Where(IsAddedOrModified))
            {
                SetDerived(entry, nameof(LinkshellMonsterTiming.SpawnKey), HnmConfig.SpawnKey(entry.Entity.MonsterName));
            }
        }

        private static bool IsAddedOrModified(EntityEntry entry) =>
            entry.State is EntityState.Added or EntityState.Modified;

        // Through the entry, so a value that did not change is not marked modified.
        private static void SetDerived(EntityEntry entry, string property, object? value)
        {
            var current = entry.Property(property);
            if (!Equals(current.CurrentValue, value))
            {
                current.CurrentValue = value;
            }
        }

        // Characters (AddCharacters) follows the accounts. A save that adds an account, or changes
        // one of its names, jobs or crafts, is followed by CharacterSync.SyncSql for that account,
        // which copies its three slots from the row AS SAVED. See CharacterSync for why it is SQL run
        // after the save and not tracked rows inside it. Collected before the save, because the
        // entry states are reset by it. A name too long to copy is left out and logged here.
        private List<string> CollectAccountsNeedingCharacterSync()
        {
            var ids = new List<string>();
            foreach (var entry in ChangeTracker.Entries<AppUser>())
            {
                var changed = entry.State == EntityState.Added
                    || (entry.State == EntityState.Modified
                        && CharacterSync.SourceProperties.Any(name => entry.Property(name).IsModified));
                // A deleted account's rows go with it: Postgres cascades, the InMemory copy is told.
                var deletedWithoutSql = entry.State == EntityState.Deleted && !Database.IsRelational();
                if (!(changed || deletedWithoutSql) || string.IsNullOrEmpty(entry.Entity.Id))
                {
                    continue;
                }
                ids.Add(entry.Entity.Id);
                if (deletedWithoutSql)
                {
                    continue;
                }
                var tooLong = CharacterSync.TooLongNames(entry.Entity).ToList();
                if (tooLong.Count > 0)
                {
                    LogCharacterSync(null,
                        "Characters: account {AppUserId} has a name longer than {MaxLength} characters ({Names}); it is not copied.",
                        entry.Entity.Id, Character.MaxNameLength, CharacterSync.Describe(tooLong));
                }
            }
            return ids.Distinct(StringComparer.Ordinal).ToList();
        }

        private const string CharacterSyncSavepoint = "character_sync";

        // Never fails the save it follows. Without a transaction the save is already committed, and
        // a failure here is only logged. Inside the caller's transaction it runs in a savepoint, so a
        // failure is undone on its own and the caller's work carries on. No cancellation token: a
        // copy stopped half-way inside a transaction would leave that transaction unusable.
        private async Task SyncCharactersAsync(List<string> accountIds, bool acceptedAllChanges)
        {
            if (accountIds.Count == 0)
            {
                return;
            }
            if (!Database.IsRelational())
            {
                await CopyCharactersWithoutSqlAsync(accountIds, acceptedAllChanges);
                return;
            }
            var transaction = Database.CurrentTransaction;
            try
            {
                if (transaction is not null)
                {
                    await transaction.CreateSavepointAsync(CharacterSyncSavepoint);
                }
                await Database.ExecuteSqlRawAsync(CharacterSync.SyncSql, CharacterSyncParameters(accountIds));
                if (transaction is not null)
                {
                    await transaction.ReleaseSavepointAsync(CharacterSyncSavepoint);
                }
            }
            catch (Exception ex)
            {
                if (transaction is not null)
                {
                    try
                    {
                        await transaction.RollbackToSavepointAsync(CharacterSyncSavepoint);
                    }
                    catch (Exception rollbackError)
                    {
                        LogCharacterSync(rollbackError, "Characters: could not undo a failed copy.");
                    }
                }
                LogCharacterSync(ex, "Characters: copying {Count} account(s) failed; the save itself went through.", accountIds.Count);
            }
        }

        private void SyncCharacters(List<string> accountIds, bool acceptedAllChanges)
        {
            if (accountIds.Count == 0)
            {
                return;
            }
            if (!Database.IsRelational())
            {
                CopyCharactersWithoutSql(accountIds, acceptedAllChanges);
                return;
            }
            var transaction = Database.CurrentTransaction;
            try
            {
                transaction?.CreateSavepoint(CharacterSyncSavepoint);
                Database.ExecuteSqlRaw(CharacterSync.SyncSql, CharacterSyncParameters(accountIds));
                transaction?.ReleaseSavepoint(CharacterSyncSavepoint);
            }
            catch (Exception ex)
            {
                if (transaction is not null)
                {
                    try
                    {
                        transaction.RollbackToSavepoint(CharacterSyncSavepoint);
                    }
                    catch (Exception rollbackError)
                    {
                        LogCharacterSync(rollbackError, "Characters: could not undo a failed copy.");
                    }
                }
                LogCharacterSync(ex, "Characters: copying {Count} account(s) failed; the save itself went through.", accountIds.Count);
            }
        }

        private static object[] CharacterSyncParameters(List<string> accountIds) =>
            CharacterSync.Parameters(accountIds, DateTime.UtcNow);

        // The InMemory test database has no SQL, and the name lookups read Characters, so there the
        // copy is made in C# by the same rules (CharacterSync.DesiredRows), as a second save of just
        // these rows. Only after a save that accepted its changes: otherwise that second save would
        // also save the caller's pending changes.
        private void CopyCharactersWithoutSql(List<string> accountIds, bool acceptedAllChanges)
        {
            if (acceptedAllChanges && StageCharacterCopyWithoutSql(accountIds))
            {
                base.SaveChanges(acceptAllChangesOnSuccess: true);
            }
        }

        private async Task CopyCharactersWithoutSqlAsync(List<string> accountIds, bool acceptedAllChanges)
        {
            if (acceptedAllChanges && StageCharacterCopyWithoutSql(accountIds))
            {
                await base.SaveChangesAsync(acceptAllChangesOnSuccess: true);
            }
        }

        // Adds, updates and removes tracked Characters rows to match the accounts as saved. Returns
        // whether anything changed. An account no longer tracked (deleted) loses its rows.
        private bool StageCharacterCopyWithoutSql(List<string> accountIds)
        {
            var users = ChangeTracker.Entries<AppUser>()
                .Where(entry => entry.State is EntityState.Unchanged or EntityState.Modified)
                .Select(entry => entry.Entity)
                .Where(user => accountIds.Contains(user.Id, StringComparer.Ordinal))
                .ToDictionary(user => user.Id, StringComparer.Ordinal);
            var existing = Characters.Where(character => accountIds.Contains(character.AppUserId)).ToList();
            var changed = false;
            foreach (var accountId in accountIds)
            {
                var desired = users.TryGetValue(accountId, out var user)
                    ? CharacterSync.DesiredRows(user)
                    : Array.Empty<CharacterSync.DesiredRow>();
                var rows = existing.Where(row => row.AppUserId == accountId).ToList();
                foreach (var row in rows.Where(row => !desired.Any(want =>
                             want.Slot == row.Slot && want.NormalizedName == row.NormalizedName)).ToList())
                {
                    Characters.Remove(row);
                    rows.Remove(row);
                    changed = true;
                }
                foreach (var want in desired)
                {
                    var row = rows.SingleOrDefault(row => row.Slot == want.Slot);
                    if (row is null)
                    {
                        row = new Character { AppUserId = accountId, Slot = want.Slot };
                        Characters.Add(row);
                    }
                    else if (row.Name == want.Name
                             && Same(row.JobLevels, want.JobLevels) && Same(row.StrongJobs, want.StrongJobs)
                             && Same(row.MeritJobs, want.MeritJobs) && Same(row.CraftLevels, want.CraftLevels))
                    {
                        continue;
                    }
                    row.Name = want.Name;
                    row.NormalizedName = want.NormalizedName;
                    row.JobLevels = want.JobLevels?.ToArray();
                    row.StrongJobs = want.StrongJobs?.ToArray();
                    row.MeritJobs = want.MeritJobs?.ToArray();
                    row.CraftLevels = want.CraftLevels?.ToArray();
                    row.UpdatedAt = DateTime.UtcNow;
                    changed = true;
                }
            }
            return changed;

            static bool Same<T>(T[]? a, T[]? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);
        }

        // A warning in the app log. Logging must not fail a save either, so a context built without
        // a logger simply logs nothing.
        private void LogCharacterSync(Exception? error, string message, params object?[] args)
        {
            try
            {
                Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                    .GetService<Microsoft.Extensions.Logging.ILoggerFactory>(this)
                    .CreateLogger(typeof(CharacterSync).FullName!)
                    .Log(Microsoft.Extensions.Logging.LogLevel.Warning, error, message, args);
            }
            catch (InvalidOperationException)
            {
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
                // An off-ledger balance change is legal iff it also writes the pool epoch — the
                // placeholder-claim merge (its matching ledger rows change OWNER in a later save of
                // the claim, which this check cannot see) and the DKP import are the only such paths.
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

        // ---- Data/ConcurrencyRetry.cs bookkeeping ------------------------------------------------

        // How many ConcurrencyRetry.RunAsync calls are running on this context. Only the outermost
        // one re-runs its action: an inner one clearing the tracker would drop entities the outer
        // action still holds.
        internal int ConcurrencyRetryDepth { get; set; }

        // Saves that committed on their own (no transaction open). ConcurrencyRetry compares it
        // before and after an attempt: a re-run must never repeat a save that already went through.
        internal int CommittedSaveCount { get; private set; }

        // Raised by ResetForRetry. Request-scoped services that remember things between saves
        // (DkpLedgerWriter's sequence numbers and staged pool amounts, TreasuryJournalWriter's)
        // forget them here, or the re-run would build on numbers from the failed attempt.
        internal event Action? ResetForRetryRequested;

        // Forget everything the failed attempt loaded or staged, so the re-run starts from what is
        // in the database now.
        internal void ResetForRetry()
        {
            ChangeTracker.Clear();
            ResetForRetryRequested?.Invoke();
        }

        private void CountCommittedSave()
        {
            if (Database.CurrentTransaction is null && _repeatSafeSaveScopes == 0)
            {
                CommittedSaveCount++;
            }
        }

        // Saves inside this scope are not counted in CommittedSaveCount, because running them again
        // gives the same result. Why it exists: every Discord Activity request signs the caller in
        // first (DiscordIdentityService refreshes the caller's Discord row and can save), INSIDE the action that
        // ConcurrencyRetry wraps. Counting that save turned the re-run off for every Post, End Event,
        // auction close and DKP audit sent from the Activity, so a lost race always became a 409.
        //
        // Work already staged when the scope opens would be saved by the first save in here, and that
        // work is not known to be safe to repeat, so then the scope changes nothing.
        internal IDisposable RepeatSafeSaves()
        {
            if (ChangeTracker.HasChanges())
            {
                return RepeatSafeSaveScope.None;
            }
            _repeatSafeSaveScopes++;
            return new RepeatSafeSaveScope(this);
        }

        private int _repeatSafeSaveScopes;

        private sealed class RepeatSafeSaveScope : IDisposable
        {
            internal static readonly IDisposable None = new RepeatSafeSaveScope(null);

            private ApplicationDbContext? _owner;

            internal RepeatSafeSaveScope(ApplicationDbContext? owner) => _owner = owner;

            public void Dispose()
            {
                if (_owner is not null)
                {
                    _owner._repeatSafeSaveScopes--;
                    _owner = null;
                }
            }
        }

        public override async Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            await StampAccountsFromCharactersAsync(async: true, cancellationToken);
            await StampNewMembershipDkpEpochAsync(CollectNewMembershipsNeedingDkpEpoch(), cancellationToken);
            await StampMembershipRolesAsync(CollectMembershipsNeedingRoleStamp(), cancellationToken);
            StampDerivedColumns();
            AssertDkpLedgerInvariant();
            AssertTreasuryEntriesBalance();
            AssertConfirmedTreasuryEntriesAreImmutable();
            var affected = CollectChangedTodLinkshellIds();
            var (createdAuctions, closedAuctions, retimedAuctions) = CollectAuctionChannelWork();
            var createdEvents = CollectAddedEvents();
            var editedEvents = CollectEditedEvents();
            var eventLoot = CollectAddedLoot();
            var endedEvents = CollectAddedEventHistories();
            var deletedEventBoards = CollectDeletedEventBoards();
            var deletedAuctionCards = CollectDeletedAuctionCards();
            var bidItemIds = CollectAddedBidAuctionItemIds();
            var dkpChangedLinkshells = CollectDkpChangedLinkshellIds();
            var liveChanges = CollectLiveChanges();
            var characterAccounts = CollectAccountsNeedingCharacterSync();
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (_bidAnnouncementsHeld is not null)
            {
                // Not committed yet (see HoldBidAnnouncements): sent by ReleaseHeldBidAnnouncementsAsync.
                _bidAnnouncementsHeld.UnionWith(bidItemIds);
                _bidAnnouncementsHeld.UnionWith(liveChanges.BidAuctionItemIds);
                bidItemIds.Clear();
                liveChanges.BidAuctionItemIds.Clear();
            }
            CountCommittedSave();
            await SyncCharactersAsync(characterAccounts, acceptAllChangesOnSuccess);
            EnqueueTodBoardRefreshes(affected);
            EnqueueAuctionChannelWork(createdAuctions, closedAuctions, retimedAuctions);
            EnqueueEventPosts(createdEvents);
            EnqueueEventPosts(editedEvents);
            EnqueueLootPosts(eventLoot);
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
            // With async false every query in it runs synchronously, so the task is already complete.
            StampAccountsFromCharactersAsync(async: false, CancellationToken.None).GetAwaiter().GetResult();
            StampNewMembershipDkpEpoch(CollectNewMembershipsNeedingDkpEpoch());
            StampMembershipRoles(CollectMembershipsNeedingRoleStamp());
            StampDerivedColumns();
            AssertDkpLedgerInvariant();
            AssertTreasuryEntriesBalance();
            AssertConfirmedTreasuryEntriesAreImmutable();
            var affected = CollectChangedTodLinkshellIds();
            var (createdAuctions, closedAuctions, retimedAuctions) = CollectAuctionChannelWork();
            var createdEvents = CollectAddedEvents();
            var editedEvents = CollectEditedEvents();
            var eventLoot = CollectAddedLoot();
            var endedEvents = CollectAddedEventHistories();
            var deletedEventBoards = CollectDeletedEventBoards();
            var deletedAuctionCards = CollectDeletedAuctionCards();
            var dkpChangedLinkshells = CollectDkpChangedLinkshellIds();
            var liveChanges = CollectLiveChanges();
            var characterAccounts = CollectAccountsNeedingCharacterSync();
            var result = base.SaveChanges(acceptAllChangesOnSuccess);
            CountCommittedSave();
            SyncCharacters(characterAccounts, acceptAllChangesOnSuccess);
            EnqueueTodBoardRefreshes(affected);
            EnqueueAuctionChannelWork(createdAuctions, closedAuctions, retimedAuctions);
            EnqueueEventPosts(createdEvents);
            EnqueueEventPosts(editedEvents);
            EnqueueLootPosts(eventLoot);
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
        public DbSet<PartySetup> PartySetups => Set<PartySetup>();
        public DbSet<PartySetupAlliance> PartySetupAlliances => Set<PartySetupAlliance>();
        public DbSet<PartySetupParty> PartySetupParties => Set<PartySetupParty>();
        public DbSet<PartySetupSlot> PartySetupSlots => Set<PartySetupSlot>();
        public DbSet<Rule> Rules => Set<Rule>();
        public DbSet<Announcement> Announcements => Set<Announcement>();
        public DbSet<Item> Items => Set<Item>();
        public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
        public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
        public DbSet<JournalEntryLine> JournalEntryLines => Set<JournalEntryLine>();
        public DbSet<LinkshellRole> LinkshellRoles => Set<LinkshellRole>();
        public DbSet<JobRating> JobRatings => Set<JobRating>();
        public DbSet<EventComment> EventComments => Set<EventComment>();
        public DbSet<AddonApiToken> AddonApiTokens => Set<AddonApiToken>();
        public DbSet<AddonPairingCode> AddonPairingCodes => Set<AddonPairingCode>();
        public DbSet<EventAttendanceWindow> EventAttendanceWindows => Set<EventAttendanceWindow>();
        public DbSet<AppUserEventWindow> AppUserEventWindows => Set<AppUserEventWindow>();
        public DbSet<AttendanceSnapshot> AttendanceSnapshots => Set<AttendanceSnapshot>();
        // Who is in the world right now, per linkshell. A cache of the present, not a record --
        // see LinkshellPresence for why the game leaves no alternative.
        public DbSet<LinkshellPresence> LinkshellPresences => Set<LinkshellPresence>();
        public DbSet<AttendanceSnapshotEntry> AttendanceSnapshotEntries => Set<AttendanceSnapshotEntry>();
        public DbSet<WindowEvent> WindowEvents => Set<WindowEvent>();
        public DbSet<WindowEventMemberDkp> WindowEventMemberDkps => Set<WindowEventMemberDkp>();
        public DbSet<PendingTodSubmission> PendingTodSubmissions => Set<PendingTodSubmission>();
        public DbSet<ClaimShieldCapture> ClaimShieldCaptures => Set<ClaimShieldCapture>();
        public DbSet<ClaimShieldCaptureMember> ClaimShieldCaptureMembers => Set<ClaimShieldCaptureMember>();
        public DbSet<LinkshellChannelRoute> LinkshellChannelRoutes => Set<LinkshellChannelRoute>();
        public DbSet<HnmRecurringBoard> HnmRecurringBoards => Set<HnmRecurringBoard>();
        public DbSet<LinkshellMonsterTiming> LinkshellMonsterTimings => Set<LinkshellMonsterTiming>();
        public DbSet<ChartPopItem> ChartPopItems => Set<ChartPopItem>();
        public DbSet<ChartPopItemCredit> ChartPopItemCredits => Set<ChartPopItemCredit>();
        public DbSet<ChartWishlistRequest> ChartWishlistRequests => Set<ChartWishlistRequest>();
        public DbSet<ChartWishlistRequestZone> ChartWishlistRequestZones => Set<ChartWishlistRequestZone>();
        public DbSet<ChartMemberKeyItem> ChartMemberKeyItems => Set<ChartMemberKeyItem>();
        public DbSet<Character> Characters => Set<Character>();
        public DbSet<AppSetting> AppSettings => Set<AppSetting>();
        public DbSet<PlaceholderClaimRequest> PlaceholderClaimRequests => Set<PlaceholderClaimRequest>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<DiscordActivityUser>(entity =>
            {
                entity.ToTable("DiscordActivityUsers");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.DiscordUserId).HasMaxLength(32).IsRequired();
                entity.Property(user => user.Username).HasMaxLength(32).IsRequired();
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

                // Derived column (AddCampSpawnKeys), stamped by StampDerivedColumns. The plain
                // LinkshellId index is named so EF keeps it: the new pair index would otherwise
                // replace it, and AddCampSpawnKeys only adds.
                entity.HasIndex(item => item.LinkshellId);
                entity.HasIndex(item => new { item.LinkshellId, item.SpawnKey });

                // Npgsql maps a uint row version to Postgres's built-in xmin, so this adds no
                // column: it only makes every UPDATE/DELETE of an event check nobody saved it since
                // we read it. Why: Models/Event.cs (Version).
                entity.Property(item => item.Version).IsRowVersion();
            });

            builder.Entity<AppUserLinkshell>(entity =>
            {
                // Same xmin check for memberships: a balance (LinkshellDkp) is read, changed and
                // written back, so without it two overlapping DKP moves lose one of them.
                entity.Property(item => item.Version).IsRowVersion();

                // One membership per account per linkshell. Every join path checks first, but a
                // double-clicked Discord Sign Up (or two join paths at once) both pass that check,
                // and two rows split the member's DKP between them and show them twice on the
                // roster. The database is the one that says no; the join paths turn that into
                // "already a member". Filtered: a row without an account is not a person to dedupe.
                // Existing duplicates were merged by the OneMembershipPerLinkshell migration.
                entity.HasIndex(item => new { item.LinkshellId, item.AppUserId })
                    .IsUnique()
                    .HasFilter("\"AppUserId\" IS NOT NULL");

                // The role a member holds, by id (AddMembershipRoleId). Two columns, so the role
                // must belong to the member's own linkshell. NO ACTION, not RESTRICT: deleting a
                // linkshell cascades to its members and its roles in one statement, and NO ACTION
                // only checks at the end of that statement. Deleting a role someone still holds
                // is refused. The Role navigation is model-only (no schema change): the save hook
                // uses it to link a member to a role added in the same save (StampMembershipRoles).
                //
                // The column is NOT NULL in the database (MembershipRoleRequired), but the model
                // keeps it optional ON PURPOSE. That migration's NOT NULL is guarded: if it ever had
                // to skip (a member left with no role), a required model would make every query that
                // loads that member throw ("Column 'LinkshellRoleId' is null"), so whole pages would
                // fail. Optional, the row still loads and LinkshellPermissions gives it the Member
                // role with a warning. MembershipRoleRequiredRoundTripTests checks both.
                entity.HasOne(item => item.Role)
                    .WithMany()
                    .HasForeignKey(item => new { item.LinkshellId, item.LinkshellRoleId })
                    .HasPrincipalKey(role => new { role.LinkshellId, role.Id })
                    .OnDelete(DeleteBehavior.NoAction);
            });

            builder.Entity<AppUserEvent>(entity =>
            {
                // One participation per account per event, for the same reason: two rows made the
                // board show the member twice, and close paid only one of them. Outside sign-ups
                // (Discord id, no account) are not covered. Existing duplicates were merged by the
                // OneParticipationPerEvent migration.
                entity.HasIndex(item => new { item.EventId, item.AppUserId })
                    .IsUnique()
                    .HasFilter("\"AppUserId\" IS NOT NULL");
            });

            builder.Entity<HnmRecurringBoard>(entity =>
            {
                // One standing board per SPAWN (TodSwitch merged any second one). SpawnKey is
                // derived from MonsterName by StampDerivedColumns, and a board always names a
                // monster, so it is required.
                entity.Property(board => board.SpawnKey).IsRequired();
                entity.HasIndex(board => new { board.LinkshellId, board.SpawnKey }).IsUnique();
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

            builder.Entity<LinkshellMonsterTiming>(entity =>
            {
                entity.ToTable("LinkshellMonsterTimings");
                entity.Property(timing => timing.MonsterName).HasMaxLength(128).IsRequired();
                entity.Property(timing => timing.NormalizedMonsterName).HasMaxLength(128).IsRequired();
                entity.Property(timing => timing.Category).HasMaxLength(32);
                // One setup per monster per linkshell. Keyed on the NORMALIZED name so casing can't
                // slip a second row past it. It still cannot catch "Nidhogg" sitting beside
                // "Fafnir/Nidhogg" — those are different strings for one spawn — which is why
                // MonsterTimingEditor rejects a duplicate under HnmConfig.MonsterMatchNames before
                // it ever reaches the DB.
                entity.HasIndex(timing => new { timing.LinkshellId, timing.NormalizedMonsterName }).IsUnique();
                // Derived column (AddCampSpawnKeys), stamped by StampDerivedColumns. One row per
                // SPAWN: CampSpawnKeySwitch makes this unique, guarded (if two rows already named one
                // spawn it keeps the plain index and warns, and MigrationHealth says so on start-up).
                entity.HasIndex(timing => new { timing.LinkshellId, timing.SpawnKey }).IsUnique();
                entity.HasOne(timing => timing.Linkshell)
                    .WithMany()
                    .HasForeignKey(timing => timing.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
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

                // One open join request per player per linkshell. The request path checks first,
                // but a double click passes that check twice; the database is what says no.
                // Answered, cancelled and invite rows are not covered.
                entity.HasIndex(invite => new { invite.LinkshellId, invite.AppUserId })
                    .IsUnique()
                    .HasFilter("\"Status\" = 'PendingJoinRequest'")
                    .HasDatabaseName("IX_Invites_OneOpenJoinRequest");
                // One open invite per person per linkshell: by account, and by Discord id for
                // someone invited before they had an account. "Pending" is what the old website
                // wrote, "PendingInvite" what the Activity writes. Join requests are the index
                // above, not these. Filters match OneCloseOneRouteOneInvite's SQL exactly.
                entity.HasIndex(invite => new { invite.LinkshellId, invite.AppUserId }, "IX_Invites_OneOpenInvite")
                    .IsUnique()
                    .HasFilter("\"Status\" IN ('Pending', 'PendingInvite') AND \"AppUserId\" IS NOT NULL");
                entity.HasIndex(invite => new { invite.LinkshellId, invite.DiscordUserId }, "IX_Invites_OneOpenDiscordInvite")
                    .IsUnique()
                    .HasFilter("\"Status\" IN ('Pending', 'PendingInvite') AND \"AppUserId\" IS NULL AND \"DiscordUserId\" IS NOT NULL");
                // Kept: the index above only holds open join requests, so it cannot answer
                // "every invite of this linkshell" (and EF would otherwise drop this one for it).
                entity.HasIndex(invite => invite.LinkshellId);
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
                // Closed-auction history rows keep SourceItemId, and closing an auction deletes the
                // treasury Item once its stack runs out. Without a delete rule Postgres refused that
                // DELETE whenever an older history row (one EF had not loaded) still pointed at the
                // Item, rolling back the whole close. SET NULL lets the Item go; the history row keeps
                // its ItemName, so nothing visible is lost.
                entity.HasOne(item => item.SourceItem)
                    .WithMany()
                    .HasForeignKey(item => item.SourceItemId)
                    .OnDelete(DeleteBehavior.SetNull);
                // Exactly one parent: a live auction OR its closed history, never both and never
                // neither. An item with neither shows on no auction and no history page, so it
                // would sit in the table unseen. Close moves each item (with its bids) from the
                // auction to the new history in one UPDATE, so every correct write sets just one.
                // AddOneParentCheckConstraints added it NOT VALID (old rows were not re-checked at
                // startup); NormalizeNamesForSquash validated it once 0 rows broke it.
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_AuctionItems_OneParent",
                    "num_nonnulls(\"AuctionId\", \"AuctionHistoryId\") = 1"));
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
                entity.HasOne(item => item.AppUserEvent)
                    .WithMany(item => item.StatusLedgerEntries)
                    .HasForeignKey(item => item.AppUserEventId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(item => item.Event)
                    .WithMany(item => item.StatusLedgerEntries)
                    .HasForeignKey(item => item.EventId)
                    .OnDelete(DeleteBehavior.Cascade);
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
                entity.HasIndex(item => new { item.LinkshellId, item.AppUserId, item.OccurredAt, item.Sequence });
                // Index the loot-source FKs so the Loot History view can
                // quickly find the edit-pair ledger entries for a given
                // loot row without scanning the full ledger.
                entity.HasIndex(item => item.SourceEventLootDetailId);
                entity.HasIndex(item => item.SourceWindowEventId);
                // One payout row per account per review card. Post checks "already paid?" before
                // inserting, but two Posts (or a Post and an Update) at the same moment both pass that
                // check; this index is what turns the second credit into an error instead of double
                // DKP. Post re-runs on that error (UniqueRaceRetry) and then pays nobody twice. Audit
                // rows carry the card's id too, so the filter keeps them out.
                entity.HasIndex(item => new { item.SourceWindowEventId, item.AppUserId })
                    .IsUnique()
                    .HasFilter("\"EntryType\" = 'SnapshotEarned' AND \"AppUserId\" IS NOT NULL AND \"SourceWindowEventId\" IS NOT NULL");
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
                entity.HasIndex(item => new { item.LinkshellId, item.Time });
                // Derived columns (AddTodDurationsAndSpawnKeys), stamped by StampDerivedColumns.
                entity.HasIndex(item => new { item.LinkshellId, item.SpawnKey, item.Time });
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint(
                        "CK_Tods_CooldownMinutes_Positive", "\"CooldownMinutes\" IS NULL OR \"CooldownMinutes\" > 0");
                    table.HasCheckConstraint(
                        "CK_Tods_IntervalMinutes_Positive", "\"IntervalMinutes\" IS NULL OR \"IntervalMinutes\" > 0");
                });
            });

            builder.Entity<PendingTodSubmission>(entity =>
            {
                // Derived columns (AddTodDurationsAndSpawnKeys), stamped by StampDerivedColumns.
                entity.HasIndex(item => new { item.LinkshellId, item.SpawnKey });
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint(
                        "CK_PendingTodSubmissions_CooldownMinutes_Positive", "\"CooldownMinutes\" IS NULL OR \"CooldownMinutes\" > 0");
                    table.HasCheckConstraint(
                        "CK_PendingTodSubmissions_IntervalMinutes_Positive", "\"IntervalMinutes\" IS NULL OR \"IntervalMinutes\" > 0");
                });
            });

            // EventHistory has no config block of its own — conventions cover it — except for
            // this one FK. Convention would make PartySetupId REQUIRED-ish/cascade here, and a
            // deleted template must never take closed history with it: the history row is the
            // payout record, the setup reference is only a convenience for the next pop.
            builder.Entity<EventHistory>(entity =>
            {
                entity.HasOne(item => item.PartySetup)
                    .WithMany()
                    .HasForeignKey(item => item.PartySetupId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => new { item.LinkshellId, item.EventName });
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
                // Provenance only, so SET NULL: deleting a template must not cascade into the
                // snapshots that were cloned from it (those belong to their events).
                entity.HasOne(item => item.ClonedFromPartySetup)
                    .WithMany()
                    .HasForeignKey(item => item.ClonedFromPartySetupId)
                    .OnDelete(DeleteBehavior.SetNull);
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
                // The loot-history list is per linkshell, and a "No event" row has no Event or
                // EventHistory to reach one through, so this is the only predicate that finds it.
                entity.HasIndex(item => item.LinkshellId);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                // SetNull: deleting a pool must not destroy the loot record.
                entity.HasOne(item => item.DkpPool)
                    .WithMany()
                    .HasForeignKey(item => item.DkpPoolId)
                    .OnDelete(DeleteBehavior.SetNull);
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
                // It also serves "every rating of one target" (LinkshellId, TargetAppUserId), since
                // those are its first two columns, so that query needs no index of its own.
                entity.HasIndex(item => new { item.LinkshellId, item.TargetAppUserId, item.RaterAppUserId, item.CharacterSlot, item.JobIndex }).IsUnique();
                // Real foreign keys (JobRatingsForeignKeys): a rating of, or by, an account or
                // linkshell that no longer exists is shown nowhere, so it goes with them. A claimed
                // placeholder's ratings are moved first (UserMergeService), so a claim keeps them.
                // No navigations: nothing reads a rating through its linkshell or its users.
                entity.HasOne<Linkshell>()
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<AppUser>()
                    .WithMany()
                    .HasForeignKey(item => item.TargetAppUserId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<AppUser>()
                    .WithMany()
                    .HasForeignKey(item => item.RaterAppUserId)
                    .OnDelete(DeleteBehavior.Cascade);
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

            // --- Gil treasury: categories, transactions and their two halves. ---
            // These replaced the old RevenueEntries gil list (converted long ago, table dropped).

            builder.Entity<LedgerAccount>(entity =>
            {
                entity.ToTable("LedgerAccounts");
                entity.Property(item => item.Name).HasMaxLength(96).IsRequired();
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => new { item.LinkshellId, item.AccountNumber }).IsUnique();
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
                // One reversal per entry: a second one would take the same gil off the books twice.
                // TreasuryJournalWriter checks first; this stops two reversals saved at the same
                // moment. Corrections share the column (a Fix is a Reversal plus a Correction), so
                // only Reversal rows count. The migration skips it if old duplicates exist.
                entity.HasIndex(item => item.ReversesJournalEntryId, "IX_JournalEntries_OneReversalPerEntry")
                    .IsUnique()
                    .HasFilter("\"Kind\" = 'Reversal'");
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
                entity.Property(item => item.CounterpartyAppUserId).HasMaxLength(450);
                entity.Property(item => item.CounterpartyCharacterName).HasMaxLength(256);
                entity.Property(item => item.HolderAppUserId).HasMaxLength(450);
                entity.Property(item => item.HolderCharacterName).HasMaxLength(256);
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
                // LinkshellId is a plain indexed int with no relationship; the header's FK already
                // ties the line to its linkshell (see JournalEntryLine.LinkshellId).
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
                // Pop or Drop. A default rather than a backfill: every row that predates the column
                // is a pop item, which is exactly what the default says.
                entity.Property(item => item.Kind).HasMaxLength(8).IsRequired()
                    .HasDefaultValue(ChartItemKinds.Pop);
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
                // No index on Kind: it is a display split applied in memory after the board is
                // loaded, never a query predicate. The index above already returns every row.
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint(
                        "CK_ChartPopItems_Quantity_NonNegative", "\"Quantity\" >= 0");
                    table.HasCheckConstraint(
                        "CK_ChartPopItems_Kind", "\"Kind\" IN ('Pop','Drop')");
                });
            });

            builder.Entity<ChartPopItemCredit>(entity =>
            {
                entity.ToTable("ChartPopItemCredits");
                entity.Property(item => item.CharacterName).HasMaxLength(256).IsRequired();
                entity.Property(item => item.CreditedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.CreditedByCharacterName).HasMaxLength(256);
                entity.HasOne(item => item.ChartPopItem)
                    .WithMany(row => row.Credits)
                    .HasForeignKey(item => item.ChartPopItemId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(item => item.ChartPopItemId);
                // Deliberately NO unique index on (item, person): credits are written set-wise — the
                // whole list for a row is deleted and re-inserted in one SaveChanges — so a duplicate
                // cannot survive a write, and a unique index would only add a normalized-name column
                // to maintain.
            });

            // --- Charts: what members have ASKED for on a board. ---
            // A separate feature from the pop items above, not a status on them: written by the
            // member rather than by an officer, and carrying no holder, no held quantity and no
            // farming credit.
            builder.Entity<ChartWishlistRequest>(entity =>
            {
                entity.ToTable("ChartWishlistRequests");
                entity.Property(item => item.Board).HasMaxLength(16).IsRequired();
                entity.Property(item => item.ItemName).HasMaxLength(128).IsRequired();
                entity.Property(item => item.Notes).HasMaxLength(512);
                entity.Property(item => item.Status).HasMaxLength(16).IsRequired()
                    .HasDefaultValue(ChartWishlistStatuses.Pending);
                entity.Property(item => item.RequestedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.RequestedByCharacterName).HasMaxLength(256).IsRequired();
                entity.Property(item => item.FulfilledByAppUserId).HasMaxLength(450);
                entity.Property(item => item.FulfilledByCharacterName).HasMaxLength(256);
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                // Covers the one read this feature has: every request for a linkshell's board,
                // pending first, in the order officers put them in. No per-Boss index - the card
                // badges are counted in memory off the list this one already returned, the same way
                // the per-boss item counts are.
                entity.HasIndex(item => new { item.LinkshellId, item.Board, item.Status, item.Priority });
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint(
                        "CK_ChartWishlistRequests_Quantity_Positive", "\"Quantity\" >= 1");
                    table.HasCheckConstraint(
                        "CK_ChartWishlistRequests_Status", "\"Status\" IN ('Pending','Fulfilled')");
                });
            });

            // The zones a request names (none = anywhere). Deleted with the request: withdrawing one
            // deletes its row, and its zones mean nothing without it.
            builder.Entity<ChartWishlistRequestZone>(entity =>
            {
                entity.ToTable("ChartWishlistRequestZones");
                entity.Property(zone => zone.Boss).HasMaxLength(64).IsRequired();
                entity.HasOne(zone => zone.Request)
                    .WithMany(request => request.Zones)
                    .HasForeignKey(zone => zone.RequestId)
                    .OnDelete(DeleteBehavior.Cascade);
                // A zone once per request.
                entity.HasIndex(zone => new { zone.RequestId, zone.Boss }).IsUnique();
            });

            // --- Charts: which members hold which key item. ---
            // Presence is the fact; unticking deletes the row. See ChartMemberKeyItem for why.
            builder.Entity<ChartMemberKeyItem>(entity =>
            {
                entity.ToTable("ChartMemberKeyItems");
                entity.Property(item => item.Board).HasMaxLength(16).IsRequired();
                entity.Property(item => item.KeyItemName).HasMaxLength(128).IsRequired();
                entity.Property(item => item.SetByAppUserId).HasMaxLength(450);
                entity.Property(item => item.SetByCharacterName).HasMaxLength(256);
                // The member's account (AccountJobsAndKeyItemAccounts, RosterSwitch): required, and
                // a real foreign key, so a tick goes when its account is deleted. A claimed
                // placeholder's ticks are moved first (UserMergeService), so a claim keeps them.
                // No navigation: nothing reads a tick through its account.
                entity.Property(item => item.AppUserId).HasMaxLength(450).IsRequired();
                entity.HasIndex(item => item.AppUserId);
                entity.HasOne<AppUser>()
                    .WithMany()
                    .HasForeignKey(item => item.AppUserId)
                    .OnDelete(DeleteBehavior.Cascade);
                // One tick per account per key item (RosterSwitch). The grid reads ticks by account,
                // so a second row for the same person would read as one. This is also what stops a
                // double-clicked tick from writing two rows.
                entity.HasIndex(item => new
                {
                    item.LinkshellId,
                    item.Board,
                    item.KeyItemName,
                    item.AppUserId,
                }).IsUnique();
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // An account's characters (AddCharacters): a copy of the account's main and two alts,
            // kept in step after every save (CharacterSync), that the name lookups read. See Character.
            builder.Entity<Character>(entity =>
            {
                entity.ToTable("Characters", table =>
                {
                    table.HasCheckConstraint("CK_Characters_Slot", "\"Slot\" IN (0, 1, 2)");
                    table.HasCheckConstraint("CK_Characters_Name_NotBlank", CharacterSync.NameNotBlankCheckSql);
                });
                entity.Property(item => item.AppUserId).HasMaxLength(450).IsRequired();
                entity.Property(item => item.Name).HasMaxLength(Character.MaxNameLength).IsRequired();
                entity.Property(item => item.NormalizedName).HasMaxLength(Character.MaxNameLength).IsRequired();
                // One row per slot per account, and one row per name per account (CharactersSwitch; a
                // slot repeating an earlier slot's name has no row). Different accounts may share a
                // name: placeholders often carry a real player's name.
                entity.HasIndex(item => new { item.AppUserId, item.Slot }).IsUnique();
                entity.HasIndex(item => new { item.AppUserId, item.NormalizedName }).IsUnique();
                entity.HasIndex(item => item.NormalizedName);
                // A deleted account takes its characters with it (a claimed placeholder too).
                // The account's Characters navigation is the read side of the names (Round 5).
                entity.HasOne<AppUser>()
                    .WithMany(user => user.Characters)
                    .HasForeignKey(item => item.AppUserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // A by-name claim of an imported member, waiting for an officer. See PlaceholderClaimRequest.
            builder.Entity<PlaceholderClaimRequest>(entity =>
            {
                entity.ToTable("PlaceholderClaimRequests");
                entity.Property(item => item.PlaceholderAppUserId).IsRequired();
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
                // The officers' list: this linkshell's open requests.
                entity.HasIndex(item => new { item.LinkshellId, item.ResolvedAtUtc });
                // One OPEN request per person per placeholder. A double-clicked Claim (or the sign-up
                // form sent twice) would otherwise put the same request in front of officers twice;
                // the service turns the rejection into "already asked". Once resolved, the person
                // may ask again.
                entity.HasIndex(item => new { item.PlaceholderAppUserId, item.RequesterAppUserId })
                    .IsUnique()
                    .HasFilter("\"ResolvedAtUtc\" IS NULL AND \"RequesterAppUserId\" IS NOT NULL");
                entity.HasIndex(item => new { item.PlaceholderAppUserId, item.RequesterDiscordUserId })
                    .IsUnique()
                    .HasFilter("\"ResolvedAtUtc\" IS NULL AND \"RequesterDiscordUserId\" IS NOT NULL");
            });

            builder.Entity<AddonApiToken>(entity =>
            {
                entity.ToTable("AddonApiTokens");
                entity.Property(item => item.TokenHash).HasMaxLength(128).IsRequired();
                entity.Property(item => item.TokenPrefix).HasMaxLength(16).IsRequired();
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
                // Still Cascade even though EventId is now nullable: an event DELETED outright
                // (not closed) should take its windows with it. The close path doesn't rely on
                // this — it clears EventId and sets EventHistoryId first, so by the time the Event
                // is removed the window no longer points at it and survives.
                entity.HasOne(item => item.Event)
                    .WithMany(evt => evt.AttendanceWindows)
                    .HasForeignKey(item => item.EventId)
                    .OnDelete(DeleteBehavior.Cascade);
                // The archive side of the same row. Cascade so deleting a closed event's history
                // cleans up its windows rather than orphaning them with both FKs null.
                entity.HasOne(item => item.EventHistory)
                    .WithMany(history => history.AttendanceWindows)
                    .HasForeignKey(item => item.EventHistoryId)
                    .OnDelete(DeleteBehavior.Cascade);
                // Postgres treats NULLs as distinct, so the archived rows (EventId null) don't
                // collide with each other here even when they share a SequenceNumber.
                entity.HasIndex(item => new { item.EventId, item.SequenceNumber }).IsUnique();
                // The only archive read path: "the windows for closed event H", in sequence.
                entity.HasIndex(item => new { item.EventHistoryId, item.SequenceNumber });
                // At most one closing window per camp, live or archived: the close bonus is paid
                // to that window. A kill roster is never the close. ClosingWindowMark moves the
                // tick in two saves so these never refuse a move. OneCloseOneRouteOneInvite skips
                // them with a warning if a camp already has two (the owner fixes those by hand).
                entity.HasIndex(item => item.EventId, "IX_EventAttendanceWindows_OneClosePerEvent")
                    .IsUnique()
                    .HasFilter("\"IsClosingWindow\" AND NOT \"IsKillWindow\"");
                entity.HasIndex(item => item.EventHistoryId, "IX_EventAttendanceWindows_OneClosePerEventHistory")
                    .IsUnique()
                    .HasFilter("\"IsClosingWindow\" AND NOT \"IsKillWindow\"");
                // Exactly one parent: the live camp OR its archive, never both and never neither.
                // A window with neither is invisible on every page. Both close paths
                // (EventEndService.EndEventCoreAsync and the HNM review handoff) set the history
                // and clear the event in the same save, so a correct write never trips this.
                // AddOneParentCheckConstraints added it NOT VALID (old rows were not re-checked at
                // startup); NormalizeNamesForSquash validated it once 0 rows broke it.
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_EventAttendanceWindows_OneParent",
                    "num_nonnulls(\"EventId\", \"EventHistoryId\") = 1"));
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
                // UNIQUE: one credit per person per window. The posts check this before inserting,
                // but two posts landing at the same moment both pass that check, so the database
                // has to be the one that says no (see UniqueRaceRetry). Null
                // AppUserIds never collide in Postgres, so legacy rows without one are unaffected.
                entity.HasIndex(item => new { item.EventAttendanceWindowId, item.AppUserId }).IsUnique();
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
                entity.HasIndex(item => new { item.LinkshellId, item.CapturedAtUtc });
                entity.HasIndex(item => item.WindowEventId);
                // The Pending review sweep: every surface that lists snapshots awaiting an
                // officer's Confirm filters on exactly this pair.
                entity.HasIndex(item => new { item.LinkshellId, item.SnapshotStatus });
                entity.Property(item => item.SnapshotStatus)
                    .HasMaxLength(32)
                    .HasDefaultValue(AttendanceSnapshotStatuses.Active);
                // Defaulted in the DATABASE, not just in the model. HnmCampReviewHandoffService
                // builds its End-Camp snapshot inline and never sets this, and so does any row
                // written before the column existed — without the default those would read as
                // Misc and be paid the misc rate for a camp roster nobody classified.
                entity.Property(item => item.SlotKind)
                    .HasMaxLength(16)
                    .HasDefaultValue(AttendanceSnapshotSlotKinds.Window);
                entity.Property(item => item.PostedByAppUserId).HasMaxLength(450);
                entity.Property(item => item.VerifiedByAppUserId).HasMaxLength(450);
                // Closed lists (see ClosedListCheck). Added NOT VALID by the migration, so rows saved
                // before it were not checked; NormalizeNamesForSquash validated them later.
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint(
                        "CK_AttendanceSnapshots_SnapshotStatus",
                        ClosedListCheck("SnapshotStatus",
                            AttendanceSnapshotStatuses.Active,
                            AttendanceSnapshotStatuses.Ignored,
                            AttendanceSnapshotStatuses.Pending));
                    table.HasCheckConstraint(
                        "CK_AttendanceSnapshots_SlotKind",
                        ClosedListCheck("SlotKind", AttendanceSnapshotSlotKinds.All.ToArray()));
                });
            });
            builder.Entity<LinkshellPresence>(entity =>
            {
                entity.ToTable("LinkshellPresences");
                entity.Property(item => item.CharacterName).HasMaxLength(256).IsRequired();
                entity.Property(item => item.MainCharacterName).HasMaxLength(256);
                entity.Property(item => item.AllianceKey).HasMaxLength(256);
                entity.Property(item => item.MainJob).HasMaxLength(8);
                entity.Property(item => item.SubJob).HasMaxLength(8);
                // THE upsert key. A character is in exactly one place at a time no matter which of
                // their alliance-mates reported them, so a second reporter must overwrite the row
                // rather than add a duplicate that would list them twice in the Lobby.
                entity.HasIndex(item => new { item.LinkshellId, item.CharacterName }).IsUnique();
                // The fresh sweep: every read is "this linkshell, seen in the last N seconds".
                entity.HasIndex(item => new { item.LinkshellId, item.LastSeenUtc });
                // Cascade, unlike the snapshot FKs: presence is a cache of the present with no
                // evidentiary value, so it has no reason to outlive its linkshell.
                entity.HasOne(item => item.Linkshell)
                    .WithMany()
                    .HasForeignKey(item => item.LinkshellId)
                    .OnDelete(DeleteBehavior.Cascade);
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
                // SetNull for the same reason: the archive is written at End Camp, and deleting
                // a Past Event must not destroy a review row whose DKP has not been posted yet.
                entity.HasOne(item => item.CampEventHistory)
                    .WithMany()
                    .HasForeignKey(item => item.CampEventHistoryId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => item.CampEventHistoryId);
                // Closed lists (see ClosedListCheck), added NOT VALID and validated later. EntryType may be null.
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint(
                        "CK_WindowEvents_Status",
                        ClosedListCheck("Status",
                            WindowEventStatuses.Open,
                            WindowEventStatuses.Closed,
                            WindowEventStatuses.Archived));
                    table.HasCheckConstraint(
                        "CK_WindowEvents_EntryType",
                        "\"EntryType\" IS NULL OR "
                        + ClosedListCheck("EntryType", WindowEventEntryTypes.All.ToArray()));
                });
            });
            // Only the closed-list check is configured here; everything else on Linkshell comes from
            // its attributes. Added NOT VALID and validated later, like the two above.
            builder.Entity<Linkshell>(entity =>
            {
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_Linkshells_HnmAttendanceMode",
                    ClosedListCheck("HnmAttendanceMode", HnmAttendanceModes.All.ToArray())));
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
                // SetNull for the same reason as the Event above: deleting the past event must
                // not erase the record of a lottery that really happened.
                entity.HasOne(item => item.EventHistory)
                    .WithMany()
                    .HasForeignKey(item => item.EventHistoryId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(item => new { item.LinkshellId, item.CapturedAtUtc });
                // Derived column (AddCampSpawnKeys), stamped by StampDerivedColumns.
                entity.HasIndex(item => new { item.LinkshellId, item.SpawnKey });
                // UNIQUE: one capture per live camp. Every officer's addon reads the same lottery off
                // chat, and each post joins the camp's capture rather than adding another. The
                // database enforces it because those posts land at the same moment (UniqueRaceRetry).
                // Captures with no camp have a null EventId, and nulls never collide in Postgres.
                entity.HasIndex(item => item.EventId).IsUnique();
                entity.HasIndex(item => item.EventHistoryId);
            });

            builder.Entity<ClaimShieldCaptureMember>(entity =>
            {
                entity.ToTable("ClaimShieldCaptureMembers");
                entity.Property(item => item.CharacterName).HasMaxLength(256).IsRequired();
                entity.Property(item => item.AppUserId).HasMaxLength(450);
                entity.Property(item => item.ActionMessage).HasMaxLength(512);
                // UNIQUE: one row per person per capture, by account and by name. Two posts joining
                // the same capture at once both read "Dan isn't on it yet"; these make the later one
                // retry instead of adding Dan twice. An unmatched name has a null AppUserId, which
                // never collides, so it is held to one row by the name index alone.
                entity.HasIndex(item => new { item.CaptureId, item.AppUserId }).IsUnique();
                entity.HasIndex(item => new { item.CaptureId, item.CharacterName }).IsUnique();
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
                // One route per linkshell for each one-owner post type (events may go to several).
                // ChannelRouteEditor checks this and saves in two steps, so a move is never refused.
                entity.HasIndex(item => item.LinkshellId, "IX_LinkshellChannelRoutes_OneLootRoute")
                    .IsUnique()
                    .HasFilter("\"PostLoot\"");
                entity.HasIndex(item => item.LinkshellId, "IX_LinkshellChannelRoutes_OneAuctionRoute")
                    .IsUnique()
                    .HasFilter("\"PostAuctions\"");
                entity.HasIndex(item => item.LinkshellId, "IX_LinkshellChannelRoutes_OneTodBoardRoute")
                    .IsUnique()
                    .HasFilter("\"PostTodBoard\"");
                entity.HasIndex(item => item.LinkshellId, "IX_LinkshellChannelRoutes_OneDkpSheetRoute")
                    .IsUnique()
                    .HasFilter("\"PostDkpSheet\"");
            });


            builder.Entity<Auction>()
                .Property(item => item.DiscordChannelId).HasMaxLength(32);
        }

        // SQL for a CHECK that a status/type column holds one of a fixed set of values, built from
        // the same constants the code writes, so the two cannot drift apart. For example
        // ClosedListCheck("SlotKind", "Window", "Misc") gives "SlotKind" IN ('Window', 'Misc').
        // Adding a new value to one of these lists needs a migration that replaces the constraint.
        private static string ClosedListCheck(string column, params string[] values)
            => $"\"{column}\" IN ({string.Join(", ", values.Select(value => $"'{value}'"))})";
    }
}
