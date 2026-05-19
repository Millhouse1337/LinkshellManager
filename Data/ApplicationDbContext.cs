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
        private readonly DiscordDkpSpendQueue? _dkpSpendQueue;
        private readonly DiscordAuctionChannelQueue? _auctionChannelQueue;

        public ApplicationDbContext(
            DbContextOptions<ApplicationDbContext> options,
            DiscordTodBoardQueue? todBoardQueue = null,
            DiscordDkpSpendQueue? dkpSpendQueue = null,
            DiscordAuctionChannelQueue? auctionChannelQueue = null)
            : base(options)
        {
            _todBoardQueue = todBoardQueue;
            _dkpSpendQueue = dkpSpendQueue;
            _auctionChannelQueue = auctionChannelQueue;
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

        // New DKP-spend ledger rows (any negative Amount — auction win,
        // loot-edit, negative adjustment/audit). Captured pre-save as entity
        // references because the db-generated Id isn't known until after the
        // commit; the id is read and enqueued only on success so the DKP
        // spend log fires from every spend path without per-controller wiring.
        private List<DkpLedgerEntry> CollectAddedDkpSpends()
        {
            if (_dkpSpendQueue is null)
            {
                return new List<DkpLedgerEntry>();
            }
            var spends = new List<DkpLedgerEntry>();
            foreach (var entry in ChangeTracker.Entries<DkpLedgerEntry>())
            {
                if (entry.State == EntityState.Added
                    && entry.Entity.Amount < 0
                    && entry.Entity.LinkshellId > 0)
                {
                    spends.Add(entry.Entity);
                }
            }
            return spends;
        }

        private void EnqueueDkpSpends(IReadOnlyList<DkpLedgerEntry> spends)
        {
            if (_dkpSpendQueue is null)
            {
                return;
            }
            foreach (var spend in spends)
            {
                if (spend.Id > 0)
                {
                    _dkpSpendQueue.Enqueue(spend.Id);
                }
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

        public override async Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            var affected = CollectChangedTodLinkshellIds();
            var dkpSpends = CollectAddedDkpSpends();
            var (createdAuctions, closedAuctions) = CollectAuctionChannelWork();
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            EnqueueTodBoardRefreshes(affected);
            EnqueueDkpSpends(dkpSpends);
            EnqueueAuctionChannelWork(createdAuctions, closedAuctions);
            return result;
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            var affected = CollectChangedTodLinkshellIds();
            var dkpSpends = CollectAddedDkpSpends();
            var (createdAuctions, closedAuctions) = CollectAuctionChannelWork();
            var result = base.SaveChanges(acceptAllChangesOnSuccess);
            EnqueueTodBoardRefreshes(affected);
            EnqueueDkpSpends(dkpSpends);
            EnqueueAuctionChannelWork(createdAuctions, closedAuctions);
            return result;
        }

        public DbSet<DiscordActivityUser> DiscordActivityUsers => Set<DiscordActivityUser>();
        public DbSet<Linkshell> Linkshells => Set<Linkshell>();
        public DbSet<AppUserLinkshell> AppUserLinkshells => Set<AppUserLinkshell>();
        public DbSet<Invite> Invites => Set<Invite>();
        public DbSet<Auction> Auctions => Set<Auction>();
        public DbSet<AuctionItem> AuctionItems => Set<AuctionItem>();
        public DbSet<Bid> Bids => Set<Bid>();
        public DbSet<AuctionHistory> AuctionHistories => Set<AuctionHistory>();
        public DbSet<Event> Events => Set<Event>();
        public DbSet<Job> Jobs => Set<Job>();
        public DbSet<AppUserEvent> AppUserEvents => Set<AppUserEvent>();
        public DbSet<AppUserEventStatusLedger> AppUserEventStatusLedgers => Set<AppUserEventStatusLedger>();
        public DbSet<DkpLedgerEntry> DkpLedgerEntries => Set<DkpLedgerEntry>();
        public DbSet<EventHistory> EventHistories => Set<EventHistory>();
        public DbSet<AppUserEventHistory> AppUserEventHistories => Set<AppUserEventHistory>();
        public DbSet<EventLootDetail> EventLootDetails => Set<EventLootDetail>();
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
        public DbSet<LinkshellRole> LinkshellRoles => Set<LinkshellRole>();
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

            builder.Entity<Job>(entity =>
            {
                entity.Property(job => job.Enlisted).HasColumnType("text[]");
            });

            builder.Entity<Invite>(entity =>
            {
                entity.Property(invite => invite.AppUserId).HasMaxLength(450).IsRequired();
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
                entity.HasOne(item => item.AppUserEvent)
                    .WithMany(aue => aue.AttendedWindows)
                    .HasForeignKey(item => item.AppUserEventId)
                    .OnDelete(DeleteBehavior.Cascade);
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
                entity.HasIndex(item => new { item.LinkshellId, item.Status, item.NormalizedName });
                entity.HasIndex(item => new { item.LinkshellId, item.LastCapturedAtUtc });
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
                entity.HasIndex(item => new { item.LinkshellId, item.CapturedAtUtc });
            });

            builder.Entity<ClaimShieldCaptureMember>(entity =>
            {
                entity.ToTable("ClaimShieldCaptureMembers");
                entity.Property(item => item.CharacterName).HasMaxLength(256).IsRequired();
                entity.Property(item => item.AppUserId).HasMaxLength(450);
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


            builder.Entity<Auction>()
                .Property(item => item.DiscordChannelId).HasMaxLength(32);
            builder.Entity<AuctionHistory>()
                .Property(item => item.DiscordChannelId).HasMaxLength(32);
        }
    }
}
