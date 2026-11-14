using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Controllers.Discord;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace LinkshellManager.Tests;

// Sends one Discord interaction through the REAL POST /api/discord/interactions action, signed
// the way Discord signs it, against a small seeded linkshell, and describes what came back: the
// response body, whether the ephemeral picker was set to be deleted, which boards were queued for
// a refresh, and how many sign-up rows exist afterwards.
//
// Built for the interaction-routing snapshot (DiscordInteractionRoutingSnapshotTests): the
// controller's handler code is being split into smaller classes, and every button already posted
// in Discord must keep reaching the same code and getting the same answer.
internal static class DiscordInteractionHarness
{
    public const int EventId = 10;          // windowed HNM board (Tiamat) with a party setup
    public const int AdHocEventId = 20;     // plain event, no party setup
    public const int ManualCheckInEventId = 30; // Manual Check In camp (Fafnir), no party setup
    public const int PastCampHistoryId = 5; // an ended camp's EventHistory
    public const int OpenSlotId = 1;        // nothing pinned -> the job wizard
    public const int PinnedSlotId = 2;      // Tank PLD/WAR, held by the member below
    public const string GuildId = "g1";

    // Who clicks. "unlinked" has no LSM account; "member" and "officer" are linked to one.
    public static readonly string[] Clickers = { "unlinked", "member", "officer" };

    private static readonly Ed25519PrivateKeyParameters SigningKey = new(Enumerable.Repeat((byte)7, 32).ToArray(), 0);
    private static readonly string PublicKeyHex =
        Convert.ToHexString(SigningKey.GeneratePublicKey().GetEncoded()).ToLowerInvariant();

    private static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DiscordIdFor(string clicker) => clicker switch
    {
        "member" => "d1",
        "officer" => "d2",
        _ => "d9",
    };

    // ---- Payloads, in the shapes Discord sends.

    public static JsonObject Component(string clicker, string customId, string[]? values = null, bool fromEphemeral = false)
    {
        var data = new JsonObject { ["custom_id"] = customId, ["component_type"] = values is null ? 2 : 3 };
        if (values is not null)
        {
            data["values"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }
        var payload = Envelope(3, clicker);
        payload["message"] = new JsonObject { ["flags"] = fromEphemeral ? 64 : 0 };
        payload["data"] = data;
        return payload;
    }

    public static JsonObject Modal(string clicker, string customId, params (string Field, string Value)[] fields)
    {
        var rows = new JsonArray(fields
            .Select(field => (JsonNode?)new JsonObject
            {
                ["type"] = 1,
                ["components"] = new JsonArray(new JsonObject
                {
                    ["type"] = 4,
                    ["custom_id"] = field.Field,
                    ["value"] = field.Value,
                }),
            })
            .ToArray());
        var payload = Envelope(5, clicker);
        payload["data"] = new JsonObject { ["custom_id"] = customId, ["components"] = rows };
        return payload;
    }

    public static JsonObject Command(string clicker, string name, bool inGuild = true)
    {
        var payload = Envelope(2, clicker);
        if (!inGuild)
        {
            payload.Remove("guild_id");
        }
        payload["data"] = new JsonObject { ["name"] = name, ["type"] = 1 };
        return payload;
    }

    public static JsonObject Bare(int type) => new() { ["type"] = type, ["token"] = "tok" };

    private static JsonObject Envelope(int type, string clicker)
    {
        var discordId = DiscordIdFor(clicker);
        return new JsonObject
        {
            ["type"] = type,
            ["token"] = "tok",
            ["guild_id"] = GuildId,
            ["member"] = new JsonObject
            {
                ["user"] = new JsonObject
                {
                    ["id"] = discordId,
                    ["username"] = $"user{discordId}",
                    ["global_name"] = $"Display {discordId}",
                },
            },
        };
    }

    // ---- Running one interaction.

    public sealed record Outcome(
        IActionResult Result, string Body, int DismissCallbacks, IReadOnlyList<int> Queued,
        int Participations, int SlotSignups, string? Threw)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
        public int ResponseType => Json.GetProperty("type").GetInt32();
        public int? Flags => Json.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("flags", out var flags) ? flags.GetInt32() : null;
        public string? Content => Json.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("content", out var content) ? content.GetString() : null;

        // One line: everything a caller could observe.
        public string Describe() =>
            $"{Body}"
            + (DismissCallbacks > 0 ? $" | dismiss={DismissCallbacks}" : string.Empty)
            + (Queued.Count > 0 ? $" | queued=[{string.Join(",", Queued)}]" : string.Empty)
            + $" | rows={Participations}/{SlotSignups}"
            + (Threw is null ? string.Empty : $" | threw={Threw}");
    }

