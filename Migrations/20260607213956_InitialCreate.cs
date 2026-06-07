using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
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
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    CharacterName = table.Column<string>(type: "text", nullable: true),
                    AltCharacterName1 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AltCharacterName2 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Alt1JobLevels = table.Column<int[]>(type: "jsonb", nullable: true),
                    Alt2JobLevels = table.Column<int[]>(type: "jsonb", nullable: true),
                    TimeZone = table.Column<string>(type: "text", nullable: true),
                    PrimaryLinkshellId = table.Column<int>(type: "integer", nullable: true),
                    PrimaryLinkshellName = table.Column<string>(type: "text", nullable: true),
                    ProfileImage = table.Column<byte[]>(type: "bytea", nullable: true),
                    IsSuperAdmin = table.Column<bool>(type: "boolean", nullable: false),
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
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    RoleId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
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
                name: "DiscordActivityUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DiscordUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Username = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Discriminator = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    GlobalName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Avatar = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IdentityUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                    Status = table.Column<string>(type: "text", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    LinkshellType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LootStructure = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DkpRoundingIncrement = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EnableHnmSection = table.Column<bool>(type: "boolean", nullable: false),
                    EnableMissions = table.Column<bool>(type: "boolean", nullable: false),
                    EnableAuctions = table.Column<bool>(type: "boolean", nullable: false),
                    AuctionsLocked = table.Column<bool>(type: "boolean", nullable: false),
                    EnableToDs = table.Column<bool>(type: "boolean", nullable: false),
                    EnableEndgame = table.Column<bool>(type: "boolean", nullable: false),
                    EnableEvents = table.Column<bool>(type: "boolean", nullable: false),
                    EnableDkp = table.Column<bool>(type: "boolean", nullable: false),
                    EnableItems = table.Column<bool>(type: "boolean", nullable: false),
                    EnableRevenue = table.Column<bool>(type: "boolean", nullable: false),
                    HiddenTodMonsters = table.Column<string>(type: "text", nullable: false),
                    DiscordGuildId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DiscordGuildName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    GoogleSpreadsheetId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    GoogleSheetTabName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DkpTemplateTabName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GoogleOAuthRefreshTokenEnc = table.Column<string>(type: "text", nullable: true),
                    GoogleOAuthUserEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    GoogleOAuthConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SheetSyncEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AttInputTabName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AttInputDefaultEntryType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ManualPointsTabName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Linkshells", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Linkshells_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<string>(type: "text", nullable: true),
                    NotificationType = table.Column<string>(type: "text", nullable: true),
                    CharacterNameSender = table.Column<string>(type: "text", nullable: true),
                    NotificationDetails = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_AspNetUsers_AppUserId",
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
                    Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
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
                    Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsumedTokenId = table.Column<int>(type: "integer", nullable: true)
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
                    LinkshellName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AnnouncementTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AnnouncementDetails = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
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
                name: "AppUserLinkshells",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<string>(type: "text", nullable: true),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "text", nullable: true),
                    Rank = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: true),
                    LinkshellDkp = table.Column<double>(type: "double precision", nullable: true),
                    SeededDkpEarned = table.Column<double>(type: "double precision", nullable: false),
                    SeededDkpSpent = table.Column<double>(type: "double precision", nullable: false),
                    DkpSeedLedgerId = table.Column<int>(type: "integer", nullable: false),
                    DateJoined = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    JobLevels = table.Column<int[]>(type: "jsonb", nullable: true)
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
                        name: "FK_AppUserLinkshells_Linkshells_LinkshellId",
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
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DiscordChannelId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
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
                    DiscordChannelId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DiscordMessageId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DiscordMessageIds = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Auctions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Auctions_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClaimShieldCaptures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    MonsterName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Won = table.Column<bool>(type: "boolean", nullable: false),
                    TotalPlayers = table.Column<int>(type: "integer", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CapturedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CapturedMessage = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MemberCount = table.Column<int>(type: "integer", nullable: false),
                    MatchedCount = table.Column<int>(type: "integer", nullable: false),
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
                name: "EventHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    EventName = table.Column<string>(type: "text", nullable: true),
                    EventType = table.Column<string>(type: "text", nullable: true),
                    EventLocation = table.Column<string>(type: "text", nullable: true),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CommencementStartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Duration = table.Column<double>(type: "double precision", nullable: true),
                    DkpPerHour = table.Column<int>(type: "integer", nullable: true),
                    EventDkp = table.Column<double>(type: "double precision", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
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
                name: "Invites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DiscordUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DiscordDisplayName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
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
                    LinkshellName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ItemName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ItemType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "LinkshellDiscordChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ChannelId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChannelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellDiscordChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellDiscordChannels_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LinkshellDiscordWebhooks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PostTodBoard = table.Column<bool>(type: "boolean", nullable: false),
                    TodBoardMessageId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PostDkpSpendLog = table.Column<bool>(type: "boolean", nullable: false),
                    PostAttendanceSnapshot = table.Column<bool>(type: "boolean", nullable: false),
                    PostAuctions = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellDiscordWebhooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellDiscordWebhooks_Linkshells_LinkshellId",
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
                    CanManageTreasury = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageRules = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageAnnouncements = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageTods = table.Column<bool>(type: "boolean", nullable: false),
                    CanAuditDkp = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageAuctions = table.Column<bool>(type: "boolean", nullable: false),
                    CanLockAuctions = table.Column<bool>(type: "boolean", nullable: false),
                    CanCustomizeLinkshell = table.Column<bool>(type: "boolean", nullable: false),
                    CanSubmitTodForApproval = table.Column<bool>(type: "boolean", nullable: false),
                    CanSubmitAttendanceForApproval = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageParties = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageInvites = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellRoles_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
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
                    AssignedMonsterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartySetups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartySetups_Linkshells_LinkshellId",
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
                    ReviewNotes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MonsterName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DayNumber = table.Column<int>(type: "integer", nullable: true),
                    Claim = table.Column<bool>(type: "boolean", nullable: true),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Cooldown = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Interval = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RepopTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImagePath = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingTodSubmissions", x => x.Id);
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
                name: "RevenueEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    LinkshellName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EntryType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Value = table.Column<long>(type: "bigint", nullable: false),
                    Details = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevenueEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RevenueEntries_Linkshells_LinkshellId",
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
                    LinkshellName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RuleTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RuleDetails = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
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
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Claim = table.Column<bool>(type: "boolean", nullable: true),
                    Cooldown = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RepopTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Interval = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TimeStamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TotalClaims = table.Column<int>(type: "integer", nullable: true),
                    TotalTods = table.Column<int>(type: "integer", nullable: true),
                    ImagePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tods_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    DkpAmount = table.Column<double>(type: "double precision", nullable: true),
                    EntryType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PostedToSheetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstAttInputRowNumber = table.Column<int>(type: "integer", nullable: true),
                    AttInputRowCount = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WindowEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WindowEvents_Linkshells_LinkshellId",
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
                    Matched = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimShieldCaptureMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimShieldCaptureMembers_ClaimShieldCaptures_CaptureId",
                        column: x => x.CaptureId,
                        principalTable: "ClaimShieldCaptures",
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
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Duration = table.Column<double>(type: "double precision", nullable: true),
                    EventDkp = table.Column<double>(type: "double precision", nullable: true),
                    IsQuickJoin = table.Column<bool>(type: "boolean", nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: true),
                    Proctor = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserEventHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUserEventHistories_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AppUserEventHistories_EventHistories_EventHistoryId",
                        column: x => x.EventHistoryId,
                        principalTable: "EventHistories",
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
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EventName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EventType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EventLocation = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EventStartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EventEndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ItemName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Details = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    EditReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SourceTodLootDetailId = table.Column<int>(type: "integer", nullable: true),
                    SourceEventLootDetailId = table.Column<int>(type: "integer", nullable: true),
                    SourceAuctionHistoryId = table.Column<int>(type: "integer", nullable: true),
                    SourceWindowEventId = table.Column<int>(type: "integer", nullable: true),
                    AuditRelatedLedgerEntryId = table.Column<int>(type: "integer", nullable: true),
                    AttInputRowNumber = table.Column<int>(type: "integer", nullable: true),
                    SheetAppendedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                        name: "FK_DkpLedgerEntries_AuctionHistories_SourceAuctionHistoryId",
                        column: x => x.SourceAuctionHistoryId,
                        principalTable: "AuctionHistories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DkpLedgerEntries_EventHistories_EventHistoryId",
                        column: x => x.EventHistoryId,
                        principalTable: "EventHistories",
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
                    EndingBidDkp = table.Column<int>(type: "integer", nullable: true),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    SourceItemId = table.Column<int>(type: "integer", nullable: true),
                    GilAmount = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuctionItems", x => x.Id);
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
                        principalColumn: "Id");
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
                name: "PendingTodLootSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PendingTodSubmissionId = table.Column<int>(type: "integer", nullable: false),
                    ItemName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ItemWinner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    WinningDkpSpent = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingTodLootSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingTodLootSubmissions_PendingTodSubmissions_PendingTodS~",
                        column: x => x.PendingTodSubmissionId,
                        principalTable: "PendingTodSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    CreatorUserId = table.Column<string>(type: "text", nullable: true),
                    StarterUserId = table.Column<string>(type: "text", nullable: true),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CommencementStartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AutoStart = table.Column<bool>(type: "boolean", nullable: false),
                    Duration = table.Column<double>(type: "double precision", nullable: true),
                    DkpPerHour = table.Column<int>(type: "integer", nullable: true),
                    EventDkp = table.Column<double>(type: "double precision", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    CreationSource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    WindowCountOverride = table.Column<int>(type: "integer", nullable: true),
                    AttInputEntryType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    AttInputAppendedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SourceTodId = table.Column<int>(type: "integer", nullable: true),
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
                        name: "FK_Events_PartySetups_PartySetupId",
                        column: x => x.PartySetupId,
                        principalTable: "PartySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Events_Tods_SourceTodId",
                        column: x => x.SourceTodId,
                        principalTable: "Tods",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TodLootDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TodId = table.Column<int>(type: "integer", nullable: true),
                    ItemName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ItemWinner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    WinningDkpSpent = table.Column<int>(type: "integer", nullable: true),
                    ActualDeductedDkp = table.Column<double>(type: "double precision", nullable: true),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EditedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    EditedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastEditReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodLootDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodLootDetails_Tods_TodId",
                        column: x => x.TodId,
                        principalTable: "Tods",
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
                name: "AppUserEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<string>(type: "text", nullable: true),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "text", nullable: true),
                    JobName = table.Column<string>(type: "text", nullable: true),
                    SubJobName = table.Column<string>(type: "text", nullable: true),
                    JobType = table.Column<string>(type: "text", nullable: true),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Duration = table.Column<double>(type: "double precision", nullable: true),
                    EventDkp = table.Column<double>(type: "double precision", nullable: true),
                    IsQuickJoin = table.Column<bool>(type: "boolean", nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: true),
                    Proctor = table.Column<string>(type: "text", nullable: true),
                    IsOnBreak = table.Column<bool>(type: "boolean", nullable: true),
                    PauseTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
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
                    table.ForeignKey(
                        name: "FK_AppUserEvents_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    UtcOffset = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LinkedEventId = table.Column<int>(type: "integer", nullable: true),
                    WindowEventId = table.Column<int>(type: "integer", nullable: true),
                    SnapshotStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Active"),
                    DuplicateOfSnapshotId = table.Column<int>(type: "integer", nullable: true),
                    AttInputAppendedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceSnapshots_AttendanceSnapshots_DuplicateOfSnapshot~",
                        column: x => x.DuplicateOfSnapshotId,
                        principalTable: "AttendanceSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AttendanceSnapshots_Events_LinkedEventId",
                        column: x => x.LinkedEventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AttendanceSnapshots_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AttendanceSnapshots_WindowEvents_WindowEventId",
                        column: x => x.WindowEventId,
                        principalTable: "WindowEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "EventAttendanceWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    SequenceNumber = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PostedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PostedBySource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DkpAmount = table.Column<double>(type: "double precision", nullable: true),
                    AttInputAppendedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventAttendanceWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventAttendanceWindows_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventLootDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: true),
                    EventHistoryId = table.Column<int>(type: "integer", nullable: true),
                    ItemName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ItemWinner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    WinningDkpSpent = table.Column<int>(type: "integer", nullable: true),
                    ActualDeductedDkp = table.Column<double>(type: "double precision", nullable: true),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EditedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    EditedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastEditReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventLootDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventLootDetails_EventHistories_EventHistoryId",
                        column: x => x.EventHistoryId,
                        principalTable: "EventHistories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_EventLootDetails_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PendingAttendanceSnapshotSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    SubmittedByAppUserId = table.Column<string>(type: "text", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewNotes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CapturedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UtcOffset = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LinkedEventId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAttendanceSnapshotSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingAttendanceSnapshotSubmissions_AspNetUsers_SubmittedB~",
                        column: x => x.SubmittedByAppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PendingAttendanceSnapshotSubmissions_Events_LinkedEventId",
                        column: x => x.LinkedEventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PendingAttendanceSnapshotSubmissions_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingAttendanceWindowSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    SubmittedByAppUserId = table.Column<string>(type: "text", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewNotes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    WindowIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAttendanceWindowSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingAttendanceWindowSubmissions_AspNetUsers_SubmittedByA~",
                        column: x => x.SubmittedByAppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PendingAttendanceWindowSubmissions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PendingAttendanceWindowSubmissions_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
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
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsPartyLeader = table.Column<bool>(type: "boolean", nullable: false),
                    SignedUpAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    SignedUpCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SignedUpAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SignedUpRole = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SignedUpMainJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SignedUpSubJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true)
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

            migrationBuilder.CreateTable(
                name: "AttendanceSnapshotEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SnapshotId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MainJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    MainJobLevel = table.Column<int>(type: "integer", nullable: true),
                    SubJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SubJobLevel = table.Column<int>(type: "integer", nullable: true),
                    Zone = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceSnapshotEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceSnapshotEntries_AttendanceSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "AttendanceSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserEventStatusLedgers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserEventId = table.Column<int>(type: "integer", nullable: false),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    AppUserId = table.Column<string>(type: "text", nullable: true),
                    ActionType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequiresVerification = table.Column<bool>(type: "boolean", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerifiedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DeniedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeniedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    EventAttendanceWindowId = table.Column<int>(type: "integer", nullable: true)
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
                    table.ForeignKey(
                        name: "FK_AppUserEventStatusLedgers_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AppUserEventStatusLedgers_EventAttendanceWindows_EventAtten~",
                        column: x => x.EventAttendanceWindowId,
                        principalTable: "EventAttendanceWindows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AppUserEventStatusLedgers_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserEventWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserEventId = table.Column<int>(type: "integer", nullable: false),
                    EventAttendanceWindowId = table.Column<int>(type: "integer", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VerifiedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
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
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppUserEventWindows_EventAttendanceWindows_EventAttendanceW~",
                        column: x => x.EventAttendanceWindowId,
                        principalTable: "EventAttendanceWindows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingAttendanceSnapshotEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PendingAttendanceSnapshotSubmissionId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MainJob = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MainJobLevel = table.Column<int>(type: "integer", nullable: true),
                    SubJob = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SubJobLevel = table.Column<int>(type: "integer", nullable: true),
                    Zone = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAttendanceSnapshotEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingAttendanceSnapshotEntries_PendingAttendanceSnapshotS~",
                        column: x => x.PendingAttendanceSnapshotSubmissionId,
                        principalTable: "PendingAttendanceSnapshotSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingAttendanceWindowMemberSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PendingAttendanceWindowSubmissionId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MainJob = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MainJobLevel = table.Column<int>(type: "integer", nullable: true),
                    SubJob = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SubJobLevel = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAttendanceWindowMemberSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingAttendanceWindowMemberSubmissions_PendingAttendanceW~",
                        column: x => x.PendingAttendanceWindowSubmissionId,
                        principalTable: "PendingAttendanceWindowSubmissions",
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
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MainJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SubJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SignedUpAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventPartySlotSignups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventPartySlotSignups_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EventPartySlotSignups_PartySetupSlots_PartySetupSlotId",
                        column: x => x.PartySetupSlotId,
                        principalTable: "PartySetupSlots",
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
                name: "IX_AppUserEventHistories_EventHistoryId",
                table: "AppUserEventHistories",
                column: "EventHistoryId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEvents_AppUserId",
                table: "AppUserEvents",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEvents_EventId",
                table: "AppUserEvents",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventStatusLedgers_AppUserEventId_OccurredAt",
                table: "AppUserEventStatusLedgers",
                columns: new[] { "AppUserEventId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventStatusLedgers_AppUserId",
                table: "AppUserEventStatusLedgers",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventStatusLedgers_EventAttendanceWindowId",
                table: "AppUserEventStatusLedgers",
                column: "EventAttendanceWindowId");

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
                name: "IX_AppUserEventWindows_EventAttendanceWindowId",
                table: "AppUserEventWindows",
                column: "EventAttendanceWindowId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserLinkshells_AppUserId",
                table: "AppUserLinkshells",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserLinkshells_LinkshellId",
                table: "AppUserLinkshells",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

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
                name: "IX_AttendanceSnapshots_DuplicateOfSnapshotId",
                table: "AttendanceSnapshots",
                column: "DuplicateOfSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_LinkedEventId",
                table: "AttendanceSnapshots",
                column: "LinkedEventId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_LinkshellId_CapturedAtUtc",
                table: "AttendanceSnapshots",
                columns: new[] { "LinkshellId", "CapturedAtUtc" });

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
                name: "IX_ClaimShieldCaptureMembers_CaptureId",
                table: "ClaimShieldCaptureMembers",
                column: "CaptureId");

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
                name: "IX_DkpLedgerEntries_AttInputRowNumber",
                table: "DkpLedgerEntries",
                column: "AttInputRowNumber");

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
                name: "IX_DkpLedgerEntries_SourceAuctionHistoryId",
                table: "DkpLedgerEntries",
                column: "SourceAuctionHistoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_SourceEventLootDetailId",
                table: "DkpLedgerEntries",
                column: "SourceEventLootDetailId");

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_SourceTodLootDetailId",
                table: "DkpLedgerEntries",
                column: "SourceTodLootDetailId");

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_SourceWindowEventId",
                table: "DkpLedgerEntries",
                column: "SourceWindowEventId");

            migrationBuilder.CreateIndex(
                name: "IX_EventAttendanceWindows_EventId_SequenceNumber",
                table: "EventAttendanceWindows",
                columns: new[] { "EventId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventHistories_LinkshellId",
                table: "EventHistories",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_EventLootDetails_EventHistoryId",
                table: "EventLootDetails",
                column: "EventHistoryId");

            migrationBuilder.CreateIndex(
                name: "IX_EventLootDetails_EventId",
                table: "EventLootDetails",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_EventPartySlotSignups_EventId_AppUserId",
                table: "EventPartySlotSignups",
                columns: new[] { "EventId", "AppUserId" });

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
                name: "IX_Items_LinkshellId_ItemName",
                table: "Items",
                columns: new[] { "LinkshellId", "ItemName" });

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellDiscordChannels_LinkshellId",
                table: "LinkshellDiscordChannels",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellDiscordWebhooks_LinkshellId",
                table: "LinkshellDiscordWebhooks",
                column: "LinkshellId");

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
                name: "IX_Notifications_AppUserId",
                table: "Notifications",
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
                name: "IX_PartySetups_LinkshellId_AssignedMonsterName",
                table: "PartySetups",
                columns: new[] { "LinkshellId", "AssignedMonsterName" });

            migrationBuilder.CreateIndex(
                name: "IX_PartySetups_LinkshellId_Name",
                table: "PartySetups",
                columns: new[] { "LinkshellId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_PartySetupSlots_PartySetupPartyId_SortOrder",
                table: "PartySetupSlots",
                columns: new[] { "PartySetupPartyId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceSnapshotEntries_PendingAttendanceSnapshotS~",
                table: "PendingAttendanceSnapshotEntries",
                column: "PendingAttendanceSnapshotSubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceSnapshotSubmissions_LinkedEventId",
                table: "PendingAttendanceSnapshotSubmissions",
                column: "LinkedEventId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceSnapshotSubmissions_LinkshellId",
                table: "PendingAttendanceSnapshotSubmissions",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceSnapshotSubmissions_SubmittedByAppUserId",
                table: "PendingAttendanceSnapshotSubmissions",
                column: "SubmittedByAppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceWindowMemberSubmissions_PendingAttendanceW~",
                table: "PendingAttendanceWindowMemberSubmissions",
                column: "PendingAttendanceWindowSubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceWindowSubmissions_EventId",
                table: "PendingAttendanceWindowSubmissions",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceWindowSubmissions_LinkshellId",
                table: "PendingAttendanceWindowSubmissions",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceWindowSubmissions_SubmittedByAppUserId",
                table: "PendingAttendanceWindowSubmissions",
                column: "SubmittedByAppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTodLootSubmissions_PendingTodSubmissionId",
                table: "PendingTodLootSubmissions",
                column: "PendingTodSubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTodSubmissions_LinkshellId",
                table: "PendingTodSubmissions",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTodSubmissions_SubmittedByAppUserId",
                table: "PendingTodSubmissions",
                column: "SubmittedByAppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueEntries_LinkshellId_OccurredAt",
                table: "RevenueEntries",
                columns: new[] { "LinkshellId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Rules_LinkshellId_CreatedAt",
                table: "Rules",
                columns: new[] { "LinkshellId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TodLootDetails_TodId",
                table: "TodLootDetails",
                column: "TodId");

            migrationBuilder.CreateIndex(
                name: "IX_Tods_LinkshellId_MonsterName",
                table: "Tods",
                columns: new[] { "LinkshellId", "MonsterName" });

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
                name: "IX_WindowEvents_LinkshellId_LastCapturedAtUtc",
                table: "WindowEvents",
                columns: new[] { "LinkshellId", "LastCapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WindowEvents_LinkshellId_Status_NormalizedName",
                table: "WindowEvents",
                columns: new[] { "LinkshellId", "Status", "NormalizedName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AttendanceSnapshotEntries");

            migrationBuilder.DropTable(
                name: "Bids");

            migrationBuilder.DropTable(
                name: "ClaimShieldCaptureMembers");

            migrationBuilder.DropTable(
                name: "DiscordActivityUsers");

            migrationBuilder.DropTable(
                name: "DkpLedgerEntries");

            migrationBuilder.DropTable(
                name: "EventLootDetails");

            migrationBuilder.DropTable(
                name: "EventPartySlotSignups");

            migrationBuilder.DropTable(
                name: "Invites");

            migrationBuilder.DropTable(
                name: "LinkshellDiscordChannels");

            migrationBuilder.DropTable(
                name: "LinkshellDiscordWebhooks");

            migrationBuilder.DropTable(
                name: "LinkshellRoles");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "PendingAttendanceSnapshotEntries");

            migrationBuilder.DropTable(
                name: "PendingAttendanceWindowMemberSubmissions");

            migrationBuilder.DropTable(
                name: "PendingTodLootSubmissions");

            migrationBuilder.DropTable(
                name: "RevenueEntries");

            migrationBuilder.DropTable(
                name: "Rules");

            migrationBuilder.DropTable(
                name: "TodLootDetails");

            migrationBuilder.DropTable(
                name: "WindowEventMemberDkps");

            migrationBuilder.DropTable(
                name: "AppUserEvents");

            migrationBuilder.DropTable(
                name: "EventAttendanceWindows");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AttendanceSnapshots");

            migrationBuilder.DropTable(
                name: "AuctionItems");

            migrationBuilder.DropTable(
                name: "ClaimShieldCaptures");

            migrationBuilder.DropTable(
                name: "EventHistories");

            migrationBuilder.DropTable(
                name: "PartySetupSlots");

            migrationBuilder.DropTable(
                name: "PendingAttendanceSnapshotSubmissions");

            migrationBuilder.DropTable(
                name: "PendingAttendanceWindowSubmissions");

            migrationBuilder.DropTable(
                name: "PendingTodSubmissions");

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
                name: "Events");

            migrationBuilder.DropTable(
                name: "PartySetupAlliances");

            migrationBuilder.DropTable(
                name: "Tods");

            migrationBuilder.DropTable(
                name: "PartySetups");

            migrationBuilder.DropTable(
                name: "Linkshells");

            migrationBuilder.DropTable(
                name: "AspNetUsers");
        }
    }
}
