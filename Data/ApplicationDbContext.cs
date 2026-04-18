using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Data
{
    public class ApplicationDbContext : IdentityDbContext<AppUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
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
        public DbSet<Notification> Notifications => Set<Notification>();

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
            });
        }
    }
}