    public static async Task<Outcome> SendAsync(JsonObject payload, bool signed = true)
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedAsync(dbName);

        var logger = new CapturingLogger<DiscordInteractionsController>();
        var services = BuildServices(dbName, logger);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var rawBody = payload.ToJsonString();
        var timestamp = "1700000000";
        var response = new RecordingResponseFeature();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Features.Set<IHttpResponseFeature>(response);
        http.Request.Method = "POST";
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("lsm.test");
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(rawBody));
        http.Request.Headers["X-Signature-Timestamp"] = timestamp;
        http.Request.Headers["X-Signature-Ed25519"] = signed ? Sign(timestamp + rawBody) : new string('0', 128);

        var controller = ActivatorUtilities.CreateInstance<DiscordInteractionsController>(scope.ServiceProvider);
        controller.ControllerContext = new ControllerContext(new Microsoft.AspNetCore.Mvc.ActionContext(
            http, new RouteData(), new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()));

        var result = await controller.HandleAsync(CancellationToken.None);

        var queue = provider.GetRequiredService<DiscordEventChannelQueue>();
        var queued = new List<int>();
        while (queue.Reader.TryRead(out var id))
        {
            queued.Add(id);
        }

        await using var check = NewDb(dbName);
        return new Outcome(
            result,
            DescribeResult(result),
            response.CompletedCallbacks,
            queued,
            await check.AppUserEvents.CountAsync(),
            await check.EventPartySlotSignups.CountAsync(),
            logger.LastException?.GetType().Name);
    }

    private static string DescribeResult(IActionResult result) => result switch
    {
        OkObjectResult ok => JsonSerializer.Serialize(ok.Value, Wire),
        StatusCodeResult status => $"{{\"status\":{status.StatusCode}}}",
        _ => $"{{\"result\":\"{result.GetType().Name}\"}}",
    };

    private static string Sign(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, SigningKey);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return Convert.ToHexString(signer.GenerateSignature()).ToLowerInvariant();
    }

    // The services the interaction endpoint asks for, as Program.cs registers them, over an
    // in-memory database. ManualMemberService gets no UserManager: the only path that uses it
    // (an outside player registering) is closed here because outside sign-ups are off.
    private static ServiceCollection BuildServices(string dbName, ILogger<DiscordInteractionsController> logger)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(logger);
        services.AddHttpClient();
        services.AddMemoryCache();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new DiscordOAuthOptions
        {
            ClientId = string.Empty, // no outbound "delete the picker" call from a test
            ClientSecret = "test",
            PublicKey = PublicKeyHex,
        }));
        services.AddSingleton<DiscordEventChannelQueue>();
        services.AddScoped(_ => NewDb(dbName));
        services.AddSingleton<IDateTimeZoneProvider>(DateTimeZoneProviders.Tzdb);
        services.AddSingleton<TimeZoneConversionService>();
        services.AddSingleton<SignupCharacterChoiceCache>();
        services.AddSingleton<OfficerAddTargetCache>();
        services.AddSingleton<DiscordInteractionVerifier>();
        services.AddScoped<GlobalSettingsService>();
        services.AddScoped<AdminOverrideService>();
        services.AddScoped<LinkshellPermissions>();
        services.AddScoped<DkpPoolProvisioner>();
        services.AddScoped<DkpPoolResolver>();
        services.AddScoped<DkpPoolBalanceService>();
        services.AddScoped<MonsterTimingResolver>();
        services.AddScoped(sp => new ManualMemberService(sp.GetRequiredService<ApplicationDbContext>(), null!, null!));
        services.AddScoped<DiscordInteractionContext>();
        services.AddScoped<DiscordSignupIdentity>();
        services.AddScoped<DiscordPermissionGates>();
        services.AddScoped<DiscordBoardSupport>();
        services.AddScoped<PartyBoardHandler>();
        services.AddScoped<HnmCampBoardHandler>();
        services.AddScoped<EndCampHandler>();
        services.AddScoped<AuctionInteractionHandler>();
        services.AddScoped<OfficerToolsHandler>();
        return services;
    }

    private static ApplicationDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    // One linkshell (outside sign-ups off) linked to guild g1, with a member (d1 -> u1, seated in
    // the pinned slot of event 10) and an officer (d2 -> u2). Event 10 is a Tiamat camp with a
    // one-alliance, one-party, two-slot setup, on window 3 with windows 1-2 captured; event 20 is a
    // plain event with no setup; event 30 is a Manual Check In camp; history 5 is an ended camp.
    private static async Task SeedAsync(string dbName)
    {
        await using var db = NewDb(dbName);
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "LS1", LootStructure = "Dkp", DiscordGuildId = GuildId });
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(1));
        db.Users.Add(new AppUser { Id = "u1", UserName = "u1", CharacterName = "Mill" });
        db.Users.Add(new AppUser { Id = "u2", UserName = "u2", CharacterName = "Offy" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { AppUserId = "u1", LinkshellId = 1, Rank = LinkshellRanks.Member, CharacterName = "Mill", Status = "Active" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { AppUserId = "u2", LinkshellId = 1, Rank = LinkshellRanks.Officer, CharacterName = "Offy", Status = "Active" });
        db.DiscordActivityUsers.Add(new DiscordActivityUser { DiscordUserId = "d1", Username = "userd1", IdentityUserId = "u1" });
        db.DiscordActivityUsers.Add(new DiscordActivityUser { DiscordUserId = "d2", Username = "userd2", IdentityUserId = "u2" });

        var setup = new PartySetup
        {
            Id = 1,
            LinkshellId = 1,
            Name = "Setup",
            Alliances =
            {
                new PartySetupAlliance
                {
                    Id = 1, SortOrder = 0, Name = "A1",
                    Parties =
                    {
                        new PartySetupParty
                        {
                            Id = 1, SortOrder = 0, Name = "P1",
                            Slots =
                            {
                                new PartySetupSlot { Id = OpenSlotId, SortOrder = 0 },
                                new PartySetupSlot { Id = PinnedSlotId, SortOrder = 1, RequirementType = "Job", Role = "Tank", MainJob = "PLD", SubJob = "WAR" },
                            },
                        },
                    },
                },
            },
        };
        db.PartySetups.Add(setup);
        db.Events.Add(new Event
        {
            Id = EventId, LinkshellId = 1, EventName = "Tiamat Camp", EventType = "HNM",
            AssignedMonsterName = "Tiamat", PartySetupId = 1, HnmWindowNumber = 3,
            StartTime = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        });
        db.Events.Add(new Event
        {
            Id = AdHocEventId, LinkshellId = 1, EventName = "Dynamis", EventType = "Event",
            StartTime = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        });
        db.Events.Add(new Event
        {
            Id = ManualCheckInEventId, LinkshellId = 1, EventName = "Fafnir Camp", EventType = "HNM",
            AssignedMonsterName = "Fafnir", AttendanceMode = HnmAttendanceModes.Wd, HnmWindowNumber = 2,
            StartTime = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        });
        // Rosters the window turnover captured for event 10's first two windows.
        foreach (var window in new[] { 1, 2 })
        {
            db.EventWindowRosterSnapshots.Add(new EventWindowRosterSnapshot
            {
                EventId = EventId, WindowNumber = window,
                CapturedAtUtc = new DateTime(2030, 1, 1, 12, window, 0, DateTimeKind.Utc),
                AllianceName = "A1", PartyName = "P1", SlotSortOrder = 1,
                CharacterName = "Mill", Role = "Tank", MainJob = "PLD", SubJob = "WAR", WasLocked = window == 2,
            });
        }
        // A camp that already ended, for "View Camp Details".
        db.EventHistories.Add(new EventHistory
        {
            Id = PastCampHistoryId, LinkshellId = 1, EventName = "Old Tiamat", EventType = "HNM",
            StartTime = new DateTime(2029, 12, 1, 12, 0, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2029, 12, 1, 14, 0, 0, DateTimeKind.Utc),
        });
        db.EventPartySlotSignups.Add(new EventPartySlotSignup
        {
            EventId = EventId, PartySetupSlotId = PinnedSlotId, AppUserId = "u1", CharacterName = "Mill",
            Role = "Tank", MainJob = "PLD", SubJob = "WAR",
            SignedUpAtUtc = new DateTime(2029, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
    }

    private sealed class RecordingResponseFeature : HttpResponseFeature
    {
        public int CompletedCallbacks { get; private set; }

        public override void OnCompleted(Func<object, Task> callback, object state) => CompletedCallbacks++;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public Exception? LastException { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                LastException = exception;
            }
        }
    }
}
