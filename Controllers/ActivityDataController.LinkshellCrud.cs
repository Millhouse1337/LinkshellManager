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
        appUser.PrimaryLinkshellName ??= linkshell.LinkshellName;

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
                    link.DateJoined))
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
        appUser.PrimaryLinkshellName = membership.Linkshell.LinkshellName;

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

        if (!string.IsNullOrWhiteSpace(request.LinkshellType))
        {
            if (!LinkshellTypes.IsValid(request.LinkshellType.Trim()))
            {
                return BadRequest(new { error = "Linkshell Type must be SkySeaDynamis, HnmOnly, or Both." });
            }
            linkshell.LinkshellType = LinkshellTypes.Normalize(request.LinkshellType);
        }

        if (request.EnableHnmSection.HasValue) linkshell.EnableHnmSection = request.EnableHnmSection.Value;
        if (request.EnableMissions.HasValue) linkshell.EnableMissions = request.EnableMissions.Value;
        if (request.EnableAuctions.HasValue) linkshell.EnableAuctions = request.EnableAuctions.Value;
        if (request.EnableToDs.HasValue) linkshell.EnableToDs = request.EnableToDs.Value;
        if (request.EnableEndgame.HasValue) linkshell.EnableEndgame = request.EnableEndgame.Value;
        if (request.EnableEvents.HasValue) linkshell.EnableEvents = request.EnableEvents.Value;
        if (request.EnableDkp.HasValue) linkshell.EnableDkp = request.EnableDkp.Value;
        if (request.EnableItems.HasValue) linkshell.EnableItems = request.EnableItems.Value;
        if (request.EnableRevenue.HasValue) linkshell.EnableRevenue = request.EnableRevenue.Value;
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
        // Member activity tracking (null = leave unchanged). Thresholds clamp to >= 1
        // to match the web Customize page and MemberActivityService's own guard.
        if (request.EnableActivityTracking.HasValue)
        {
            linkshell.EnableActivityTracking = request.EnableActivityTracking.Value;
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
        if (request.DiscordGuildId is not null)
        {
            var trimmedGuildId = request.DiscordGuildId.Trim();
            if (trimmedGuildId.Length == 0)
            {
                linkshell.DiscordGuildId = null;
                linkshell.DiscordGuildName = null;
                linkshell.LockToDiscordGuild = false;
            }
            else if (trimmedGuildId.Length <= 20 && trimmedGuildId.All(char.IsDigit))
            {
                linkshell.DiscordGuildId = trimmedGuildId;
            }
            else
            {
                return BadRequest(new { error = "Discord Server ID must be the numeric server ID (digits only). Leave blank to clear it." });
            }
        }

        var memberships = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == linkshellId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var memberIds = memberships
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => link.AppUserId!)
            .Distinct()
            .ToList();

        if (memberIds.Count > 0)
        {
            var users = await _dbContext.Users.Where(user => memberIds.Contains(user.Id)).ToListAsync(cancellationToken);
            foreach (var user in users.Where(user => user.PrimaryLinkshellId == linkshellId))
            {
                user.PrimaryLinkshellName = trimmedName;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Enabling tracking or changing thresholds can change who's Inactive →
        // recompute member statuses now (no-op when tracking is off).
        await _memberActivity.ApplyComputedStatusAsync(linkshellId, cancellationToken);

        return Ok(new { success = true });
    }

    // The Discord servers the caller can lock a linkshell to: the bot's servers
    // that the caller is also a member of (mirrors the web Customize dropdown).
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
        // into dozens of Discord calls (mirrors the web's BuildEligibleGuildsAsync).
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

        string guildId;
        var resolvedName = request.GuildName?.Trim();
        var requestedGuildId = request.GuildId?.Trim();

        if (!string.IsNullOrWhiteSpace(requestedGuildId))
        {
            // A server was picked from the dropdown — verify it's a real snowflake
            // and that the caller is actually a member of it before setting it.
            if (requestedGuildId.Length > 20 || !requestedGuildId.All(char.IsDigit))
            {
                return BadRequest(new { error = "Invalid Discord server selection." });
            }

            var discordUserId = await _dbContext.DiscordActivityUsers
                .Where(link => link.IdentityUserId == appUser.Id)
                .Select(link => link.DiscordUserId)
                .FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(discordUserId) ||
                !await _discordIdentityService.IsUserInGuildAsync(requestedGuildId, discordUserId, cancellationToken))
            {
                return BadRequest(new { error = "You must be a member of the server you set." });
            }

            guildId = requestedGuildId;
            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                var botGuilds = await _discordIdentityService.ListBotGuildsAsync(cancellationToken);
                resolvedName = botGuilds?.FirstOrDefault(guild => guild.Id == requestedGuildId)?.Name;
            }
        }
        else
        {
            // No explicit pick — use the server the Activity is launched in.
            guildId = GetRequestGuildId() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(guildId))
            {
                return BadRequest(new { error = "Pick a server from the list, or open the Activity inside the server you want to set." });
            }
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

    // --- Discord channel posting config (Phase 2), mirrored from the web
    // Customize page so officers can set it from the Activity too. The bot posts
    // event/auction/loot announcements directly to these channels. ---

    [HttpGet("linkshells/{linkshellId:int}/discord-channels")]
    public async Task<IActionResult> GetDiscordChannelsAsync(int linkshellId, CancellationToken cancellationToken)
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
            : await _discordBot.ListTextChannelsAsync(guild, cancellationToken);

        var existing = await _dbContext.LinkshellDiscordChannels
            .AsNoTracking()
            .Where(channel => channel.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        var channels = DiscordChannelPurposes.All.Select(purpose =>
        {
            var row = existing.FirstOrDefault(channel => channel.Purpose == purpose);
            return new
            {
                purpose,
                label = DiscordChannelPurposes.Label(purpose),
                channelId = row?.ChannelId,
                channelName = row?.ChannelName
            };
        }).ToList();

        return Ok(new
        {
            guildConfigured = !string.IsNullOrWhiteSpace(guild),
            availableChannels = (available ?? Array.Empty<DiscordChannelInfo>())
                .Select(channel => new { id = channel.Id, name = channel.Name })
                .ToList(),
            channels
        });
    }

    [HttpPost("linkshells/{linkshellId:int}/discord-channels")]
    public async Task<IActionResult> SaveDiscordChannelsAsync(
        int linkshellId,
        [FromBody] ActivitySaveDiscordChannelsRequest request,
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

        var bindings = request.Channels ?? Array.Empty<ActivityDiscordChannelBinding>();
        var guild = linkshell.DiscordGuildId;
        var available = string.IsNullOrWhiteSpace(guild)
            ? null
            : await _discordBot.ListTextChannelsAsync(guild, cancellationToken);

        var existing = await _dbContext.LinkshellDiscordChannels
            .Where(channel => channel.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        foreach (var purpose in DiscordChannelPurposes.All)
        {
            var chosen = bindings
                .FirstOrDefault(binding => string.Equals(binding.Purpose, purpose, StringComparison.OrdinalIgnoreCase))?
                .ChannelId?.Trim();
            var row = existing.FirstOrDefault(channel => channel.Purpose == purpose);

            // Empty / non-snowflake clears the binding for that purpose.
            var valid = !string.IsNullOrEmpty(chosen) && chosen!.Length <= 20 && chosen.All(char.IsDigit);
            if (!valid)
            {
                if (row is not null)
                {
                    _dbContext.LinkshellDiscordChannels.Remove(row);
                }
                continue;
            }

            var name = available?.FirstOrDefault(channel => channel.Id == chosen)?.Name;
            if (row is not null)
            {
                row.ChannelId = chosen!;
                row.ChannelName = name;
            }
            else
            {
                _dbContext.LinkshellDiscordChannels.Add(new LinkshellDiscordChannel
                {
                    LinkshellId = linkshellId,
                    Purpose = purpose,
                    ChannelId = chosen!,
                    ChannelName = name,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

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
        if (!IsLeader(membership))
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
                user.PrimaryLinkshellName = fallback?.Linkshell?.LinkshellName;
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
    public async Task<IActionResult> LeaveLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
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
            appUser.PrimaryLinkshellName = fallback?.Linkshell?.LinkshellName;
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

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }
}
