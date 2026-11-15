using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // The whole schema in one migration (Round 4, D11: the ops-17 squash). It replaced the 173
    // migrations from 20260607213956_InitialCreate to 20261115161909_NormalizeNamesForSquash, which
    // stay in git history (commit 4b05ee2 is the last build that has them).
    //
    // A fresh database gets exactly the schema the old chain built: the generated tables, indexes,
    // foreign keys and checks, plus ChainOnlyDefaultsSql below.
    // tests/LinkshellManager.PostgresTests/Snapshots/SchemaCatalog.txt (SchemaCatalogGoldenTests) is
    // the proof: it was written from a chain-built database and was not changed by the squash.
    //
    // A database the old chain built (production) never runs this Up. At start-up,
    // MigrationBaseline.AdoptAsync sees the old chain's last id in __EFMigrationsHistory and adds
    // this migration's row, so EF treats it as applied. See docs/migration-squash.md.
    //
    // Down drops everything, like any first migration. Never run it on a real database.
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        // Column defaults the old chain added (AddColumn with a defaultValue, so old rows got a value)
        // that the model does not declare. EF always sends these columns on insert, so the app does
        // not need them, but a chain-built database (production) has them, and raw-SQL inserts in
        // tools and tests may rely on them. Kept so a fresh database matches production exactly.
        // Adding HasDefaultValue to the model instead would change how EF inserts these columns.
        public const string ChainOnlyDefaultsSql = """
            ALTER TABLE "AppUserEventHistories" ALTER COLUMN "ActiveCredit" SET DEFAULT true;
            ALTER TABLE "AppUserEvents" ALTER COLUMN "EnfeebReady" SET DEFAULT false;
            ALTER TABLE "AppUserEvents" ALTER COLUMN "RelicWeapon" SET DEFAULT false;
            ALTER TABLE "AppUserEvents" ALTER COLUMN "ResistReady" SET DEFAULT false;
            ALTER TABLE "AppUserEvents" ALTER COLUMN "WithdrewFromEvent" SET DEFAULT false;
            ALTER TABLE "AppUserLinkshells" ALTER COLUMN "DiscordIdConfirmed" SET DEFAULT false;
            ALTER TABLE "AppUserLinkshells" ALTER COLUMN "DkpPoolLedgerFromId" SET DEFAULT 0;
            ALTER TABLE "AspNetUsers" ALTER COLUMN "IsPlaceholder" SET DEFAULT false;
            ALTER TABLE "AttendanceSnapshotEntries" ALTER COLUMN "AddedManually" SET DEFAULT false;
            ALTER TABLE "DkpLedgerEntries" ALTER COLUMN "DkpPoolPinned" SET DEFAULT false;
            ALTER TABLE "EventAttendanceWindows" ALTER COLUMN "IsClosingWindow" SET DEFAULT false;
            ALTER TABLE "EventAttendanceWindows" ALTER COLUMN "IsKillWindow" SET DEFAULT false;
            ALTER TABLE "EventHistories" ALTER COLUMN "CountsTowardActive" SET DEFAULT true;
            ALTER TABLE "EventPartySlotSignups" ALTER COLUMN "EnfeebReady" SET DEFAULT false;
            ALTER TABLE "EventPartySlotSignups" ALTER COLUMN "IsAllianceLeader" SET DEFAULT false;
            ALTER TABLE "EventPartySlotSignups" ALTER COLUMN "IsPartyLeader" SET DEFAULT false;
            ALTER TABLE "EventPartySlotSignups" ALTER COLUMN "RelicWeapon" SET DEFAULT false;
            ALTER TABLE "EventPartySlotSignups" ALTER COLUMN "ResistReady" SET DEFAULT false;
            ALTER TABLE "EventPartySlotSignups" ALTER COLUMN "StayNextWindow" SET DEFAULT false;
            ALTER TABLE "Events" ALTER COLUMN "CountsTowardActive" SET DEFAULT true;
            ALTER TABLE "Events" ALTER COLUMN "HnmWindowNumber" SET DEFAULT 1;
            ALTER TABLE "Events" ALTER COLUMN "WdClaimed" SET DEFAULT false;
            ALTER TABLE "Events" ALTER COLUMN "WdKilled" SET DEFAULT false;
            ALTER TABLE "Items" ALTER COLUMN "IsSold" SET DEFAULT false;
            ALTER TABLE "JobRatings" ALTER COLUMN "CharacterSlot" SET DEFAULT 0;
            ALTER TABLE "LinkshellChannelRoutes" ALTER COLUMN "PostDkpSheet" SET DEFAULT false;
            ALTER TABLE "LinkshellMonsterTimings" ALTER COLUMN "AdditionalSeconds" SET DEFAULT 0;
            ALTER TABLE "LinkshellMonsterTimings" ALTER COLUMN "ClaimShieldEnabled" SET DEFAULT true;
            ALTER TABLE "LinkshellRoles" ALTER COLUMN "CanBid" SET DEFAULT true;
            ALTER TABLE "LinkshellRoles" ALTER COLUMN "CanManageAttendanceWithAddon" SET DEFAULT false;
            ALTER TABLE "LinkshellRoles" ALTER COLUMN "CanManageCharts" SET DEFAULT false;
            ALTER TABLE "LinkshellRoles" ALTER COLUMN "CanUseAddon" SET DEFAULT false;
            ALTER TABLE "Linkshells" ALTER COLUMN "ActiveAfterAttendances" SET DEFAULT 2;
            ALTER TABLE "Linkshells" ALTER COLUMN "EnableActivityTracking" SET DEFAULT false;
            ALTER TABLE "Linkshells" ALTER COLUMN "HnmAttendanceMode" SET DEFAULT 'Standard';
            ALTER TABLE "Linkshells" ALTER COLUMN "HnmStandardClaimBonus" SET DEFAULT 0.0;
            ALTER TABLE "Linkshells" ALTER COLUMN "HnmStandardCloseBonus" SET DEFAULT 0.0;
            ALTER TABLE "Linkshells" ALTER COLUMN "HnmStandardKillBonus" SET DEFAULT 0.0;
            ALTER TABLE "Linkshells" ALTER COLUMN "HnmStandardMiscBonus" SET DEFAULT 0.0;
            ALTER TABLE "Linkshells" ALTER COLUMN "HnmStandardOpenBonus" SET DEFAULT 0.0;
            ALTER TABLE "Linkshells" ALTER COLUMN "HnmStandardWindowBonus" SET DEFAULT 0.0;
            ALTER TABLE "Linkshells" ALTER COLUMN "InactiveAfterAbsences" SET DEFAULT 3;
            ALTER TABLE "Linkshells" ALTER COLUMN "LockToDiscordGuild" SET DEFAULT false;
            ALTER TABLE "Linkshells" ALTER COLUMN "OutsidePartySignupEnabled" SET DEFAULT false;
            ALTER TABLE "Linkshells" ALTER COLUMN "WdClaimBonus" SET DEFAULT 0.0;
            ALTER TABLE "Linkshells" ALTER COLUMN "WdCloseBonus" SET DEFAULT 0.0;
            ALTER TABLE "Linkshells" ALTER COLUMN "WdDkpPerWindow" SET DEFAULT 0.25;
            ALTER TABLE "Linkshells" ALTER COLUMN "WdKillBonus" SET DEFAULT 0.0;
            ALTER TABLE "Linkshells" ALTER COLUMN "WdOpenBonus" SET DEFAULT 0.0;
            ALTER TABLE "PendingTodSubmissions" ALTER COLUMN "AdditionalSeconds" SET DEFAULT 0;
            ALTER TABLE "PendingTodSubmissions" ALTER COLUMN "Hq" SET DEFAULT false;
            ALTER TABLE "Tods" ALTER COLUMN "AdditionalSeconds" SET DEFAULT 0;
            ALTER TABLE "Tods" ALTER COLUMN "Hq" SET DEFAULT false;
            ALTER TABLE "WindowEvents" ALTER COLUMN "PerCaptureDkp" SET DEFAULT false;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    CharacterName = table.Column<string>(type: "text", nullable: true),
                    JobLevels = table.Column<string>(type: "jsonb", nullable: true),
                    StrongJobs = table.Column<string>(type: "jsonb", nullable: true),
                    MeritJobs = table.Column<string>(type: "jsonb", nullable: true),
                    AltCharacterName1 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AltCharacterName2 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Alt1JobLevels = table.Column<string>(type: "jsonb", nullable: true),
                    Alt2JobLevels = table.Column<string>(type: "jsonb", nullable: true),
                    Alt1StrongJobs = table.Column<string>(type: "jsonb", nullable: true),
                    Alt2StrongJobs = table.Column<string>(type: "jsonb", nullable: true),
                    Alt1MeritJobs = table.Column<string>(type: "jsonb", nullable: true),
                    Alt2MeritJobs = table.Column<string>(type: "jsonb", nullable: true),
                    CraftLevels = table.Column<string>(type: "jsonb", nullable: true),
                    Alt1CraftLevels = table.Column<string>(type: "jsonb", nullable: true),
                    Alt2CraftLevels = table.Column<string>(type: "jsonb", nullable: true),
                    TimeZone = table.Column<string>(type: "text", nullable: true),
                    PrimaryLinkshellId = table.Column<int>(type: "integer", nullable: true),
                    IsSuperAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    IsPlaceholder = table.Column<bool>(type: "boolean", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LoginProvider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Characters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Slot = table.Column<short>(type: "smallint", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    JobLevels = table.Column<string>(type: "jsonb", nullable: true),
                    StrongJobs = table.Column<string>(type: "jsonb", nullable: true),
                    MeritJobs = table.Column<string>(type: "jsonb", nullable: true),
                    CraftLevels = table.Column<string>(type: "jsonb", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Characters", x => x.Id);
                    table.CheckConstraint("CK_Characters_Name_NotBlank", "\"Name\" ~ '[^ \\t\\n\\v\\f\\r\\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000]'");
                    table.CheckConstraint("CK_Characters_Slot", "\"Slot\" IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_Characters_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscordActivityUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DiscordUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Username = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GlobalName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Avatar = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IdentityUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordActivityUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscordActivityUsers_AspNetUsers_IdentityUserId",
                        column: x => x.IdentityUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Linkshells",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<string>(type: "text", nullable: true),
                    LinkshellName = table.Column<string>(type: "text", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    LootStructure = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DkpRoundingIncrement = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EnableHnmSection = table.Column<bool>(type: "boolean", nullable: false),
                    HnmAttendanceMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WdDkpPerWindow = table.Column<double>(type: "double precision", nullable: false),
                    WdClaimBonus = table.Column<double>(type: "double precision", nullable: false),
                    WdKillBonus = table.Column<double>(type: "double precision", nullable: false),
                    WdOpenBonus = table.Column<double>(type: "double precision", nullable: false),
                    WdCloseBonus = table.Column<double>(type: "double precision", nullable: false),
                    HnmStandardOpenBonus = table.Column<double>(type: "double precision", nullable: false),
                    HnmStandardCloseBonus = table.Column<double>(type: "double precision", nullable: false),
                    HnmStandardClaimBonus = table.Column<double>(type: "double precision", nullable: false),
                    HnmStandardKillBonus = table.Column<double>(type: "double precision", nullable: false),
                    HnmStandardWindowBonus = table.Column<double>(type: "double precision", nullable: false),
                    HnmStandardMiscBonus = table.Column<double>(type: "double precision", nullable: false),
                    EnableAuctions = table.Column<bool>(type: "boolean", nullable: false),
                    AuctionsLocked = table.Column<bool>(type: "boolean", nullable: false),
                    EnableToDs = table.Column<bool>(type: "boolean", nullable: false),
                    EnableEndgame = table.Column<bool>(type: "boolean", nullable: false),
                    EnableEvents = table.Column<bool>(type: "boolean", nullable: false),
                    EnableDkp = table.Column<bool>(type: "boolean", nullable: false),
                    EnableTreasury = table.Column<bool>(type: "boolean", nullable: false),
                    EnableActivityTracking = table.Column<bool>(type: "boolean", nullable: false),
                    InactiveAfterAbsences = table.Column<int>(type: "integer", nullable: false),
                    ActiveAfterAttendances = table.Column<int>(type: "integer", nullable: false),
                    OutsidePartySignupEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    HiddenTodMonsters = table.Column<string>(type: "text", nullable: false),
                    DiscordGuildId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DiscordGuildName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DiscussionChannelId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LockToDiscordGuild = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Linkshells", x => x.Id);
                    table.CheckConstraint("CK_Linkshells_HnmAttendanceMode", "\"HnmAttendanceMode\" IN ('Standard', 'Wd')");
                    table.ForeignKey(
                        name: "FK_Linkshells_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AddonApiTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    IssuedToAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TokenPrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PairingBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AddonApiTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AddonApiTokens_AspNetUsers_IssuedToAppUserId",
                        column: x => x.IssuedToAppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AddonApiTokens_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AddonPairingCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    IssuedToAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AddonPairingCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AddonPairingCodes_AspNetUsers_IssuedToAppUserId",
                        column: x => x.IssuedToAppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AddonPairingCodes_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Announcements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    AnnouncementTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AnnouncementDetails = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Announcements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Announcements_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuctionHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    AuctionTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuctionHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuctionHistories_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChartMemberKeyItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Board = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    KeyItemName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    SetByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    SetByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChartMemberKeyItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChartMemberKeyItems_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChartMemberKeyItems_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChartPopItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Board = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Boss = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, defaultValue: "Pop"),
                    ItemName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    HeldByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChartPopItems", x => x.Id);
                    table.CheckConstraint("CK_ChartPopItems_Kind", "\"Kind\" IN ('Pop','Drop')");
                    table.CheckConstraint("CK_ChartPopItems_Quantity_NonNegative", "\"Quantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_ChartPopItems_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChartWishlistRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Board = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ItemName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Pending"),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    RequestedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    RequestedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FulfilledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FulfilledByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    FulfilledByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChartWishlistRequests", x => x.Id);
                    table.CheckConstraint("CK_ChartWishlistRequests_Quantity_Positive", "\"Quantity\" >= 1");
                    table.CheckConstraint("CK_ChartWishlistRequests_Status", "\"Status\" IN ('Pending','Fulfilled')");
                    table.ForeignKey(
                        name: "FK_ChartWishlistRequests_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DkpPools",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Accent = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DkpPools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DkpPools_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Invites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DiscordUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DiscordDisplayName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invites_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Invites_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Items",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    ItemName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ItemType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsSold = table.Column<bool>(type: "boolean", nullable: false),
                    SoldPrice = table.Column<long>(type: "bigint", nullable: true),
                    SoldAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SoldByCharacterName = table.Column<string>(type: "text", nullable: true),
                    JournalEntryId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Items_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobRatings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    CharacterSlot = table.Column<int>(type: "integer", nullable: false),
                    TargetAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    RaterAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    JobIndex = table.Column<int>(type: "integer", nullable: false),
                    Gear = table.Column<int>(type: "integer", nullable: false),
                    Skill = table.Column<int>(type: "integer", nullable: false),
                    HasRelic = table.Column<bool>(type: "boolean", nullable: false),
                    RelicNames = table.Column<string>(type: "jsonb", nullable: true),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRatings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobRatings_AspNetUsers_RaterAppUserId",
                        column: x => x.RaterAppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobRatings_AspNetUsers_TargetAppUserId",
                        column: x => x.TargetAppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobRatings_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JournalEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    LinkshellName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    EntryNumber = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    TransactionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TransactionKind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    Memo = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ReversesJournalEntryId = table.Column<int>(type: "integer", nullable: true),
                    CorrectionReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ConfirmedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SourceItemId = table.Column<int>(type: "integer", nullable: true),
                    LegacyEntryType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LegacyCategory = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntries", x => x.Id);
                    table.CheckConstraint("CK_JournalEntries_ReasonRequiredForFixes", "\"Kind\" NOT IN ('Reversal', 'Correction') OR (\"CorrectionReason\" IS NOT NULL AND length(btrim(\"CorrectionReason\")) > 0)");
                    table.ForeignKey(
                        name: "FK_JournalEntries_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LedgerAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    AccountNumber = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    IsCash = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerAccounts", x => x.Id);
                    table.CheckConstraint("CK_LedgerAccounts_AccountNumber_Range", "\"AccountNumber\" BETWEEN 1000 AND 5999");
                    table.ForeignKey(
                        name: "FK_LedgerAccounts_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LinkshellBanners",
                columns: table => new
                {
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    ImageData = table.Column<byte[]>(type: "bytea", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellBanners", x => x.LinkshellId);
                    table.ForeignKey(
                        name: "FK_LinkshellBanners_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LinkshellChannelRoutes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ChannelId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChannelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PostEvents = table.Column<bool>(type: "boolean", nullable: false),
                    PostLoot = table.Column<bool>(type: "boolean", nullable: false),
                    PostAuctions = table.Column<bool>(type: "boolean", nullable: false),
                    PostTodBoard = table.Column<bool>(type: "boolean", nullable: false),
                    PostDkpSheet = table.Column<bool>(type: "boolean", nullable: false),
                    EventTypeFilter = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    HnmMonsterFilter = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TodBoardMessageId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DkpSheetMessageId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DkpSheetExtraMessageIds = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellChannelRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellChannelRoutes_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LinkshellMonsterTimings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    MonsterName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NormalizedMonsterName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    WindowCount = table.Column<int>(type: "integer", nullable: true),
                    WindowCadenceMinutes = table.Column<int>(type: "integer", nullable: true),
                    CooldownMinutes = table.Column<int>(type: "integer", nullable: false),
                    AdditionalSeconds = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    IsCustom = table.Column<bool>(type: "boolean", nullable: false),
                    ClaimShieldEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    WindowMethod = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellMonsterTimings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellMonsterTimings_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LinkshellPresences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MainCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ZoneId = table.Column<int>(type: "integer", nullable: true),
                    AllianceNumber = table.Column<int>(type: "integer", nullable: false),
                    AllianceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsAllianceLeader = table.Column<bool>(type: "boolean", nullable: false),
                    MainJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    MainJobLevel = table.Column<int>(type: "integer", nullable: true),
                    SubJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SubJobLevel = table.Column<int>(type: "integer", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellPresences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellPresences_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LinkshellRoles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CanManageRoles = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageMembers = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageEvents = table.Column<bool>(type: "boolean", nullable: false),
                    CanModerateLiveEvent = table.Column<bool>(type: "boolean", nullable: false),
                    CanAddLoot = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageInventory = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageCharts = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageTreasury = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageRules = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageAnnouncements = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageTods = table.Column<bool>(type: "boolean", nullable: false),
                    CanAuditDkp = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageAuctions = table.Column<bool>(type: "boolean", nullable: false),
                    CanLockAuctions = table.Column<bool>(type: "boolean", nullable: false),
                    CanCustomizeLinkshell = table.Column<bool>(type: "boolean", nullable: false),
                    CanSubmitTodForApproval = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageParties = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageInvites = table.Column<bool>(type: "boolean", nullable: false),
                    CanBid = table.Column<bool>(type: "boolean", nullable: false),
                    CanUseAddon = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageAttendanceWithAddon = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellRoles", x => x.Id);
                    table.UniqueConstraint("AK_LinkshellRoles_LinkshellId_Id", x => new { x.LinkshellId, x.Id });
                    table.ForeignKey(
                        name: "FK_LinkshellRoles_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingTodSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    SubmittedByAppUserId = table.Column<string>(type: "text", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MonsterName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DayNumber = table.Column<int>(type: "integer", nullable: true),
                    Claim = table.Column<bool>(type: "boolean", nullable: true),
                    Hq = table.Column<bool>(type: "boolean", nullable: false),
                    AdditionalSeconds = table.Column<int>(type: "integer", nullable: false),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Cooldown = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Interval = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RepopTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImagePath = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PopWindow = table.Column<int>(type: "integer", nullable: true),
                    CooldownMinutes = table.Column<int>(type: "integer", nullable: true),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: true),
                    SpawnKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingTodSubmissions", x => x.Id);
                    table.CheckConstraint("CK_PendingTodSubmissions_CooldownMinutes_Positive", "\"CooldownMinutes\" IS NULL OR \"CooldownMinutes\" > 0");
                    table.CheckConstraint("CK_PendingTodSubmissions_IntervalMinutes_Positive", "\"IntervalMinutes\" IS NULL OR \"IntervalMinutes\" > 0");
                    table.ForeignKey(
                        name: "FK_PendingTodSubmissions_AspNetUsers_SubmittedByAppUserId",
                        column: x => x.SubmittedByAppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PendingTodSubmissions_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlaceholderClaimRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    PlaceholderAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    RequesterAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    RequesterDiscordUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RequesterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Approved = table.Column<bool>(type: "boolean", nullable: true),
                    ResolvedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaceholderClaimRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaceholderClaimRequests_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Rules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    RuleTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RuleDetails = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rules_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    MonsterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DayNumber = table.Column<int>(type: "integer", nullable: true),
                    PopWindow = table.Column<int>(type: "integer", nullable: true),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Claim = table.Column<bool>(type: "boolean", nullable: true),
                    Hq = table.Column<bool>(type: "boolean", nullable: false),
                    Killed = table.Column<bool>(type: "boolean", nullable: true),
                    AdditionalSeconds = table.Column<int>(type: "integer", nullable: false),
                    Cooldown = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RepopTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Interval = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TimeStamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImagePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CooldownMinutes = table.Column<int>(type: "integer", nullable: true),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: true),
                    SpawnKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tods", x => x.Id);
                    table.CheckConstraint("CK_Tods_CooldownMinutes_Positive", "\"CooldownMinutes\" IS NULL OR \"CooldownMinutes\" > 0");
                    table.CheckConstraint("CK_Tods_IntervalMinutes_Positive", "\"IntervalMinutes\" IS NULL OR \"IntervalMinutes\" > 0");
                    table.ForeignKey(
                        name: "FK_Tods_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChartPopItemCredits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChartPopItemId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreditedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreditedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChartPopItemCredits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChartPopItemCredits_ChartPopItems_ChartPopItemId",
                        column: x => x.ChartPopItemId,
                        principalTable: "ChartPopItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChartWishlistRequestZones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequestId = table.Column<int>(type: "integer", nullable: false),
                    Boss = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChartWishlistRequestZones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChartWishlistRequestZones_ChartWishlistRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "ChartWishlistRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Auctions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    AuctionTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DkpPoolId = table.Column<int>(type: "integer", nullable: true),
                    DiscordChannelId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DiscordMessageId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DiscordMessageIds = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Auctions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Auctions_DkpPools_DkpPoolId",
                        column: x => x.DkpPoolId,
                        principalTable: "DkpPools",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Auctions_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DkpPoolEventTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    DkpPoolId = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEventType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DkpPoolEventTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DkpPoolEventTypes_DkpPools_DkpPoolId",
                        column: x => x.DkpPoolId,
                        principalTable: "DkpPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DkpPoolEventTypes_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JournalEntryLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JournalEntryId = table.Column<int>(type: "integer", nullable: false),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    LedgerAccountId = table.Column<int>(type: "integer", nullable: false),
                    AccountNumber = table.Column<int>(type: "integer", nullable: false),
                    AccountName = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    TransactionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    CounterpartyAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CounterpartyCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    HolderAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    HolderCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntryLines", x => x.Id);
                    table.CheckConstraint("CK_JournalEntryLines_LineNumber_Positive", "\"LineNumber\" >= 1");
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_LedgerAccounts_LedgerAccountId",
                        column: x => x.LedgerAccountId,
                        principalTable: "LedgerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AppUserLinkshells",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<string>(type: "text", nullable: true),
                    DiscordUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DiscordIdConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "text", nullable: true),
                    Rank = table.Column<string>(type: "text", nullable: true),
                    LinkshellRoleId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: true),
                    ManualActiveCreditStreak = table.Column<int>(type: "integer", nullable: true),
                    ManualAbsentStreak = table.Column<int>(type: "integer", nullable: true),
                    ManualStreakSetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LinkshellDkp = table.Column<double>(type: "double precision", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    SeededDkpEarned = table.Column<double>(type: "double precision", nullable: false),
                    SeededDkpSpent = table.Column<double>(type: "double precision", nullable: false),
                    DkpSeedLedgerId = table.Column<int>(type: "integer", nullable: false),
                    DkpPoolLedgerFromId = table.Column<int>(type: "integer", nullable: false),
                    DateJoined = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserLinkshells", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUserLinkshells_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AppUserLinkshells_LinkshellRoles_LinkshellId_LinkshellRoleId",
                        columns: x => new { x.LinkshellId, x.LinkshellRoleId },
                        principalTable: "LinkshellRoles",
                        principalColumns: new[] { "LinkshellId", "Id" });
                    table.ForeignKey(
                        name: "FK_AppUserLinkshells_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuctionItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AuctionId = table.Column<int>(type: "integer", nullable: true),
                    AuctionHistoryId = table.Column<int>(type: "integer", nullable: true),
                    ItemName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ItemType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    StartingBidDkp = table.Column<int>(type: "integer", nullable: true),
                    CurrentHighestBid = table.Column<int>(type: "integer", nullable: true),
                    CurrentHighestBidder = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CurrentHighestBidderAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    SourceItemId = table.Column<int>(type: "integer", nullable: true),
                    GilAmount = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuctionItems", x => x.Id);
                    table.CheckConstraint("CK_AuctionItems_OneParent", "num_nonnulls(\"AuctionId\", \"AuctionHistoryId\") = 1");
                    table.ForeignKey(
                        name: "FK_AuctionItems_AuctionHistories_AuctionHistoryId",
                        column: x => x.AuctionHistoryId,
                        principalTable: "AuctionHistories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AuctionItems_Auctions_AuctionId",
                        column: x => x.AuctionId,
                        principalTable: "Auctions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AuctionItems_Items_SourceItemId",
                        column: x => x.SourceItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Bids",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AuctionItemId = table.Column<int>(type: "integer", nullable: false),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    BidAmount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bids", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bids_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Bids_AuctionItems_AuctionItemId",
                        column: x => x.AuctionItemId,
                        principalTable: "AuctionItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserEventHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<string>(type: "text", nullable: true),
                    EventHistoryId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "text", nullable: true),
                    JobName = table.Column<string>(type: "text", nullable: true),
                    SubJobName = table.Column<string>(type: "text", nullable: true),
                    JobType = table.Column<string>(type: "text", nullable: true),
                    Duration = table.Column<double>(type: "double precision", nullable: true),
                    EventDkp = table.Column<double>(type: "double precision", nullable: true),
                    ActiveCredit = table.Column<bool>(type: "boolean", nullable: false),
                    WindowsAttended = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserEventHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUserEventHistories_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AppUserEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<string>(type: "text", nullable: true),
                    DiscordUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "text", nullable: true),
                    JobName = table.Column<string>(type: "text", nullable: true),
                    SubJobName = table.Column<string>(type: "text", nullable: true),
                    JobType = table.Column<string>(type: "text", nullable: true),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Duration = table.Column<double>(type: "double precision", nullable: true),
                    IsQuickJoin = table.Column<bool>(type: "boolean", nullable: false),
                    EnfeebReady = table.Column<bool>(type: "boolean", nullable: false),
                    ResistReady = table.Column<bool>(type: "boolean", nullable: false),
                    RelicWeapon = table.Column<bool>(type: "boolean", nullable: false),
                    WdArrivalWindow = table.Column<int>(type: "integer", nullable: true),
                    WdDepartureWindow = table.Column<int>(type: "integer", nullable: true),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: true),
                    IsOnBreak = table.Column<bool>(type: "boolean", nullable: true),
                    WithdrewFromEvent = table.Column<bool>(type: "boolean", nullable: false),
                    ResumeTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUserEvents_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AppUserEventStatusLedgers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserEventId = table.Column<int>(type: "integer", nullable: false),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    ActionType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequiresVerification = table.Column<bool>(type: "boolean", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeniedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserEventStatusLedgers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUserEventStatusLedgers_AppUserEvents_AppUserEventId",
                        column: x => x.AppUserEventId,
                        principalTable: "AppUserEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserEventWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserEventId = table.Column<int>(type: "integer", nullable: true),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    MainCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EventAttendanceWindowId = table.Column<int>(type: "integer", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VerifiedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AllianceNumber = table.Column<int>(type: "integer", nullable: true),
                    AllianceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserEventWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUserEventWindows_AppUserEvents_AppUserEventId",
                        column: x => x.AppUserEventId,
                        principalTable: "AppUserEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceSnapshotEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SnapshotId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    MainJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    MainJobLevel = table.Column<int>(type: "integer", nullable: true),
                    SubJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SubJobLevel = table.Column<int>(type: "integer", nullable: true),
                    Zone = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AddedManually = table.Column<bool>(type: "boolean", nullable: false),
                    DkpAmount = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceSnapshotEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CapturedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LinkedEventId = table.Column<int>(type: "integer", nullable: true),
                    WindowEventId = table.Column<int>(type: "integer", nullable: true),
                    WindowNumber = table.Column<int>(type: "integer", nullable: true),
                    CampWindowId = table.Column<int>(type: "integer", nullable: true),
                    SlotKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Window"),
                    DkpAmount = table.Column<double>(type: "double precision", nullable: true),
                    PostSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    AllianceNumber = table.Column<int>(type: "integer", nullable: true),
                    AllianceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AllianceLeaderName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PostedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    VerifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerifiedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    SnapshotStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Active")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceSnapshots", x => x.Id);
                    table.CheckConstraint("CK_AttendanceSnapshots_SlotKind", "\"SlotKind\" IN ('Window', 'Misc')");
                    table.CheckConstraint("CK_AttendanceSnapshots_SnapshotStatus", "\"SnapshotStatus\" IN ('Active', 'Ignored', 'Pending')");
                    table.ForeignKey(
                        name: "FK_AttendanceSnapshots_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClaimShieldCaptureMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CaptureId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    Matched = table.Column<bool>(type: "boolean", nullable: false),
                    ActionMessage = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimShieldCaptureMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClaimShieldCaptures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    EventId = table.Column<int>(type: "integer", nullable: true),
                    EventHistoryId = table.Column<int>(type: "integer", nullable: true),
                    MonsterName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Won = table.Column<bool>(type: "boolean", nullable: false),
                    TotalPlayers = table.Column<int>(type: "integer", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CapturedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CapturedMessage = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimShieldCaptures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimShieldCaptures_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DkpLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<string>(type: "text", nullable: true),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    EventHistoryId = table.Column<int>(type: "integer", nullable: true),
                    EntryType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<double>(type: "double precision", nullable: false),
                    DkpPoolId = table.Column<int>(type: "integer", nullable: true),
                    DkpPoolPinned = table.Column<bool>(type: "boolean", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EventName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EventType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EventLocation = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ItemName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Details = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    EditReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SourceEventLootDetailId = table.Column<int>(type: "integer", nullable: true),
                    SourceWindowEventId = table.Column<int>(type: "integer", nullable: true),
                    AuditRelatedLedgerEntryId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DkpLedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DkpLedgerEntries_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DkpLedgerEntries_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventAttendanceWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: true),
                    EventHistoryId = table.Column<int>(type: "integer", nullable: true),
                    SequenceNumber = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PostedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SpawnWindow = table.Column<int>(type: "integer", nullable: true),
                    PostedBySource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DkpAmount = table.Column<double>(type: "double precision", nullable: true),
                    IsClosingWindow = table.Column<bool>(type: "boolean", nullable: false),
                    IsKillWindow = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventAttendanceWindows", x => x.Id);
                    table.CheckConstraint("CK_EventAttendanceWindows_OneParent", "num_nonnulls(\"EventId\", \"EventHistoryId\") = 1");
                });

            migrationBuilder.CreateTable(
                name: "EventComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventHistoryId = table.Column<int>(type: "integer", nullable: false),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsAnonymous = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DiscordMessageId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventComments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    EventName = table.Column<string>(type: "text", nullable: true),
                    EventType = table.Column<string>(type: "text", nullable: true),
                    EventLocation = table.Column<string>(type: "text", nullable: true),
                    PartySetupId = table.Column<int>(type: "integer", nullable: true),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CommencementStartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Duration = table.Column<double>(type: "double precision", nullable: true),
                    DkpPerHour = table.Column<int>(type: "integer", nullable: true),
                    CountsTowardActive = table.Column<bool>(type: "boolean", nullable: false),
                    PopWindow = table.Column<int>(type: "integer", nullable: true),
                    PopWindowCount = table.Column<int>(type: "integer", nullable: true),
                    TimeStamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventHistories_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventLootDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: true),
                    DkpPoolId = table.Column<int>(type: "integer", nullable: true),
                    DkpDebitedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EventId = table.Column<int>(type: "integer", nullable: true),
                    EventHistoryId = table.Column<int>(type: "integer", nullable: true),
                    ItemName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ItemWinner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ItemWinnerAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    WinningDkpSpent = table.Column<int>(type: "integer", nullable: true),
                    ActualDeductedDkp = table.Column<double>(type: "double precision", nullable: true),
                    RecordedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EditedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    EditedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastEditReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventLootDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventLootDetails_DkpPools_DkpPoolId",
                        column: x => x.DkpPoolId,
                        principalTable: "DkpPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_EventLootDetails_EventHistories_EventHistoryId",
                        column: x => x.EventHistoryId,
                        principalTable: "EventHistories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_EventLootDetails_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventPartySlotSignups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    PartySetupSlotId = table.Column<int>(type: "integer", nullable: false),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    DiscordUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MainJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SubJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SignedUpAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsPartyLeader = table.Column<bool>(type: "boolean", nullable: false),
                    IsAllianceLeader = table.Column<bool>(type: "boolean", nullable: false),
                    StayNextWindow = table.Column<bool>(type: "boolean", nullable: false),
                    EnfeebReady = table.Column<bool>(type: "boolean", nullable: false),
                    ResistReady = table.Column<bool>(type: "boolean", nullable: false),
                    RelicWeapon = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventPartySlotSignups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    EventName = table.Column<string>(type: "text", nullable: true),
                    EventType = table.Column<string>(type: "text", nullable: true),
                    EventLocation = table.Column<string>(type: "text", nullable: true),
                    AssignedMonsterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatorUserId = table.Column<string>(type: "text", nullable: true),
                    StarterUserId = table.Column<string>(type: "text", nullable: true),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CommencementStartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    AutoStart = table.Column<bool>(type: "boolean", nullable: false),
                    CountsTowardActive = table.Column<bool>(type: "boolean", nullable: false),
                    Duration = table.Column<double>(type: "double precision", nullable: true),
                    DkpPerHour = table.Column<int>(type: "integer", nullable: true),
                    HnmOpenBonusOverride = table.Column<double>(type: "double precision", nullable: true),
                    HnmCloseBonusOverride = table.Column<double>(type: "double precision", nullable: true),
                    HnmClaimBonusOverride = table.Column<double>(type: "double precision", nullable: true),
                    HnmKillBonusOverride = table.Column<double>(type: "double precision", nullable: true),
                    HnmPerWindowOverride = table.Column<double>(type: "double precision", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    WindowCountOverride = table.Column<int>(type: "integer", nullable: true),
                    SourceTodId = table.Column<int>(type: "integer", nullable: true),
                    HnmDefeatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HnmRepostAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HnmWindowNumber = table.Column<int>(type: "integer", nullable: false),
                    HnmClearedWindow = table.Column<int>(type: "integer", nullable: true),
                    DayNumber = table.Column<int>(type: "integer", nullable: true),
                    HnmNextDayOverride = table.Column<int>(type: "integer", nullable: true),
                    AttendanceMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    WdFinalizedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WdClaimed = table.Column<bool>(type: "boolean", nullable: false),
                    WdKilled = table.Column<bool>(type: "boolean", nullable: false),
                    WdPopWindow = table.Column<int>(type: "integer", nullable: true),
                    NextWindowAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SpawnWindowCount = table.Column<int>(type: "integer", nullable: true),
                    SpawnWindowMinutes = table.Column<int>(type: "integer", nullable: true),
                    WindowMethod = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    WindowAnchorAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PartySetupId = table.Column<int>(type: "integer", nullable: true),
                    DiscordChannelId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DiscordMessageId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TimeStamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Events_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Events_Tods_SourceTodId",
                        column: x => x.SourceTodId,
                        principalTable: "Tods",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "EventWindowRosterSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    WindowNumber = table.Column<int>(type: "integer", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AllianceName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AllianceSortOrder = table.Column<int>(type: "integer", nullable: false),
                    PartyName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PartySortOrder = table.Column<int>(type: "integer", nullable: false),
                    SlotSortOrder = table.Column<int>(type: "integer", nullable: false),
                    SlotLabel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MainJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SubJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    IsPartyLeader = table.Column<bool>(type: "boolean", nullable: false),
                    IsAllianceLeader = table.Column<bool>(type: "boolean", nullable: false),
                    WasLocked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventWindowRosterSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventWindowRosterSnapshots_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PartySetups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AssignedMonsterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    OwnerEventId = table.Column<int>(type: "integer", nullable: true),
                    ClonedFromPartySetupId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartySetups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartySetups_Events_OwnerEventId",
                        column: x => x.OwnerEventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PartySetups_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PartySetups_PartySetups_ClonedFromPartySetupId",
                        column: x => x.ClonedFromPartySetupId,
                        principalTable: "PartySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "WindowEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Open"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstCapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastCapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WindowAnchorAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WindowCount = table.Column<int>(type: "integer", nullable: true),
                    WindowMinutes = table.Column<int>(type: "integer", nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DkpAmount = table.Column<double>(type: "double precision", nullable: true),
                    MiscDkpAmount = table.Column<double>(type: "double precision", nullable: true),
                    PerCaptureDkp = table.Column<bool>(type: "boolean", nullable: false),
                    EntryType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PostedToSheetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SourceEventId = table.Column<int>(type: "integer", nullable: true),
                    CampEventHistoryId = table.Column<int>(type: "integer", nullable: true),
                    CampStartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CampEndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CampEventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CampEventLocation = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WindowEvents", x => x.Id);
                    table.CheckConstraint("CK_WindowEvents_EntryType", "\"EntryType\" IS NULL OR \"EntryType\" IN ('Kings Camp', 'Wyrms Camp', 'Misc Camp', 'Kill')");
                    table.CheckConstraint("CK_WindowEvents_Status", "\"Status\" IN ('Open', 'Closed', 'Archived')");
                    table.ForeignKey(
                        name: "FK_WindowEvents_EventHistories_CampEventHistoryId",
                        column: x => x.CampEventHistoryId,
                        principalTable: "EventHistories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WindowEvents_Events_SourceEventId",
                        column: x => x.SourceEventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WindowEvents_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HnmRecurringBoards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    MonsterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LeadHours = table.Column<double>(type: "double precision", nullable: false),
                    PartySetupId = table.Column<int>(type: "integer", nullable: true),
                    Details = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    EventLocation = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EventNameTemplate = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastSourceTodId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByAppUserId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SpawnKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HnmRecurringBoards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HnmRecurringBoards_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HnmRecurringBoards_PartySetups_PartySetupId",
                        column: x => x.PartySetupId,
                        principalTable: "PartySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PartySetupAlliances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PartySetupId = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartySetupAlliances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartySetupAlliances_PartySetups_PartySetupId",
                        column: x => x.PartySetupId,
                        principalTable: "PartySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WindowEventMemberDkps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WindowEventId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DkpAmount = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WindowEventMemberDkps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WindowEventMemberDkps_WindowEvents_WindowEventId",
                        column: x => x.WindowEventId,
                        principalTable: "WindowEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PartySetupParties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PartySetupAllianceId = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartySetupParties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartySetupParties_PartySetupAlliances_PartySetupAllianceId",
                        column: x => x.PartySetupAllianceId,
                        principalTable: "PartySetupAlliances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PartySetupSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PartySetupPartyId = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    RequirementType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MainJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SubJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    IsPartyLeader = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartySetupSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartySetupSlots_PartySetupParties_PartySetupPartyId",
                        column: x => x.PartySetupPartyId,
                        principalTable: "PartySetupParties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AddonApiTokens_IssuedToAppUserId",
                table: "AddonApiTokens",
                column: "IssuedToAppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AddonApiTokens_LinkshellId",
                table: "AddonApiTokens",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_AddonApiTokens_TokenHash",
                table: "AddonApiTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AddonPairingCodes_Code",
                table: "AddonPairingCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AddonPairingCodes_ExpiresAt",
                table: "AddonPairingCodes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AddonPairingCodes_IssuedToAppUserId",
                table: "AddonPairingCodes",
                column: "IssuedToAppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AddonPairingCodes_LinkshellId",
                table: "AddonPairingCodes",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_LinkshellId_CreatedAt",
                table: "Announcements",
                columns: new[] { "LinkshellId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventHistories_AppUserId",
                table: "AppUserEventHistories",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventHistories_EventHistoryId_AppUserId",
                table: "AppUserEventHistories",
                columns: new[] { "EventHistoryId", "AppUserId" },
                unique: true,
                filter: "\"AppUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEvents_AppUserId",
                table: "AppUserEvents",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEvents_EventId_AppUserId",
                table: "AppUserEvents",
                columns: new[] { "EventId", "AppUserId" },
                unique: true,
                filter: "\"AppUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventStatusLedgers_AppUserEventId_OccurredAt",
                table: "AppUserEventStatusLedgers",
                columns: new[] { "AppUserEventId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventStatusLedgers_EventId",
                table: "AppUserEventStatusLedgers",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventWindows_AppUserEventId_EventAttendanceWindowId",
                table: "AppUserEventWindows",
                columns: new[] { "AppUserEventId", "EventAttendanceWindowId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventWindows_EventAttendanceWindowId_AppUserId",
                table: "AppUserEventWindows",
                columns: new[] { "EventAttendanceWindowId", "AppUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUserLinkshells_AppUserId",
                table: "AppUserLinkshells",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserLinkshells_LinkshellId_AppUserId",
                table: "AppUserLinkshells",
                columns: new[] { "LinkshellId", "AppUserId" },
                unique: true,
                filter: "\"AppUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserLinkshells_LinkshellId_LinkshellRoleId",
                table: "AppUserLinkshells",
                columns: new[] { "LinkshellId", "LinkshellRoleId" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshotEntries_SnapshotId",
                table: "AttendanceSnapshotEntries",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_LinkedEventId",
                table: "AttendanceSnapshots",
                column: "LinkedEventId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_LinkshellId_CapturedAtUtc",
                table: "AttendanceSnapshots",
                columns: new[] { "LinkshellId", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_LinkshellId_SnapshotStatus",
                table: "AttendanceSnapshots",
                columns: new[] { "LinkshellId", "SnapshotStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_WindowEventId",
                table: "AttendanceSnapshots",
                column: "WindowEventId");

            migrationBuilder.CreateIndex(
                name: "IX_AuctionHistories_LinkshellId",
                table: "AuctionHistories",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_AuctionItems_AuctionHistoryId",
                table: "AuctionItems",
                column: "AuctionHistoryId");

            migrationBuilder.CreateIndex(
                name: "IX_AuctionItems_AuctionId",
                table: "AuctionItems",
                column: "AuctionId");

            migrationBuilder.CreateIndex(
                name: "IX_AuctionItems_SourceItemId",
                table: "AuctionItems",
                column: "SourceItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_DkpPoolId",
                table: "Auctions",
                column: "DkpPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_LinkshellId",
                table: "Auctions",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_Bids_AppUserId",
                table: "Bids",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Bids_AuctionItemId_CreatedAt",
                table: "Bids",
                columns: new[] { "AuctionItemId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Characters_AppUserId_NormalizedName",
                table: "Characters",
                columns: new[] { "AppUserId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Characters_AppUserId_Slot",
                table: "Characters",
                columns: new[] { "AppUserId", "Slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Characters_NormalizedName",
                table: "Characters",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_ChartMemberKeyItems_AppUserId",
                table: "ChartMemberKeyItems",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChartMemberKeyItems_LinkshellId_Board_KeyItemName_AppUserId",
                table: "ChartMemberKeyItems",
                columns: new[] { "LinkshellId", "Board", "KeyItemName", "AppUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChartPopItemCredits_ChartPopItemId",
                table: "ChartPopItemCredits",
                column: "ChartPopItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ChartPopItems_LinkshellId_Board_Boss_SortOrder",
                table: "ChartPopItems",
                columns: new[] { "LinkshellId", "Board", "Boss", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ChartWishlistRequests_LinkshellId_Board_Status_Priority",
                table: "ChartWishlistRequests",
                columns: new[] { "LinkshellId", "Board", "Status", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_ChartWishlistRequestZones_RequestId_Boss",
                table: "ChartWishlistRequestZones",
                columns: new[] { "RequestId", "Boss" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptureMembers_CaptureId_AppUserId",
                table: "ClaimShieldCaptureMembers",
                columns: new[] { "CaptureId", "AppUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptureMembers_CaptureId_CharacterName",
                table: "ClaimShieldCaptureMembers",
                columns: new[] { "CaptureId", "CharacterName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptures_EventHistoryId",
                table: "ClaimShieldCaptures",
                column: "EventHistoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptures_EventId",
                table: "ClaimShieldCaptures",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptures_LinkshellId_CapturedAtUtc",
                table: "ClaimShieldCaptures",
                columns: new[] { "LinkshellId", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscordActivityUsers_DiscordUserId",
                table: "DiscordActivityUsers",
                column: "DiscordUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordActivityUsers_IdentityUserId",
                table: "DiscordActivityUsers",
                column: "IdentityUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_AppUserId",
                table: "DkpLedgerEntries",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_AuditRelatedLedgerEntryId",
                table: "DkpLedgerEntries",
                column: "AuditRelatedLedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_EventHistoryId",
                table: "DkpLedgerEntries",
                column: "EventHistoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_LinkshellId_AppUserId_OccurredAt_Sequence",
                table: "DkpLedgerEntries",
                columns: new[] { "LinkshellId", "AppUserId", "OccurredAt", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_LinkshellId_DkpPoolId",
                table: "DkpLedgerEntries",
                columns: new[] { "LinkshellId", "DkpPoolId" });

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_SourceEventLootDetailId",
                table: "DkpLedgerEntries",
                column: "SourceEventLootDetailId");

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_SourceWindowEventId",
                table: "DkpLedgerEntries",
                column: "SourceWindowEventId");

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_SourceWindowEventId_AppUserId",
                table: "DkpLedgerEntries",
                columns: new[] { "SourceWindowEventId", "AppUserId" },
                unique: true,
                filter: "\"EntryType\" = 'SnapshotEarned' AND \"AppUserId\" IS NOT NULL AND \"SourceWindowEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DkpPoolEventTypes_DkpPoolId",
                table: "DkpPoolEventTypes",
                column: "DkpPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_DkpPoolEventTypes_LinkshellId_NormalizedEventType",
                table: "DkpPoolEventTypes",
                columns: new[] { "LinkshellId", "NormalizedEventType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DkpPools_LinkshellId_Default",
                table: "DkpPools",
                column: "LinkshellId",
                unique: true,
                filter: "\"IsDefault\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_DkpPools_LinkshellId_Name",
                table: "DkpPools",
                columns: new[] { "LinkshellId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventAttendanceWindows_EventHistoryId_SequenceNumber",
                table: "EventAttendanceWindows",
                columns: new[] { "EventHistoryId", "SequenceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_EventAttendanceWindows_EventId_SequenceNumber",
                table: "EventAttendanceWindows",
                columns: new[] { "EventId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventAttendanceWindows_OneClosePerEvent",
                table: "EventAttendanceWindows",
                column: "EventId",
                unique: true,
                filter: "\"IsClosingWindow\" AND NOT \"IsKillWindow\"");

            migrationBuilder.CreateIndex(
                name: "IX_EventAttendanceWindows_OneClosePerEventHistory",
                table: "EventAttendanceWindows",
                column: "EventHistoryId",
                unique: true,
                filter: "\"IsClosingWindow\" AND NOT \"IsKillWindow\"");

            migrationBuilder.CreateIndex(
                name: "IX_EventComments_EventHistoryId_CreatedAt",
                table: "EventComments",
                columns: new[] { "EventHistoryId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EventHistories_LinkshellId_EventName",
                table: "EventHistories",
                columns: new[] { "LinkshellId", "EventName" });

            migrationBuilder.CreateIndex(
                name: "IX_EventHistories_PartySetupId",
                table: "EventHistories",
                column: "PartySetupId");

            migrationBuilder.CreateIndex(
                name: "IX_EventLootDetails_DkpPoolId",
                table: "EventLootDetails",
                column: "DkpPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_EventLootDetails_EventHistoryId",
                table: "EventLootDetails",
                column: "EventHistoryId");

            migrationBuilder.CreateIndex(
                name: "IX_EventLootDetails_EventId",
                table: "EventLootDetails",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_EventLootDetails_LinkshellId",
                table: "EventLootDetails",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_EventPartySlotSignups_EventId_AppUserId",
                table: "EventPartySlotSignups",
                columns: new[] { "EventId", "AppUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_EventPartySlotSignups_EventId_DiscordUserId",
                table: "EventPartySlotSignups",
                columns: new[] { "EventId", "DiscordUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_EventPartySlotSignups_EventId_PartySetupSlotId",
                table: "EventPartySlotSignups",
                columns: new[] { "EventId", "PartySetupSlotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventPartySlotSignups_PartySetupSlotId",
                table: "EventPartySlotSignups",
                column: "PartySetupSlotId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_LinkshellId",
                table: "Events",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_PartySetupId",
                table: "Events",
                column: "PartySetupId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_SourceTodId",
                table: "Events",
                column: "SourceTodId");

            migrationBuilder.CreateIndex(
                name: "IX_EventWindowRosterSnapshots_EventId_WindowNumber",
                table: "EventWindowRosterSnapshots",
                columns: new[] { "EventId", "WindowNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_HnmRecurringBoards_LinkshellId_SpawnKey",
                table: "HnmRecurringBoards",
                columns: new[] { "LinkshellId", "SpawnKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HnmRecurringBoards_PartySetupId",
                table: "HnmRecurringBoards",
                column: "PartySetupId");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_AppUserId",
                table: "Invites",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_DiscordUserId",
                table: "Invites",
                column: "DiscordUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_LinkshellId",
                table: "Invites",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_OneOpenDiscordInvite",
                table: "Invites",
                columns: new[] { "LinkshellId", "DiscordUserId" },
                unique: true,
                filter: "\"Status\" IN ('Pending', 'PendingInvite') AND \"AppUserId\" IS NULL AND \"DiscordUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_OneOpenInvite",
                table: "Invites",
                columns: new[] { "LinkshellId", "AppUserId" },
                unique: true,
                filter: "\"Status\" IN ('Pending', 'PendingInvite') AND \"AppUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_OneOpenJoinRequest",
                table: "Invites",
                columns: new[] { "LinkshellId", "AppUserId" },
                unique: true,
                filter: "\"Status\" = 'PendingJoinRequest'");

            migrationBuilder.CreateIndex(
                name: "IX_Items_LinkshellId_ItemName",
                table: "Items",
                columns: new[] { "LinkshellId", "ItemName" });

            migrationBuilder.CreateIndex(
                name: "IX_JobRatings_LinkshellId_TargetAppUserId_RaterAppUserId_Chara~",
                table: "JobRatings",
                columns: new[] { "LinkshellId", "TargetAppUserId", "RaterAppUserId", "CharacterSlot", "JobIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRatings_RaterAppUserId",
                table: "JobRatings",
                column: "RaterAppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRatings_TargetAppUserId",
                table: "JobRatings",
                column: "TargetAppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_LinkshellId_EntryNumber",
                table: "JournalEntries",
                columns: new[] { "LinkshellId", "EntryNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_LinkshellId_Sequence",
                table: "JournalEntries",
                columns: new[] { "LinkshellId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_LinkshellId_TransactionDate",
                table: "JournalEntries",
                columns: new[] { "LinkshellId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_OneReversalPerEntry",
                table: "JournalEntries",
                column: "ReversesJournalEntryId",
                unique: true,
                filter: "\"Kind\" = 'Reversal'");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_ReversesJournalEntryId",
                table: "JournalEntries",
                column: "ReversesJournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_JournalEntryId",
                table: "JournalEntryLines",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_LedgerAccountId",
                table: "JournalEntryLines",
                column: "LedgerAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_LinkshellId_LedgerAccountId_TransactionDa~",
                table: "JournalEntryLines",
                columns: new[] { "LinkshellId", "LedgerAccountId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_LinkshellId_TransactionDate",
                table: "JournalEntryLines",
                columns: new[] { "LinkshellId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_LinkshellId_AccountNumber",
                table: "LedgerAccounts",
                columns: new[] { "LinkshellId", "AccountNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_LinkshellId_Cash",
                table: "LedgerAccounts",
                column: "LinkshellId",
                unique: true,
                filter: "\"IsCash\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellChannelRoutes_LinkshellId",
                table: "LinkshellChannelRoutes",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellChannelRoutes_OneAuctionRoute",
                table: "LinkshellChannelRoutes",
                column: "LinkshellId",
                unique: true,
                filter: "\"PostAuctions\"");

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellChannelRoutes_OneDkpSheetRoute",
                table: "LinkshellChannelRoutes",
                column: "LinkshellId",
                unique: true,
                filter: "\"PostDkpSheet\"");

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellChannelRoutes_OneLootRoute",
                table: "LinkshellChannelRoutes",
                column: "LinkshellId",
                unique: true,
                filter: "\"PostLoot\"");

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellChannelRoutes_OneTodBoardRoute",
                table: "LinkshellChannelRoutes",
                column: "LinkshellId",
                unique: true,
                filter: "\"PostTodBoard\"");

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellMonsterTimings_LinkshellId_NormalizedMonsterName",
                table: "LinkshellMonsterTimings",
                columns: new[] { "LinkshellId", "NormalizedMonsterName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellPresences_LinkshellId_CharacterName",
                table: "LinkshellPresences",
                columns: new[] { "LinkshellId", "CharacterName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellPresences_LinkshellId_LastSeenUtc",
                table: "LinkshellPresences",
                columns: new[] { "LinkshellId", "LastSeenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellRoles_LinkshellId_Name",
                table: "LinkshellRoles",
                columns: new[] { "LinkshellId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Linkshells_AppUserId",
                table: "Linkshells",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PartySetupAlliances_PartySetupId_SortOrder",
                table: "PartySetupAlliances",
                columns: new[] { "PartySetupId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PartySetupParties_PartySetupAllianceId_SortOrder",
                table: "PartySetupParties",
                columns: new[] { "PartySetupAllianceId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PartySetups_ClonedFromPartySetupId",
                table: "PartySetups",
                column: "ClonedFromPartySetupId");

            migrationBuilder.CreateIndex(
                name: "IX_PartySetups_LinkshellId_AssignedMonsterName",
                table: "PartySetups",
                columns: new[] { "LinkshellId", "AssignedMonsterName" });

            migrationBuilder.CreateIndex(
                name: "IX_PartySetups_LinkshellId_Name",
                table: "PartySetups",
                columns: new[] { "LinkshellId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_PartySetups_OwnerEventId",
                table: "PartySetups",
                column: "OwnerEventId",
                unique: true,
                filter: "\"OwnerEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PartySetupSlots_PartySetupPartyId_SortOrder",
                table: "PartySetupSlots",
                columns: new[] { "PartySetupPartyId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingTodSubmissions_LinkshellId_SpawnKey",
                table: "PendingTodSubmissions",
                columns: new[] { "LinkshellId", "SpawnKey" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingTodSubmissions_SubmittedByAppUserId",
                table: "PendingTodSubmissions",
                column: "SubmittedByAppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaceholderClaimRequests_LinkshellId_ResolvedAtUtc",
                table: "PlaceholderClaimRequests",
                columns: new[] { "LinkshellId", "ResolvedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaceholderClaimRequests_PlaceholderAppUserId_RequesterAppU~",
                table: "PlaceholderClaimRequests",
                columns: new[] { "PlaceholderAppUserId", "RequesterAppUserId" },
                unique: true,
                filter: "\"ResolvedAtUtc\" IS NULL AND \"RequesterAppUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlaceholderClaimRequests_PlaceholderAppUserId_RequesterDisc~",
                table: "PlaceholderClaimRequests",
                columns: new[] { "PlaceholderAppUserId", "RequesterDiscordUserId" },
                unique: true,
                filter: "\"ResolvedAtUtc\" IS NULL AND \"RequesterDiscordUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Rules_LinkshellId_CreatedAt",
                table: "Rules",
                columns: new[] { "LinkshellId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tods_LinkshellId_SpawnKey_Time",
                table: "Tods",
                columns: new[] { "LinkshellId", "SpawnKey", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_Tods_LinkshellId_Time",
                table: "Tods",
                columns: new[] { "LinkshellId", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_WindowEventMemberDkps_WindowEventId_CharacterName",
                table: "WindowEventMemberDkps",
                columns: new[] { "WindowEventId", "CharacterName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WindowEvents_CampEventHistoryId",
                table: "WindowEvents",
                column: "CampEventHistoryId");

            migrationBuilder.CreateIndex(
                name: "IX_WindowEvents_LinkshellId_LastCapturedAtUtc",
                table: "WindowEvents",
                columns: new[] { "LinkshellId", "LastCapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WindowEvents_LinkshellId_Status_NormalizedName",
                table: "WindowEvents",
                columns: new[] { "LinkshellId", "Status", "NormalizedName" });

            migrationBuilder.CreateIndex(
                name: "IX_WindowEvents_SourceEventId",
                table: "WindowEvents",
                column: "SourceEventId");

            migrationBuilder.AddForeignKey(
                name: "FK_AppUserEventHistories_EventHistories_EventHistoryId",
                table: "AppUserEventHistories",
                column: "EventHistoryId",
                principalTable: "EventHistories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AppUserEvents_Events_EventId",
                table: "AppUserEvents",
                column: "EventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AppUserEventStatusLedgers_Events_EventId",
                table: "AppUserEventStatusLedgers",
                column: "EventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AppUserEventWindows_EventAttendanceWindows_EventAttendanceW~",
                table: "AppUserEventWindows",
                column: "EventAttendanceWindowId",
                principalTable: "EventAttendanceWindows",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AttendanceSnapshotEntries_AttendanceSnapshots_SnapshotId",
                table: "AttendanceSnapshotEntries",
                column: "SnapshotId",
                principalTable: "AttendanceSnapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AttendanceSnapshots_Events_LinkedEventId",
                table: "AttendanceSnapshots",
                column: "LinkedEventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AttendanceSnapshots_WindowEvents_WindowEventId",
                table: "AttendanceSnapshots",
                column: "WindowEventId",
                principalTable: "WindowEvents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ClaimShieldCaptureMembers_ClaimShieldCaptures_CaptureId",
                table: "ClaimShieldCaptureMembers",
                column: "CaptureId",
                principalTable: "ClaimShieldCaptures",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ClaimShieldCaptures_EventHistories_EventHistoryId",
                table: "ClaimShieldCaptures",
                column: "EventHistoryId",
                principalTable: "EventHistories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ClaimShieldCaptures_Events_EventId",
                table: "ClaimShieldCaptures",
                column: "EventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_DkpLedgerEntries_EventHistories_EventHistoryId",
                table: "DkpLedgerEntries",
                column: "EventHistoryId",
                principalTable: "EventHistories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_EventAttendanceWindows_EventHistories_EventHistoryId",
                table: "EventAttendanceWindows",
                column: "EventHistoryId",
                principalTable: "EventHistories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_EventAttendanceWindows_Events_EventId",
                table: "EventAttendanceWindows",
                column: "EventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_EventComments_EventHistories_EventHistoryId",
                table: "EventComments",
                column: "EventHistoryId",
                principalTable: "EventHistories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_EventHistories_PartySetups_PartySetupId",
                table: "EventHistories",
                column: "PartySetupId",
                principalTable: "PartySetups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_EventLootDetails_Events_EventId",
                table: "EventLootDetails",
                column: "EventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_EventPartySlotSignups_Events_EventId",
                table: "EventPartySlotSignups",
                column: "EventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_EventPartySlotSignups_PartySetupSlots_PartySetupSlotId",
                table: "EventPartySlotSignups",
                column: "PartySetupSlotId",
                principalTable: "PartySetupSlots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Events_PartySetups_PartySetupId",
                table: "Events",
                column: "PartySetupId",
                principalTable: "PartySetups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql(ChainOnlyDefaultsSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Linkshells_AspNetUsers_AppUserId",
                table: "Linkshells");

            migrationBuilder.DropForeignKey(
                name: "FK_Events_Linkshells_LinkshellId",
                table: "Events");

            migrationBuilder.DropForeignKey(
                name: "FK_PartySetups_Linkshells_LinkshellId",
                table: "PartySetups");

            migrationBuilder.DropForeignKey(
                name: "FK_Tods_Linkshells_LinkshellId",
                table: "Tods");

            migrationBuilder.DropForeignKey(
                name: "FK_PartySetups_Events_OwnerEventId",
                table: "PartySetups");

            migrationBuilder.DropTable(
                name: "AddonApiTokens");

            migrationBuilder.DropTable(
                name: "AddonPairingCodes");

            migrationBuilder.DropTable(
                name: "Announcements");

            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "AppUserEventHistories");

            migrationBuilder.DropTable(
                name: "AppUserEventStatusLedgers");

            migrationBuilder.DropTable(
                name: "AppUserEventWindows");

            migrationBuilder.DropTable(
                name: "AppUserLinkshells");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AttendanceSnapshotEntries");

            migrationBuilder.DropTable(
                name: "Bids");

            migrationBuilder.DropTable(
                name: "Characters");

            migrationBuilder.DropTable(
                name: "ChartMemberKeyItems");

            migrationBuilder.DropTable(
                name: "ChartPopItemCredits");

            migrationBuilder.DropTable(
                name: "ChartWishlistRequestZones");

            migrationBuilder.DropTable(
                name: "ClaimShieldCaptureMembers");

            migrationBuilder.DropTable(
                name: "DiscordActivityUsers");

            migrationBuilder.DropTable(
                name: "DkpLedgerEntries");

            migrationBuilder.DropTable(
                name: "DkpPoolEventTypes");

            migrationBuilder.DropTable(
                name: "EventComments");

            migrationBuilder.DropTable(
                name: "EventLootDetails");

            migrationBuilder.DropTable(
                name: "EventPartySlotSignups");

            migrationBuilder.DropTable(
                name: "EventWindowRosterSnapshots");

            migrationBuilder.DropTable(
                name: "HnmRecurringBoards");

            migrationBuilder.DropTable(
                name: "Invites");

            migrationBuilder.DropTable(
                name: "JobRatings");

            migrationBuilder.DropTable(
                name: "JournalEntryLines");

            migrationBuilder.DropTable(
                name: "LinkshellBanners");

            migrationBuilder.DropTable(
                name: "LinkshellChannelRoutes");

            migrationBuilder.DropTable(
                name: "LinkshellMonsterTimings");

            migrationBuilder.DropTable(
                name: "LinkshellPresences");

            migrationBuilder.DropTable(
                name: "PendingTodSubmissions");

            migrationBuilder.DropTable(
                name: "PlaceholderClaimRequests");

            migrationBuilder.DropTable(
                name: "Rules");

            migrationBuilder.DropTable(
                name: "WindowEventMemberDkps");

            migrationBuilder.DropTable(
                name: "AppUserEvents");

            migrationBuilder.DropTable(
                name: "EventAttendanceWindows");

            migrationBuilder.DropTable(
                name: "LinkshellRoles");

            migrationBuilder.DropTable(
                name: "AttendanceSnapshots");

            migrationBuilder.DropTable(
                name: "AuctionItems");

            migrationBuilder.DropTable(
                name: "ChartPopItems");

            migrationBuilder.DropTable(
                name: "ChartWishlistRequests");

            migrationBuilder.DropTable(
                name: "ClaimShieldCaptures");

            migrationBuilder.DropTable(
                name: "PartySetupSlots");

            migrationBuilder.DropTable(
                name: "JournalEntries");

            migrationBuilder.DropTable(
                name: "LedgerAccounts");

            migrationBuilder.DropTable(
                name: "WindowEvents");

            migrationBuilder.DropTable(
                name: "AuctionHistories");

            migrationBuilder.DropTable(
                name: "Auctions");

            migrationBuilder.DropTable(
                name: "Items");

            migrationBuilder.DropTable(
                name: "PartySetupParties");

            migrationBuilder.DropTable(
                name: "EventHistories");

            migrationBuilder.DropTable(
                name: "DkpPools");

            migrationBuilder.DropTable(
                name: "PartySetupAlliances");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "Linkshells");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "PartySetups");

            migrationBuilder.DropTable(
                name: "Tods");
        }
    }
}
