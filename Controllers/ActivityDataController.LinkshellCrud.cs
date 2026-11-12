using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    [HttpPost("linkshells")]
    public async Task<IActionResult> CreateLinkshellAsync(
        [FromBody] ActivityCreateLinkshellRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Linkshell name is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to create a linkshell."
            });
        }

        var trimmedName = request.Name.Trim();
        var duplicateLinkshell = await _dbContext.Linkshells
            .AnyAsync(
                linkshell => linkshell.AppUserId == appUser.Id && linkshell.LinkshellName == trimmedName,
                cancellationToken);

        if (duplicateLinkshell)
        {
            return BadRequest(new { error = "A linkshell with that name already exists for the current app user." });
        }

        var linkshell = new Linkshell
        {
            AppUserId = appUser.Id,
            LinkshellName = trimmedName,
            Details = request.Details?.Trim(),
            Status = "Active"
        };

        _dbContext.Linkshells.Add(linkshell);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _dbContext.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = appUser.Id,
            LinkshellId = linkshell.Id,
            CharacterName = appUser.CharacterName ?? appUser.UserName,
            Rank = LinkshellRanks.Leader,
            Status = "Active",
            LinkshellDkp = 0,
            DateJoined = DateTime.UtcNow
        });

        appUser.PrimaryLinkshellId ??= linkshell.Id;

        // UserManager.UpdateAsync uses the same DbContext (Identity is wired to ApplicationDbContext)
        // and calls SaveChangesAsync internally, which flushes the AppUserLinkshell add too.
        await _userManager.UpdateAsync(appUser);

        return Ok(new { success = true, linkshellId = linkshell.Id });
    }

    [HttpGet("linkshells/{linkshellId:int}")]
    public async Task<IActionResult> GetLinkshellDetailAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load linkshell details."
            });
        }

        // GetMembershipAsync also enforces the per-linkshell Discord guild lock.
        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells
            .Include(item => item.AppUserLinkshells)
            .ThenInclude(link => link.AppUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);

        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        // Current credit / absent streaks per member (consecutive recent counting
        // events) so the Linkshell-tab + sidebar roster show the same active-credit /
        // absent-streak badges the Overview's primary roster does. Without this the
        // detail roster always rendered "0".
        var streaks = await new MemberActivityService(_dbContext)
            .ComputeStreaksByAppUserAsync(linkshellId, cancellationToken);

        // Which members have actually opened/synced the Discord Activity (a
        // DiscordActivityUser row points at their AppUserId) — used for the roster's
        // "App Sync" badge + filter so officers can tell real app users apart from
        // members who only ever used the outside sign-up board.
        var memberAppUserIds = linkshell.AppUserLinkshells
            .Select(link => link.AppUserId)
            .Where(id => id != null)
            .Select(id => id!)
            .Distinct()
            .ToList();
        var syncedAppUserIds = memberAppUserIds.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await _dbContext.DiscordActivityUsers
                .Where(d => d.IdentityUserId != null && memberAppUserIds.Contains(d.IdentityUserId))
                .Select(d => d.IdentityUserId!)
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

        // Whether to badge super-admin rows with ADMIN. One cached bool for the whole
        // roster — AppUser is already Included, so this costs no extra query.
        var adminOverrideEnabled = await _adminOverride.IsEnabledAsync(cancellationToken);

        // Member Status is now the single source of truth (the attendance rule
        // auto-drives Active/Inactive at close — see MemberActivityService), so the
        // roster just surfaces Status; no separate computed activity flag.
        return Ok(new ActivityLinkshellDetailDto(
            linkshell.Id,
            linkshell.LinkshellName ?? "Unknown linkshell",
            linkshell.AppUserLinkshells.Count,
            linkshell.Details,
            linkshell.Status,
            linkshell.AppUserLinkshells
                .OrderBy(link => link.CharacterName)
                .Select(link => new ActivityMemberDto(
                    link.Id,
                    link.AppUserId,
                    link.CharacterName ?? link.AppUser?.UserName ?? "Unknown member",
                    link.AppUser?.AltCharacterName1,
                    link.AppUser?.AltCharacterName2,
                    link.Rank,
                    link.Status,
                    link.LinkshellDkp,
                    link.DateJoined,
                    link.AppUserId != null ? streaks.GetValueOrDefault(link.AppUserId).Credit : 0,
                    link.AppUserId != null ? streaks.GetValueOrDefault(link.AppUserId).Absent : 0,
                    IsPlaceholder: link.AppUser?.IsPlaceholder ?? false,
                    HasSyncedActivity: link.AppUserId != null && syncedAppUserIds.Contains(link.AppUserId),
                    IsAdmin: adminOverrideEnabled && (link.AppUser?.IsSuperAdmin ?? false)))
                .ToList()));
    }

    [HttpPost("linkshells/{linkshellId:int}/primary")]
    public async Task<IActionResult> SetPrimaryLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update the primary linkshell."
            });
        }

        var membership = await _dbContext.AppUserLinkshells
            .Include(link => link.Linkshell)
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == appUser.Id && link.LinkshellId == linkshellId, cancellationToken);

        if (membership?.Linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell membership was not found." });
        }

        appUser.PrimaryLinkshellId = membership.LinkshellId;

        await _userManager.UpdateAsync(appUser);

        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/update")]
    public async Task<IActionResult> UpdateLinkshellAsync(
        int linkshellId,
        [FromBody] ActivityUpdateLinkshellRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Linkshell name is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update the linkshell."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var trimmedName = request.Name.Trim();
        var duplicate = await _dbContext.Linkshells
            .AnyAsync(
                item => item.Id != linkshellId &&
                        item.AppUserId == linkshell.AppUserId &&
                        item.LinkshellName == trimmedName,
                cancellationToken);

        if (duplicate)
        {
            return BadRequest(new { error = "Another linkshell with that name already exists." });
        }

        linkshell.LinkshellName = trimmedName;
        linkshell.Details = request.Details?.Trim();

        if (!string.IsNullOrWhiteSpace(request.LootStructure))
        {
            var requestedStructure = request.LootStructure.Trim();
            if (!IsValidLootStructure(requestedStructure))
            {
                return BadRequest(new { error = "Loot Structure must be Dkp, LootCouncil, or Hybrid." });
            }
            linkshell.LootStructure = NormalizeLootStructure(requestedStructure);
        }

        if (request.EnableHnmSection.HasValue) linkshell.EnableHnmSection = request.EnableHnmSection.Value;
        if (request.EnableAuctions.HasValue) linkshell.EnableAuctions = request.EnableAuctions.Value;
        if (request.EnableToDs.HasValue) linkshell.EnableToDs = request.EnableToDs.Value;
        if (request.EnableEndgame.HasValue) linkshell.EnableEndgame = request.EnableEndgame.Value;
        if (request.EnableEvents.HasValue) linkshell.EnableEvents = request.EnableEvents.Value;
        if (request.EnableDkp.HasValue) linkshell.EnableDkp = request.EnableDkp.Value;
        if (request.EnableTreasury.HasValue) linkshell.EnableTreasury = request.EnableTreasury.Value;
        if (!string.IsNullOrWhiteSpace(request.DkpRoundingIncrement))
        {
            linkshell.DkpRoundingIncrement = NormalizeDkpRounding(request.DkpRoundingIncrement);
        }
        // null in the request = leave unchanged. An explicitly empty list
        // clears the hidden-mob list. The serializer trims, drops blanks,
        // and de-dupes so the stored value stays clean.
        if (request.HiddenTodMonsters is not null)
        {
            linkshell.HiddenTodMonsters = SerializeHiddenTodMonsters(request.HiddenTodMonsters);
        }
        // (TodMonsterTimings was written here. Per-monster setups live in the LinkshellMonsterTimings
        // table now and save through their own endpoint; the column is kept, unwritten, only so
        // LinkshellMonsterTimingProvisioner can import a linkshell's old values on first seed.)
        // Member activity tracking (null = leave unchanged). Thresholds clamp to >= 1
        // to match MemberActivityService's own guard.
        if (request.EnableActivityTracking.HasValue)
        {
            linkshell.EnableActivityTracking = request.EnableActivityTracking.Value;
        }
        if (request.OutsidePartySignupEnabled.HasValue)
        {
            linkshell.OutsidePartySignupEnabled = request.OutsidePartySignupEnabled.Value;
        }
        if (request.InactiveAfterAbsences.HasValue)
        {
            linkshell.InactiveAfterAbsences = Math.Max(1, request.InactiveAfterAbsences.Value);
        }
        if (request.ActiveAfterAttendances.HasValue)
        {
            linkshell.ActiveAfterAttendances = Math.Max(1, request.ActiveAfterAttendances.Value);
        }
        // null in the request = leave unchanged. Empty/whitespace clears the
        // server association (and, with it, the access lock). A non-empty value
        // must be a Discord snowflake (digits only, <= 20 chars) and associates
        // the linkshell with that server WITHOUT locking access (the separate
        // set-guild-lock endpoint governs the lock). Server-setting normally goes
        // through the dedicated set-guild/clear-guild endpoints now; this branch
        // remains for clients that still send DiscordGuildId on the main update.
        // It gets the same checks as set-guild/clear-guild: the bot reads the tied
        // server's channels and members and posts there, so typing a stranger's
        // server id here must not be enough to tie this linkshell to it.
        switch (ClassifyGuildIdUpdate(request.DiscordGuildId, linkshell.DiscordGuildId))
        {
            case GuildIdUpdate.Invalid:
                return BadRequest(new { error = "Discord Server ID must be the numeric server ID (digits only). Leave blank to clear it." });
            case GuildIdUpdate.Clear:
                if (IsBlockedByGuildLock(linkshell))
                {
                    return Forbid();
                }
                linkshell.DiscordGuildId = null;
                linkshell.DiscordGuildName = null;
                linkshell.LockToDiscordGuild = false;
                break;
            case GuildIdUpdate.Set:
                if (IsBlockedByGuildLock(linkshell))
                {
                    return Forbid();
                }
                var newGuildId = request.DiscordGuildId!.Trim();
                if (!await CallerIsInGuildAsync(appUser.Id, newGuildId, cancellationToken))
                {
                    return BadRequest(new { error = "You must be a member of the server you set." });
                }
                linkshell.DiscordGuildId = newGuildId;
                linkshell.DiscordGuildName = (await _discordIdentityService.ListBotGuildsAsync(cancellationToken))
                    ?.FirstOrDefault(guild => guild.Id == newGuildId)?.Name;
                break;
        }

        // Manual Check In HNM attendance (null = leave unchanged). Mode fails closed to Standard;
        // scoring values clamp to >= 0.
        if (!string.IsNullOrWhiteSpace(request.HnmAttendanceMode))
        {
            if (!HnmAttendanceModes.IsValid(request.HnmAttendanceMode.Trim()))
            {
                return BadRequest(new { error = "HNM Attendance Mode must be Standard or Wd." });
            }
            var previousMode = HnmAttendanceModes.Normalize(linkshell.HnmAttendanceMode);
            linkshell.HnmAttendanceMode = HnmAttendanceModes.Normalize(request.HnmAttendanceMode);

            // Event.AttendanceMode is stamped per camp at creation, so without this the flip only
            // affected boards created afterwards — an officer who switched to Manual Check In saw
            // nothing change on the board sitting in front of them. Re-stamp the camps that haven't
            // started; running ones keep their mode until they pop (switching mid-camp would strand
            // the attendance already collected — see ReStampNotStartedCampsAsync).
            //
            // Only on an ACTUAL change: otherwise every unrelated Customize save would mark each
            // queued camp Modified and enqueue a Discord board re-render for it. Staged here so the
            // linkshell row, the re-stamps, and the save-hook's board edits share one save below.
            if (!string.Equals(previousMode, linkshell.HnmAttendanceMode, StringComparison.Ordinal))
            {
                await HnmEventSeeder.ReStampNotStartedCampsAsync(
                    _dbContext, linkshellId, linkshell.HnmAttendanceMode, cancellationToken);
            }
        }
        if (request.WdDkpPerWindow.HasValue) linkshell.WdDkpPerWindow = Math.Max(0d, request.WdDkpPerWindow.Value);
        if (request.WdClaimBonus.HasValue) linkshell.WdClaimBonus = Math.Max(0d, request.WdClaimBonus.Value);
        if (request.WdKillBonus.HasValue) linkshell.WdKillBonus = Math.Max(0d, request.WdKillBonus.Value);
        if (request.WdOpenBonus.HasValue) linkshell.WdOpenBonus = Math.Max(0d, request.WdOpenBonus.Value);
        if (request.WdCloseBonus.HasValue) linkshell.WdCloseBonus = Math.Max(0d, request.WdCloseBonus.Value);

        // Standard-mode HNM bonuses (null = leave unchanged), clamped to >= 0 like the Manual Check In values.
        if (request.HnmStandardOpenBonus.HasValue) linkshell.HnmStandardOpenBonus = Math.Max(0d, request.HnmStandardOpenBonus.Value);
        if (request.HnmStandardCloseBonus.HasValue) linkshell.HnmStandardCloseBonus = Math.Max(0d, request.HnmStandardCloseBonus.Value);
        if (request.HnmStandardClaimBonus.HasValue) linkshell.HnmStandardClaimBonus = Math.Max(0d, request.HnmStandardClaimBonus.Value);
        if (request.HnmStandardKillBonus.HasValue) linkshell.HnmStandardKillBonus = Math.Max(0d, request.HnmStandardKillBonus.Value);
        if (request.HnmStandardWindowBonus.HasValue) linkshell.HnmStandardWindowBonus = Math.Max(0d, request.HnmStandardWindowBonus.Value);
        if (request.HnmStandardMiscBonus.HasValue) linkshell.HnmStandardMiscBonus = Math.Max(0d, request.HnmStandardMiscBonus.Value);

        // No copy of the new name to update on members' accounts: the primary linkshell's name is
        // read from this row wherever it is shown.
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Enabling tracking or changing thresholds can change who's Inactive →
        // recompute member statuses now (no-op when tracking is off).
        await _memberActivity.ApplyComputedStatusAsync(linkshellId, cancellationToken);

        return Ok(new { success = true });
    }

    // Set (or clear) the channel new post-event discussion comments mirror to.
    // Gated on CanCustomizeLinkshell. Empty/blank clears it (discussion stays in-app).
    [HttpPost("linkshells/{linkshellId:int}/discussion-channel")]
    public async Task<IActionResult> SetDiscussionChannelAsync(
        int linkshellId,
        [FromBody] ActivitySetDiscussionChannelRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to set the discussion channel." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var trimmed = request.ChannelId?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && (trimmed.Length > 20 || !trimmed.All(char.IsDigit)))
        {
            return BadRequest(new { error = "Channel ID must be the numeric Discord channel ID (digits only), or blank to clear." });
        }

        // The bot mirrors comments into this channel, so it must be one of the linkshell's
        // own server's channels — not any id typed in (same rule as the channel routes).
        if (!string.IsNullOrEmpty(trimmed))
        {
            var channelError = ChannelRouteEditor.CheckChannelsInGuild(
                new[] { trimmed },
                await ListGuildChannelsForCheckAsync(linkshell.DiscordGuildId, new[] { trimmed }, cancellationToken));
            if (channelError is not null)
            {
                return BadRequest(new { error = channelError });
            }
        }

        linkshell.DiscussionChannelId = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // The Discord servers the caller can lock a linkshell to: the bot's servers
    // that the caller is also a member of.
    [HttpGet("eligible-guilds")]
    public async Task<IActionResult> GetEligibleGuildsAsync(CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to list Discord servers." });
        }

        var discordUserId = await _dbContext.DiscordActivityUsers
            .Where(link => link.IdentityUserId == appUser.Id)
            .Select(link => link.DiscordUserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(discordUserId))
        {
            return Ok(Array.Empty<ActivityGuildOptionDto>());
        }

        var botGuilds = await _discordIdentityService.ListBotGuildsAsync(cancellationToken);
        if (botGuilds is null || botGuilds.Count == 0)
        {
            return Ok(Array.Empty<ActivityGuildOptionDto>());
        }

        // Cap per-guild membership checks so a bot in many servers doesn't fan out
        // into dozens of Discord calls.
        const int maxChecks = 25;
        var eligible = new List<ActivityGuildOptionDto>();
        foreach (var guild in botGuilds.Take(maxChecks))
        {
            if (await _discordIdentityService.IsUserInGuildAsync(guild.Id, discordUserId, cancellationToken))
            {
                eligible.Add(new ActivityGuildOptionDto(guild.Id, guild.Name));
            }
        }

        return Ok(eligible);
    }

    // Associate this linkshell with a Discord server (does NOT lock access — that's
    // the separate set-guild-lock endpoint). Prefers a server chosen from the
    // eligible-guilds dropdown (request.GuildId, verified the caller is in it);
    // falls back to the guild the Activity is launched in (header) when none is
    // chosen. Requires the CanCustomizeLinkshell permission.
    [HttpPost("linkshells/{linkshellId:int}/set-guild")]
    public async Task<IActionResult> SetLinkshellGuildAsync(
        int linkshellId,
        [FromBody] ActivitySetGuildRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to set the linkshell's Discord server." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var resolvedName = request.GuildName?.Trim();

        // A server picked from the dropdown, or (no pick) the server the Activity is
        // launched in. That second one comes from the X-Discord-Guild-Id header, which
        // the client sends and could set to anything, so BOTH get the same checks:
        // a real snowflake, and the caller is actually a member of that server.
        var guildId = PickGuildToSet(request.GuildId, GetRequestGuildId());
        if (guildId is null)
        {
            return BadRequest(new { error = "Pick a server from the list, or open the Activity inside the server you want to set." });
        }
        if (!ChannelRouteEditor.IsSnowflake(guildId))
        {
            return BadRequest(new { error = "Invalid Discord server selection." });
        }
        if (!await CallerIsInGuildAsync(appUser.Id, guildId, cancellationToken))
        {
            return BadRequest(new { error = "You must be a member of the server you set." });
        }

        if (string.IsNullOrWhiteSpace(resolvedName))
        {
            var botGuilds = await _discordIdentityService.ListBotGuildsAsync(cancellationToken);
            resolvedName = botGuilds?.FirstOrDefault(guild => guild.Id == guildId)?.Name;
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        // If the linkshell is currently *locked*, only someone in the locked guild
        // may change which server it's tied to (else they'd lock others out).
        if (IsBlockedByGuildLock(linkshell))
        {
            return Forbid();
        }

        linkshell.DiscordGuildId = guildId;
        linkshell.DiscordGuildName = string.IsNullOrWhiteSpace(resolvedName) ? null : resolvedName;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Clear the linkshell's Discord server association (also turns off the lock).
    [HttpPost("linkshells/{linkshellId:int}/clear-guild")]
    public async Task<IActionResult> ClearLinkshellGuildAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to clear the linkshell's Discord server." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        // Only someone currently in the locked guild (or with no lock) can change
        // it — IsBlockedByGuildLock guards against clearing from elsewhere.
        if (IsBlockedByGuildLock(linkshell))
        {
            return Forbid();
        }

        linkshell.DiscordGuildId = null;
        linkshell.DiscordGuildName = null;
        linkshell.LockToDiscordGuild = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Toggle the optional access lock. Requires a server already set. When turning
    // the lock ON, the caller must be launched in that server (or be on the website,
    // which the lock doesn't govern) so they don't lock themselves out.
    [HttpPost("linkshells/{linkshellId:int}/set-guild-lock")]
    public async Task<IActionResult> SetLinkshellGuildLockAsync(
        int linkshellId,
        [FromBody] ActivitySetGuildLockRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to change the linkshell's access lock." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        // While a lock is already in effect, only someone in the locked guild may
        // change it (this is what lets an in-guild officer turn the lock OFF).
        if (IsBlockedByGuildLock(linkshell))
        {
            return Forbid();
        }

        if (request.Locked)
        {
            if (string.IsNullOrWhiteSpace(linkshell.DiscordGuildId))
            {
                return BadRequest(new { error = "Set a Discord server before locking access to it." });
            }

            // Don't let the caller lock to a server they aren't in — they'd be
            // blocked immediately. The website (no guild header) may lock since
            // the lock only governs the Activity.
            var requestGuildId = GetRequestGuildId();
            if (requestGuildId is not null &&
                !string.Equals(requestGuildId, linkshell.DiscordGuildId, StringComparison.Ordinal))
            {
                return BadRequest(new { error = "Open the Activity inside the server you want to lock to, then turn on the lock." });
            }
        }

        linkshell.LockToDiscordGuild = request.Locked;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // --- Discord channel routes config. Officers add named routes, pick a
    // channel, and tick which post types (events/loot/auctions/attendance/ToD) the
    // bot posts there — combine several into one channel or split them out. ---

    [HttpGet("linkshells/{linkshellId:int}/discord-channels")]
    public async Task<IActionResult> GetDiscordChannelsAsync(
        int linkshellId, CancellationToken cancellationToken, [FromQuery] bool refresh = false)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage Discord channels." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = membership?.Linkshell
            ?? await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var guild = linkshell.DiscordGuildId;
        var available = string.IsNullOrWhiteSpace(guild)
            ? null
            : await _discordBot.ListTextChannelsAsync(guild, cancellationToken, forceRefresh: refresh);

        var routes = await _dbContext.LinkshellChannelRoutes
            .AsNoTracking()
            .Where(route => route.LinkshellId == linkshellId)
            .OrderBy(route => route.Id)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            guildConfigured = !string.IsNullOrWhiteSpace(guild),
            availableChannels = (available ?? Array.Empty<DiscordChannelInfo>())
                .Select(channel => new { id = channel.Id, name = channel.Name })
                .ToList(),
            postTypes = ChannelPostTypes.All.Select(key => new { key, label = ChannelPostTypes.Label(key) }).ToList(),
            eventTypes = EventTypeVocabulary.All,
            // HNM monster picklist for the per-monster route narrowing (shown when a route
            // includes HNM). Same canonical list the create-event monster dropdown uses.
            monsterOptions = (await _monsterTimings.GetMapAsync(linkshellId, cancellationToken)).EventMonsterOptions,
            routes = routes.Select(route => new
            {
                id = route.Id,
                name = route.Name,
                channelId = route.ChannelId,
                channelName = route.ChannelName,
                postEvents = route.PostEvents,
                postLoot = route.PostLoot,
                postAuctions = route.PostAuctions,
                postTodBoard = route.PostTodBoard,
                postDkpSheet = route.PostDkpSheet,
                eventTypeFilter = SplitEventTypeFilter(route.EventTypeFilter),
                hnmMonsterFilter = SplitEventTypeFilter(route.HnmMonsterFilter),
            }).ToList(),
        });
    }

    [HttpPost("linkshells/{linkshellId:int}/discord-channels")]
    public async Task<IActionResult> SaveDiscordChannelsAsync(
        int linkshellId,
        [FromBody] ActivitySaveChannelRoutesRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage Discord channels." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = membership?.Linkshell
            ?? await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var edits = (request.Routes ?? Array.Empty<ActivityChannelRouteInput>())
            .Select(r => new ChannelRouteEdit(
                r.Id, r.Name, r.ChannelId,
                r.PostEvents, r.PostLoot, r.PostAuctions, r.PostTodBoard, r.PostDkpSheet,
                r.EventTypeFilter, r.HnmMonsterFilter))
            .ToList();

        // The editor rejects any route whose channel isn't in this list.
        var available = await ListGuildChannelsForCheckAsync(
            linkshell.DiscordGuildId,
            edits.Where(e => ChannelRouteEditor.IsSnowflake(e.ChannelId)).Select(e => e.ChannelId!.Trim()),
            cancellationToken);

        var error = await _channelRoutes.SaveAsync(linkshellId, edits, available, cancellationToken);
        if (error is not null)
        {
            return BadRequest(new { error });
        }
        return Ok(new { success = true });
    }

    // The bot's text channels for the linkshell's own server, for checking picked channel
    // ids against. The list is cached for a few minutes, so when an id isn't in it (a
    // channel made in Discord a moment ago) it is fetched fresh once before saying no.
    // Null when no server is set or the bot can't list it.
    private async Task<IReadOnlyList<DiscordChannelInfo>?> ListGuildChannelsForCheckAsync(
        string? guildId, IEnumerable<string> channelIds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(guildId))
        {
            return null;
        }
        var ids = channelIds.ToList();
        var channels = await _discordBot.ListTextChannelsAsync(guildId, cancellationToken);
        if (channels is not null && ChannelRouteEditor.CheckChannelsInGuild(ids, channels) is not null)
        {
            channels = await _discordBot.ListTextChannelsAsync(guildId, cancellationToken, forceRefresh: true);
        }
        return channels;
    }

    // Whether the signed-in user is a member of this Discord server, asked through the bot
    // (the same check the eligible-guilds dropdown uses). False when their Discord account
    // isn't linked or Discord can't confirm it.
    private async Task<bool> CallerIsInGuildAsync(string appUserId, string guildId, CancellationToken cancellationToken)
    {
        var discordUserId = await _dbContext.DiscordActivityUsers
            .Where(link => link.IdentityUserId == appUserId)
            .Select(link => link.DiscordUserId)
            .FirstOrDefaultAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(discordUserId)
            && await _discordIdentityService.IsUserInGuildAsync(guildId, discordUserId, cancellationToken);
    }

    // set-guild's server: the dropdown pick wins, else the server the Activity is open in
    // (header). Null when neither was given. The caller still has to verify it.
    internal static string? PickGuildToSet(string? pickedGuildId, string? headerGuildId)
    {
        var picked = pickedGuildId?.Trim();
        if (!string.IsNullOrEmpty(picked))
        {
            return picked;
        }
        var header = headerGuildId?.Trim();
        return string.IsNullOrEmpty(header) ? null : header;
    }

    internal enum GuildIdUpdate { Unchanged, Clear, Set, Invalid }

    // What a DiscordGuildId on the main /update asks for. Null = leave it; blank = clear;
    // the id already stored = nothing to do (so an old client echoing it back needs no
    // Discord call); any other snowflake = a NEW server, which must be verified.
    internal static GuildIdUpdate ClassifyGuildIdUpdate(string? requestedGuildId, string? currentGuildId)
    {
        if (requestedGuildId is null)
        {
            return GuildIdUpdate.Unchanged;
        }
        var trimmed = requestedGuildId.Trim();
        if (trimmed.Length == 0)
        {
            return GuildIdUpdate.Clear;
        }
        if (!ChannelRouteEditor.IsSnowflake(trimmed))
        {
            return GuildIdUpdate.Invalid;
        }
        return string.Equals(trimmed, currentGuildId, StringComparison.Ordinal)
            ? GuildIdUpdate.Unchanged
            : GuildIdUpdate.Set;
    }

    private static IReadOnlyList<string> SplitEventTypeFilter(string? filter) =>
        string.IsNullOrWhiteSpace(filter)
            ? Array.Empty<string>()
            : filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [HttpPost("linkshells/{linkshellId:int}/delete")]
    public async Task<IActionResult> DeleteLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to delete the linkshell."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null ||
            !(IsLeader(membership) || await _adminOverride.IsActiveForAsync(appUser, cancellationToken)))
        {
            return Forbid();
        }

        return await DeleteLinkshellCoreAsync(linkshellId, cancellationToken);
    }

    private async Task<IActionResult> DeleteLinkshellCoreAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var linkshell = await _dbContext.Linkshells
            .Include(ls => ls.AppUserLinkshells)
            .Include(ls => ls.Events)
                .ThenInclude(evt => evt.AppUserEvents)
            .Include(ls => ls.Events)
                .ThenInclude(evt => evt.EventLootDetails)
            .Include(ls => ls.EventHistories)
                .ThenInclude(history => history.AppUserEventHistories)
            .FirstOrDefaultAsync(ls => ls.Id == linkshellId, cancellationToken);

        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        if (linkshell.AppUserLinkshells.Count > 1)
        {
            return BadRequest(new
            {
                error = "Remove the remaining members or transfer ownership before deleting this linkshell."
            });
        }

        if (linkshell.Events.Count > 0)
        {
            return BadRequest(new
            {
                error = "Cancel or end all active and queued events before deleting this linkshell."
            });
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var impactedUserIds = linkshell.AppUserLinkshells
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => link.AppUserId!)
            .Distinct()
            .ToList();

        if (impactedUserIds.Count > 0)
        {
            var impactedUsers = await _dbContext.Users
                .Where(user => impactedUserIds.Contains(user.Id))
                .ToListAsync(cancellationToken);

            foreach (var user in impactedUsers.Where(user => user.PrimaryLinkshellId == linkshellId))
            {
                var fallback = await _dbContext.AppUserLinkshells
                    .Include(link => link.Linkshell)
                    .Where(link => link.AppUserId == user.Id && link.LinkshellId != linkshellId)
                    .OrderBy(link => link.Linkshell!.LinkshellName)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(cancellationToken);

                user.PrimaryLinkshellId = fallback?.LinkshellId;
            }
        }

        var pendingInvites = await _dbContext.Invites
            .Where(invite => invite.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        if (pendingInvites.Count > 0)
        {
            _dbContext.Invites.RemoveRange(pendingInvites);
        }

        _dbContext.AppUserLinkshells.RemoveRange(linkshell.AppUserLinkshells);
        _dbContext.AppUserEvents.RemoveRange(linkshell.Events.SelectMany(evt => evt.AppUserEvents));
        _dbContext.EventLootDetails.RemoveRange(linkshell.Events.SelectMany(evt => evt.EventLootDetails));
        _dbContext.Events.RemoveRange(linkshell.Events);
        _dbContext.AppUserEventHistories.RemoveRange(linkshell.EventHistories.SelectMany(history => history.AppUserEventHistories));
        _dbContext.EventHistories.RemoveRange(linkshell.EventHistories);
        _dbContext.Linkshells.Remove(linkshell);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/leave")]
    public async Task<IActionResult> LeaveLinkshellAsync(
        int linkshellId,
        [FromServices] AddonApiAuthService addonAuth,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to leave the linkshell."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return NotFound(new { error = "The selected linkshell membership was not found." });
        }

        var memberCount = await _dbContext.AppUserLinkshells
            .CountAsync(link => link.LinkshellId == linkshellId, cancellationToken);

        if (IsLeader(membership) && memberCount > 1)
        {
            return BadRequest(new { error = "Leaders must transfer ownership or remove remaining members before leaving." });
        }

        if (IsLeader(membership) && memberCount == 1)
        {
            // Sole-leader leaves -> deletes the linkshell. Delegate to the shared core
            // (which runs inside its own transaction) before this method has touched
            // the change tracker, so the two flows do not interleave.
            return await DeleteLinkshellCoreAsync(linkshellId, cancellationToken);
        }

        _dbContext.AppUserLinkshells.Remove(membership);

        if (appUser.PrimaryLinkshellId == linkshellId)
        {
            var fallback = await _dbContext.AppUserLinkshells
                .Include(link => link.Linkshell)
                .Where(link => link.AppUserId == appUser.Id && link.LinkshellId != linkshellId)
                .OrderBy(link => link.Linkshell!.LinkshellName)
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);

            appUser.PrimaryLinkshellId = fallback?.LinkshellId;
        }

        var eventParticipations = await _dbContext.AppUserEvents
            .Include(participation => participation.Event)
            .Where(participation => participation.AppUserId == appUser.Id && participation.Event!.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        if (eventParticipations.Count > 0)
        {
            _dbContext.AppUserEvents.RemoveRange(eventParticipations);
        }

        var pendingInvites = await _dbContext.Invites
            .Where(invite => invite.LinkshellId == linkshellId && invite.AppUserId == appUser.Id)
            .ToListAsync(cancellationToken);

        if (pendingInvites.Count > 0)
        {
            _dbContext.Invites.RemoveRange(pendingInvites);
        }

        // Leaving ends the game addon's access to this linkshell too. Staged so the save below
        // writes it together with the membership removal.
        await addonAuth.RevokeAllForMemberAsync(appUser.Id, linkshellId, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }
}
