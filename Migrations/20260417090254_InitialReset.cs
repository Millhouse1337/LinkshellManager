using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class InitialReset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    TimeZone = table.Column<string>(type: "text", nullable: true),
                    PrimaryLinkshellId = table.Column<int>(type: "integer", nullable: true),
                    PrimaryLinkshellName = table.Column<string>(type: "text", nullable: true),
                    ProfileImage = table.Column<byte[]>(type: "bytea", nullable: true),
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
                    Details = table.Column<string>(type: "text", nullable: true)
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
                        name: "FK_AppUserLinkshells_Linkshells_LinkshellId",
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
                    table.PrimaryKey("PK_Events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Events_Linkshells_LinkshellId",
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
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
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
                name: "EventLootDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    ItemName = table.Column<string>(type: "text", nullable: true),
                    ItemWinner = table.Column<string>(type: "text", nullable: true),
                    WinningDkpSpent = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventLootDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventLootDetails_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    JobName = table.Column<string>(type: "text", nullable: true),
                    SubJobName = table.Column<string>(type: "text", nullable: true),
                    JobType = table.Column<string>(type: "text", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: true),
                    SignedUp = table.Column<int>(type: "integer", nullable: true),
                    Enlisted = table.Column<List<string>>(type: "text[]", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Jobs_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                name: "IX_DiscordActivityUsers_DiscordUserId",
                table: "DiscordActivityUsers",
                column: "DiscordUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordActivityUsers_IdentityUserId",
                table: "DiscordActivityUsers",
                column: "IdentityUserId");

            migrationBuilder.CreateIndex(
                name: "IX_EventHistories_LinkshellId",
                table: "EventHistories",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_EventLootDetails_EventId",
                table: "EventLootDetails",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_LinkshellId",
                table: "Events",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_AppUserId",
                table: "Invites",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_LinkshellId",
                table: "Invites",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_EventId",
                table: "Jobs",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Linkshells_AppUserId",
                table: "Linkshells",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_AppUserId",
                table: "Notifications",
                column: "AppUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppUserEventHistories");

            migrationBuilder.DropTable(
                name: "AppUserEvents");

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
                name: "DiscordActivityUsers");

            migrationBuilder.DropTable(
                name: "EventLootDetails");

            migrationBuilder.DropTable(
                name: "Invites");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "EventHistories");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "Linkshells");

            migrationBuilder.DropTable(
                name: "AspNetUsers");
        }
    }
}
