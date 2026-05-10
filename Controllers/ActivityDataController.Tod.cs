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

        var nowUtc = DateTime.UtcNow;
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
    [RequestSizeLimit(2_200_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2_200_000)]
    public async Task<IActionResult> UploadTodImageAsync(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to upload ToD images." });
        }

        if (file is null || file.Length <= 0)
        {
            return BadRequest(new { error = "Choose an image to upload." });
        }

        if (file.Length > 2_000_000)
        {
            return BadRequest(new { error = "Images must be 2 MB or smaller." });
        }

        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowedExtensions.Contains(extension))
        {
            return BadRequest(new { error = "Only PNG, JPG, or WEBP images are supported." });
        }

        // Verify the file's magic bytes match its declared extension. Without
        // this an attacker could upload an HTML/SVG/JS file renamed to .png
        // and have it served from the same origin as the app.
        byte[] fileBytes;
        await using (var memory = new MemoryStream())
        {
            await file.CopyToAsync(memory, cancellationToken);
            fileBytes = memory.ToArray();
        }

        var detectedExtension = DetectImageExtensionFromMagicBytes(fileBytes);
        if (detectedExtension is null)
        {
            return BadRequest(new { error = "Uploaded file is not a recognized PNG, JPG, or WEBP image." });
        }

        // Use the detected extension (overrides whatever the client sent).
        if (!allowedExtensions.Contains(detectedExtension))
        {
            return BadRequest(new { error = "Uploaded file is not a recognized PNG, JPG, or WEBP image." });
        }

        var webRoot = _webHostEnvironment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        }

        var uploadsDir = Path.Combine(webRoot, "uploads", "tods");
        Directory.CreateDirectory(uploadsDir);

        var fileName = $"{Guid.NewGuid():N}{detectedExtension}";
        var absolutePath = Path.Combine(uploadsDir, fileName);
        await System.IO.File.WriteAllBytesAsync(absolutePath, fileBytes, cancellationToken);

        var relativePath = $"/uploads/tods/{fileName}";
        return Ok(new { imagePath = relativePath });
    }

    private static string? DetectImageExtensionFromMagicBytes(ReadOnlySpan<byte> bytes)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return ".png";
        }

        // JPEG: FF D8 FF
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ".jpg";
        }

        // WEBP: 'RIFF' .... 'WEBP'
        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return ".webp";
        }

        return null;
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
