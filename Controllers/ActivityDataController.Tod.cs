using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    [HttpPost("tods")]
    public async Task<IActionResult> CreateTodAsync(
        [FromBody] ActivityCreateTodRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LinkshellId <= 0)
        {
            return BadRequest(new { error = "A linkshell selection is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to log a ToD entry."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var monsterName = request.MonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monsterName))
        {
            return BadRequest(new { error = "A monster name is required." });
        }

        if (!TryConvertUserTimeZoneToUtc(request.TimeLocal, appUser.TimeZone, out var todTimeUtc) || !todTimeUtc.HasValue)
        {
            return BadRequest(new { error = "Enter a valid Time of Death using your local time." });
        }

        var cooldown = string.IsNullOrWhiteSpace(request.Cooldown)
            ? GetDefaultTodCooldown(monsterName)
            : request.Cooldown.Trim();
        if (!IsAcceptableTodCooldown(cooldown))
        {
            return BadRequest(new { error = "Enter a valid cooldown (e.g. 22 Hour, 72 Hour, or a positive number of hours)." });
        }

        var interval = request.Interval?.Trim();
        if (string.IsNullOrWhiteSpace(interval))
        {
            interval = null;
        }
        else if (!SupportedTodIntervals.Contains(interval))
        {
            return BadRequest(new { error = "Select a valid interval." });
        }

        var linkshellEntity = await _dbContext.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(ls => ls.Id == request.LinkshellId, cancellationToken);
        var linkshellStructure = NormalizeLootStructure(linkshellEntity?.LootStructure ?? "Dkp");

        var normalizedLootDetails = request.Claim == true && !request.NoLoot && linkshellStructure != "LootCouncil"
            ? NormalizeTodLootDetails(request.LootDetails)
            : new List<TodLootDetail>();

        var validCharacterNames = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == request.LinkshellId)
            .Select(link => link.CharacterName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToListAsync(cancellationToken);

        foreach (var lootDetail in normalizedLootDetails)
        {
            if (string.IsNullOrWhiteSpace(lootDetail.ItemName))
            {
                return BadRequest(new { error = "Each ToD loot row needs an item name." });
            }

            if (string.IsNullOrWhiteSpace(lootDetail.ItemWinner))
            {
                return BadRequest(new { error = "Each ToD loot row needs an item winner." });
            }

            if (!validCharacterNames.Contains(lootDetail.ItemWinner.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest(new { error = "Choose a loot winner from the current linkshell roster." });
            }

            if (!lootDetail.WinningDkpSpent.HasValue || lootDetail.WinningDkpSpent <= 0)
            {
                return BadRequest(new
                {
                    error = linkshellStructure == "Hybrid"
                        ? "Each ToD loot row needs a deduction % (1-100)."
                        : "Each ToD loot row needs a positive DKP spent value."
                });
            }

            if (linkshellStructure == "Hybrid" && lootDetail.WinningDkpSpent > 100)
            {
                return BadRequest(new { error = "Deduction % cannot exceed 100." });
            }
        }

        // Three-tier permission check:
        //   CanManageTods            -> immediate create (today's behaviour)
        //   CanSubmitTodForApproval  -> queue as a pending submission
        //   neither                  -> 403
        var canManage = await CanAsync(membership, r => r.CanManageTods, cancellationToken);
        var canSubmit = await CanAsync(membership, r => r.CanSubmitTodForApproval, cancellationToken);
        if (!canManage && !canSubmit)
        {
            return Forbid();
        }

        var nowUtc = DateTime.UtcNow;

        if (!canManage)
        {
            // Member submit-for-approval path. Persist exactly what was sent.
            var approvalSvc = HttpContext.RequestServices.GetRequiredService<SubmissionApprovalService>();
            var input = new TodSubmissionInput(
                monsterName,
                request.DayNumber,
                request.Claim,
                todTimeUtc,
                cooldown,
                interval,
                todTimeUtc.Value.AddHours(ResolveTodCooldownHours(cooldown)),
                SanitizeUploadedImagePath(request.ImagePath),
                normalizedLootDetails
                    .Select(l => new TodSubmissionLootInput(l.ItemName, l.ItemWinner, l.WinningDkpSpent))
                    .ToList());
            var submissionId = await approvalSvc.QueueTodAsync(request.LinkshellId, appUser.Id, input, cancellationToken);
            return Ok(new { pending = true, submissionId });
        }

        var tod = new Tod
        {
            LinkshellId = request.LinkshellId,
            MonsterName = monsterName,
            DayNumber = request.DayNumber,
            Claim = request.Claim,
            Time = todTimeUtc,
            Cooldown = cooldown,
            RepopTime = todTimeUtc.Value.AddHours(ResolveTodCooldownHours(cooldown)),
            Interval = interval,
            TimeStamp = nowUtc,
            TotalTods = 1,
            TotalClaims = request.Claim == true ? 1 : 0,
            ImagePath = SanitizeUploadedImagePath(request.ImagePath)
        };

        _dbContext.Tods.Add(tod);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (normalizedLootDetails.Count > 0)
        {
            foreach (var lootDetail in normalizedLootDetails)
            {
                lootDetail.TodId = tod.Id;
            }

            await _dbContext.TodLootDetails.AddRangeAsync(normalizedLootDetails, cancellationToken);
            await AdjustTodLootDkpAsync(_dbContext, tod, normalizedLootDetails, nowUtc, isRefund: false, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            tod.TodLootDetails = normalizedLootDetails;
        }

        // A new ToD = a new pop window, so reset any party sign-ups assigned to
        // this monster (the old roster is for the pop that just happened).
        await PartySetupController.ClearSignupsForMonsterAsync(_dbContext, tod.LinkshellId, tod.MonsterName, cancellationToken);

        return Ok(MapTodDto(tod));
    }

    [HttpPost("tods/{todId:int}/delete")]
    public async Task<IActionResult> DeleteTodAsync(int todId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to delete a ToD entry."
            });
        }

        var tod = await _dbContext.Tods
            .Include(item => item.TodLootDetails)
            .FirstOrDefaultAsync(item => item.Id == todId, cancellationToken);

        if (tod is null)
        {
            return NotFound(new { error = "The selected ToD entry was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, tod.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        await AdjustTodLootDkpAsync(_dbContext, tod, tod.TodLootDetails.ToList(), DateTime.UtcNow, isRefund: true, cancellationToken);
        _dbContext.TodLootDetails.RemoveRange(tod.TodLootDetails);
        DeleteUploadedTodImage(tod.ImagePath);
        _dbContext.Tods.Remove(tod);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true });
    }

    [HttpPost("tods/upload-image")]
    [RequestSizeLimit(5_500_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 5_500_000)]
    public async Task<IActionResult> UploadTodImageAsync(
        [FromForm] IFormFile? file,
        [FromServices] TodImageUploadService uploads,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to upload ToD images." });
        }

        var result = await uploads.SaveAsync(file!, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }
        return Ok(new { imagePath = result.ImagePath });
    }

    // GET /api/activity/uploads/tods/{fileName}
    // Proxies uploaded ToD screenshots so the Discord Activity (which only
    // has /api/* mapped through Discord's proxy) can fetch them.  Files
    // live in wwwroot/uploads/tods/ -- the same place the web serves them
    // directly. Validates the filename so callers can't escape the
    // uploads directory.
    [HttpGet("uploads/tods/{fileName}")]
    [AllowAnonymous]
    public IActionResult GetTodImage(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return NotFound();
        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\')) return NotFound();

        var webRoot = _webHostEnvironment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot)) return NotFound();

        var absolutePath = Path.Combine(webRoot, "uploads", "tods", fileName);
        if (!System.IO.File.Exists(absolutePath)) return NotFound();

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var contentType = extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

        return PhysicalFile(absolutePath, contentType);
    }

    [HttpPost("tods/{todId:int}/update")]
    public async Task<IActionResult> UpdateTodAsync(
        int todId,
        [FromBody] ActivityUpdateTodRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to update a ToD entry." });
        }

        var tod = await _dbContext.Tods
            .Include(item => item.TodLootDetails)
            .Include(item => item.Linkshell)
            .FirstOrDefaultAsync(item => item.Id == todId, cancellationToken);

        if (tod is null)
        {
            return NotFound(new { error = "The selected ToD entry was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, tod.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var linkshellStructure = NormalizeLootStructure(tod.Linkshell?.LootStructure ?? "Dkp");

        var monsterName = request.MonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monsterName))
        {
            return BadRequest(new { error = "A monster name is required." });
        }

        if (!TryConvertUserTimeZoneToUtc(request.TimeLocal, appUser.TimeZone, out var todTimeUtc) || !todTimeUtc.HasValue)
        {
            return BadRequest(new { error = "Enter a valid Time of Death using your local time." });
        }

        var cooldown = string.IsNullOrWhiteSpace(request.Cooldown)
            ? GetDefaultTodCooldown(monsterName)
            : request.Cooldown.Trim();
        if (!IsAcceptableTodCooldown(cooldown))
        {
            return BadRequest(new { error = "Enter a valid cooldown." });
        }

        var interval = request.Interval?.Trim();
        if (string.IsNullOrWhiteSpace(interval))
        {
            interval = null;
        }
        else if (!SupportedTodIntervals.Contains(interval))
        {
            return BadRequest(new { error = "Select a valid interval." });
        }

        var normalizedLootDetails = request.Claim == true && !request.NoLoot && linkshellStructure != "LootCouncil"
            ? NormalizeTodLootDetails(request.LootDetails)
            : new List<TodLootDetail>();

        var validCharacterNames = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == tod.LinkshellId)
            .Select(link => link.CharacterName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToListAsync(cancellationToken);

        foreach (var lootDetail in normalizedLootDetails)
        {
            if (string.IsNullOrWhiteSpace(lootDetail.ItemName))
            {
                return BadRequest(new { error = "Each ToD loot row needs an item name." });
            }

            if (string.IsNullOrWhiteSpace(lootDetail.ItemWinner))
            {
                return BadRequest(new { error = "Each ToD loot row needs an item winner." });
            }

            if (!validCharacterNames.Contains(lootDetail.ItemWinner.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest(new { error = "Choose a loot winner from the current linkshell roster." });
            }

            if (!lootDetail.WinningDkpSpent.HasValue || lootDetail.WinningDkpSpent <= 0)
            {
                return BadRequest(new
                {
                    error = linkshellStructure == "Hybrid"
                        ? "Each ToD loot row needs a deduction % (1-100)."
                        : "Each ToD loot row needs a positive DKP spent value."
                });
            }

            if (linkshellStructure == "Hybrid" && lootDetail.WinningDkpSpent > 100)
            {
                return BadRequest(new { error = "Deduction % cannot exceed 100." });
            }
        }

        var nowUtc = DateTime.UtcNow;

        // Reverse DKP impact from existing loot, remove it, then apply the new set.
        if (tod.TodLootDetails.Count > 0)
        {
            await AdjustTodLootDkpAsync(_dbContext, tod, tod.TodLootDetails.ToList(), nowUtc, isRefund: true, cancellationToken);
            _dbContext.TodLootDetails.RemoveRange(tod.TodLootDetails);
        }

        tod.MonsterName = monsterName;
        tod.DayNumber = request.DayNumber;
        tod.Claim = request.Claim;
        tod.Time = todTimeUtc;
        tod.Cooldown = cooldown;
        tod.RepopTime = todTimeUtc.Value.AddHours(ResolveTodCooldownHours(cooldown));
        tod.Interval = interval;
        tod.TimeStamp = nowUtc;
        tod.TotalClaims = request.Claim == true ? 1 : 0;

        var previousImage = tod.ImagePath;
        var newImage = SanitizeUploadedImagePath(request.ImagePath);
        tod.ImagePath = newImage;

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (normalizedLootDetails.Count > 0)
        {
            foreach (var lootDetail in normalizedLootDetails)
            {
                lootDetail.TodId = tod.Id;
            }

            await _dbContext.TodLootDetails.AddRangeAsync(normalizedLootDetails, cancellationToken);
            await AdjustTodLootDkpAsync(_dbContext, tod, normalizedLootDetails, nowUtc, isRefund: false, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            tod.TodLootDetails = normalizedLootDetails;
        }
        else
        {
            tod.TodLootDetails = new List<TodLootDetail>();
        }

        if (!string.IsNullOrWhiteSpace(previousImage) && !string.Equals(previousImage, newImage, StringComparison.Ordinal))
        {
            DeleteUploadedTodImage(previousImage);
        }

        return Ok(MapTodDto(tod));
    }
}
