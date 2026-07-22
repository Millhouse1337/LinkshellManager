using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    // Serves a linkshell's dashboard banner image. Intentionally UNAUTHENTICATED:
    // it's rendered via an <img> tag (which can't send a bearer token) from both
    // the web app and the Discord Activity iframe, and a linkshell banner isn't
    // sensitive. Lives under /api/activity so the Discord proxy maps it.
    [HttpGet("linkshells/{linkshellId:int}/banner")]
    public async Task<IActionResult> GetLinkshellBanner(int linkshellId, CancellationToken cancellationToken)
    {
        var banner = await _dbContext.LinkshellBanners
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.LinkshellId == linkshellId, cancellationToken);

        if (banner is null || banner.ImageData.Length == 0)
        {
            return NotFound();
        }

        // The URL carries a ?v={ticks} cache-buster, so it's safe to cache hard.
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(banner.ImageData, banner.ContentType);
    }

    // Officer uploads a banner from the Activity (or web). The image arrives as a
    // base64 data URL in JSON — the Discord iframe can't do multipart uploads.
    [HttpPost("linkshells/{linkshellId:int}/banner")]
    public async Task<IActionResult> UploadLinkshellBanner(
        int linkshellId, [FromBody] ActivityBannerUploadRequest request, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage the banner." });
        }
        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.DataBase64))
        {
            return BadRequest(new { error = "No image provided." });
        }

        // Accept either a raw base64 string or a full "data:...;base64,xxxx" URL.
        var payload = request.DataBase64;
        var comma = payload.IndexOf(',');
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
        {
            payload = payload[(comma + 1)..];
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(payload); }
        catch (FormatException) { return BadRequest(new { error = "Invalid image data." }); }

        if (bytes.Length == 0 || bytes.Length > 5_000_000)
        {
            return BadRequest(new { error = "Image must be between 1 byte and 5 MB." });
        }
        var contentType = ResolveBannerContentType(bytes);
        if (contentType is null)
        {
            return BadRequest(new { error = "Unsupported image type. Use PNG, JPG, WEBP, or GIF." });
        }

        var banner = await _dbContext.LinkshellBanners
            .FirstOrDefaultAsync(b => b.LinkshellId == linkshellId, cancellationToken);
        if (banner is null)
        {
            banner = new LinkshellBanner { LinkshellId = linkshellId };
            _dbContext.LinkshellBanners.Add(banner);
        }
        banner.ImageData = bytes;
        banner.ContentType = contentType;
        banner.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            bannerUrl = $"/api/activity/linkshells/{linkshellId}/banner?v={banner.UpdatedAt.Ticks}"
        });
    }

    [HttpPost("linkshells/{linkshellId:int}/banner/remove")]
    public async Task<IActionResult> RemoveLinkshellBanner(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage the banner." });
        }
        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var banner = await _dbContext.LinkshellBanners
            .FirstOrDefaultAsync(b => b.LinkshellId == linkshellId, cancellationToken);
        if (banner is not null)
        {
            _dbContext.LinkshellBanners.Remove(banner);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        return Ok(new { success = true });
    }

    // Sniff the format from magic bytes (don't trust a client-declared type).
    private static string? ResolveBannerContentType(byte[] b)
    {
        if (b.Length < 12) { return null; }
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) { return "image/png"; }
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) { return "image/jpeg"; }
        if (b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F') { return "image/gif"; }
        if (b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') { return "image/webp"; }
        return null;
    }
}

public sealed record ActivityBannerUploadRequest(string? DataBase64);
