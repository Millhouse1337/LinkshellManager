using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
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

        // Stored as the combined pair name, with Hq carrying which half. See
        // NormalizeMergePairTod -- this is what makes an Activity-logged ToD match one logged
        // from the addon or the camp board.
        var (normalizedMonsterName, normalizedHq) = NormalizeMergePairTod(monsterName, request.Hq);
        monsterName = normalizedMonsterName;

        if (!TryConvertUserTimeZoneToUtc(request.TimeLocal, appUser.TimeZone, out var todTimeUtc) || !todTimeUtc.HasValue)
        {
            return BadRequest(new { error = "Enter a valid Time of Death using your local time." });
        }

        // The LINKSHELL'S Monster setups own the cooldown, the interval and the seconds offset now:
        // the Log ToD form no longer asks for them. A value that still arrives (an older client)
        // is honoured; anything blank comes from the setup for this monster.
        var timing = await _monsterTimings.ResolveAsync(request.LinkshellId, monsterName, cancellationToken);
        var cooldown = string.IsNullOrWhiteSpace(request.Cooldown)
            ? TodDurationFormat.Format(timing.CooldownMinutes)
            : request.Cooldown.Trim();
        if (!IsAcceptableTodCooldown(cooldown))
        {
            return BadRequest(new { error = "Enter a valid cooldown (e.g. 22 Hour, 72 Hour, or a positive number of hours)." });
        }

        var interval = request.Interval?.Trim();
        if (string.IsNullOrWhiteSpace(interval))
        {
            interval = timing.HasSpawnGrid ? TodDurationFormat.Format(timing.TodIntervalMinutes) : null;
        }
        else if (!IsAcceptableTodInterval(interval))
        {
            return BadRequest(new { error = "Select a valid interval." });
        }
        var requestedAdditionalSeconds = request.AdditionalSeconds > 0
            ? request.AdditionalSeconds
            : timing.AdditionalSeconds;

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
                todTimeUtc.Value.AddHours(ResolveTodCooldownHours(cooldown)).AddSeconds(Math.Max(0, requestedAdditionalSeconds)),
                SanitizeUploadedImagePath(request.ImagePath));
            var submissionId = await approvalSvc.QueueTodAsync(request.LinkshellId, appUser.Id, input, cancellationToken);
            return Ok(new { pending = true, submissionId });
        }

        var additionalSeconds = Math.Max(0, requestedAdditionalSeconds);
        var tod = new Tod
        {
            LinkshellId = request.LinkshellId,
            MonsterName = monsterName,
            DayNumber = request.DayNumber,
            PopWindow = NormalizePopWindow(request.PopWindow),
            Claim = request.Claim,
            Hq = normalizedHq,
            AdditionalSeconds = additionalSeconds,
            Time = todTimeUtc,
            Cooldown = cooldown,
            RepopTime = todTimeUtc.Value.AddHours(ResolveTodCooldownHours(cooldown)).AddSeconds(additionalSeconds),
            Interval = interval,
            TimeStamp = nowUtc,
            TotalTods = 1,
            TotalClaims = request.Claim == true ? 1 : 0,
            ImagePath = SanitizeUploadedImagePath(request.ImagePath)
        };

        _dbContext.Tods.Add(tod);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // A new ToD = a new pop window, so reset any party sign-ups assigned to
        // this monster (the old roster is for the pop that just happened).
        await PartySetupSignupReset.ClearSignupsForMonsterAsync(_dbContext, tod.LinkshellId, tod.MonsterName, cancellationToken);

        // The tracker writes only the Tod row, so any board parked waiting to re-post would keep
        // showing the old pop time until it actually re-posted. Re-point it at this ToD's repop.
        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(
            _dbContext, tod.LinkshellId, tod.MonsterName, cancellationToken);

        return Ok(MapTodDto(tod));
    }

    // A merge pair is stored under its COMBINED name, with Hq carrying which half it was.
    //
    // The addon and the HNM board both write the board's AssignedMonsterName, which on a merge
    // pair is "Fafnir/Nidhogg". The Activity wrote whatever the picker held -- "Fafnir" -- so the
    // same kill was recorded under two different names depending on where it was logged, and
    // every lookup keyed on the name (the tracker, recurring boards, claim stats) had to guess.
    //
    // ResolveClaimHalf is what makes this lossless. Picking "Nidhogg" explicitly IS an HQ answer
    // even if the toggle was never touched, so it returns isHq true and that is written to Hq
    // before the name collapses -- the half survives in the field built to hold it, which is
    // exactly what the HQ question is for. Non-pair monsters come back untouched.
    private static (string Name, bool Hq) NormalizeMergePairTod(string? monsterName, bool hq)
    {
        var (_, isHq, hasHqVariant) = HnmConfig.ResolveClaimHalf(monsterName, hq);
        return hasHqVariant
            ? (HnmConfig.ClaimGroupName(monsterName), isHq)
            : (monsterName?.Trim() ?? string.Empty, hq);
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

        // Deleting a ToD never changes anyone's DKP. Its loot rows go with it, but the DKP charged
        // for that loot stays charged: the ledger rows keep only the loot's id (not a foreign key),
        // so they survive the delete. Refunding loot is the Loot tab's job, one item at a time.
        _dbContext.TodLootDetails.RemoveRange(tod.TodLootDetails);
        DeleteUploadedTodImage(tod.ImagePath);
        _dbContext.Tods.Remove(tod);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Deleting the ToD a parked board was counting on leaves it advertising a pop that no
        // longer exists — fall back to the monster's next-newest ToD, or to no time at all.
        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(
            _dbContext, tod.LinkshellId, tod.MonsterName, cancellationToken);

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

        var monsterName = request.MonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monsterName))
        {
            return BadRequest(new { error = "A monster name is required." });
        }

        // Stored as the combined pair name, with Hq carrying which half. See
        // NormalizeMergePairTod -- this is what makes an Activity-logged ToD match one logged
        // from the addon or the camp board.
        var (normalizedMonsterName, normalizedHq) = NormalizeMergePairTod(monsterName, request.Hq);
        monsterName = normalizedMonsterName;

        if (!TryConvertUserTimeZoneToUtc(request.TimeLocal, appUser.TimeZone, out var todTimeUtc) || !todTimeUtc.HasValue)
        {
            return BadRequest(new { error = "Enter a valid Time of Death using your local time." });
        }

        var cooldown = string.IsNullOrWhiteSpace(request.Cooldown)
            ? await GetDefaultTodCooldownAsync(_monsterTimings, tod.LinkshellId, monsterName, cancellationToken)
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
        else if (!IsAcceptableTodInterval(interval))
        {
            return BadRequest(new { error = "Select a valid interval." });
        }

        // Loot is never edited here. A ToD carries no loot any more, and an older ToD that still has
        // some keeps it untouched -- along with the DKP charged for it -- through any edit.
        var nowUtc = DateTime.UtcNow;

        tod.MonsterName = monsterName;
        tod.DayNumber = request.DayNumber;
        tod.PopWindow = NormalizePopWindow(request.PopWindow);
        tod.Claim = request.Claim;
        tod.Hq = normalizedHq;
        tod.AdditionalSeconds = Math.Max(0, request.AdditionalSeconds);
        tod.Time = todTimeUtc;
        tod.Cooldown = cooldown;
        tod.RepopTime = todTimeUtc.Value.AddHours(ResolveTodCooldownHours(cooldown)).AddSeconds(tod.AdditionalSeconds);
        tod.Interval = interval;
        tod.TimeStamp = nowUtc;
        tod.TotalClaims = request.Claim == true ? 1 : 0;

        var previousImage = tod.ImagePath;
        var newImage = SanitizeUploadedImagePath(request.ImagePath);
        tod.ImagePath = newImage;

        await _dbContext.SaveChangesAsync(cancellationToken);

        // A corrected repop has to reach any board parked waiting on it: re-point its displayed
        // pop / re-post time, and re-open the cycle if the poller had already given up on it.
        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(
            _dbContext, tod.LinkshellId, tod.MonsterName, cancellationToken);

        if (!string.IsNullOrWhiteSpace(previousImage) && !string.Equals(previousImage, newImage, StringComparison.Ordinal))
        {
            DeleteUploadedTodImage(previousImage);
        }

        return Ok(MapTodDto(tod));
    }
}
