using Microsoft.AspNetCore.Hosting;

namespace LinkshellManagerDiscordApp.Services;

// Saves, finds and deletes ToD screenshots.
//
// The files live under the "Uploads:Root" setting, NOT inside the published app. The deploy
// replaces /var/www/lsmanager on every release, so screenshots kept in wwwroot/uploads vanished
// with each deploy while the ToD rows still pointed at them. Production sets
// Uploads__Root=/var/lib/lsmanager/uploads (already writable in lsmanager.service). Left blank,
// it falls back to wwwroot/uploads so a dev machine keeps working with no setup.
public sealed class TodImageUploadService
{
    public const int MaxBytes = 5_000_000;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp",
    };

    private readonly string _todsDirectory;

    public TodImageUploadService(IWebHostEnvironment webHostEnvironment, IConfiguration configuration)
        : this(ResolveUploadsRoot(
            configuration["Uploads:Root"], webHostEnvironment.WebRootPath, webHostEnvironment.ContentRootPath))
    {
    }

    internal TodImageUploadService(string uploadsRoot)
    {
        _todsDirectory = Path.Combine(uploadsRoot, "tods");
    }

    // The folder that holds every upload. A configured path wins; otherwise the old
    // wwwroot/uploads spot (WebRootPath is blank when the app has no wwwroot folder).
    internal static string ResolveUploadsRoot(string? configuredRoot, string? webRootPath, string contentRootPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return configuredRoot.Trim();
        }

        var webRoot = string.IsNullOrWhiteSpace(webRootPath)
            ? Path.Combine(contentRootPath, "wwwroot")
            : webRootPath;
        return Path.Combine(webRoot, "uploads");
    }

    // Full path of a stored screenshot, or null when the name is unsafe or the file is gone.
    // Only a bare file name is accepted, so a caller can never reach outside the tods folder.
    public string? FindExistingFile(string? fileName)
    {
        if (!IsSafeFileName(fileName))
        {
            return null;
        }

        var absolutePath = Path.Combine(_todsDirectory, fileName!);
        return File.Exists(absolutePath) ? absolutePath : null;
    }

    // Best-effort removal: a missing or locked file is not worth failing the request over.
    public void DeleteIfExists(string? fileName)
    {
        var absolutePath = FindExistingFile(fileName);
        if (absolutePath is null)
        {
            return;
        }

        try
        {
            File.Delete(absolutePath);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    internal static bool IsSafeFileName(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
            && !fileName.Contains("..", StringComparison.Ordinal)
            && !fileName.Contains('/')
            && !fileName.Contains('\\')
            && !fileName.Contains(':');
    }

    public async Task<TodImageUploadResult> SaveAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length <= 0)
        {
            return TodImageUploadResult.Fail("Choose an image to upload.");
        }
        if (file.Length > MaxBytes)
        {
            return TodImageUploadResult.Fail("Images must be 5 MB or smaller.");
        }

        var declaredExtension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(declaredExtension))
        {
            return TodImageUploadResult.Fail("Only PNG, JPG, or WEBP images are supported.");
        }

        byte[] bytes;
        await using (var memory = new MemoryStream())
        {
            await file.CopyToAsync(memory, cancellationToken);
            bytes = memory.ToArray();
        }

        // Verify the file's magic bytes match its declared extension. Without
        // this an attacker could upload an HTML/SVG/JS file renamed to .png
        // and have it served from the same origin as the app.
        var detectedExtension = DetectExtensionFromMagicBytes(bytes);
        if (detectedExtension is null || !AllowedExtensions.Contains(detectedExtension))
        {
            return TodImageUploadResult.Fail("Uploaded file is not a recognized PNG, JPG, or WEBP image.");
        }

        Directory.CreateDirectory(_todsDirectory);

        var fileName = $"{Guid.NewGuid():N}{detectedExtension}";
        var absolutePath = Path.Combine(_todsDirectory, fileName);
        await File.WriteAllBytesAsync(absolutePath, bytes, cancellationToken);

        return TodImageUploadResult.Ok($"/uploads/tods/{fileName}");
    }

    private static string? DetectExtensionFromMagicBytes(ReadOnlySpan<byte> bytes)
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
}

public sealed record TodImageUploadResult(bool Success, string? ImagePath, string? Error)
{
    public static TodImageUploadResult Ok(string imagePath) => new(true, imagePath, null);
    public static TodImageUploadResult Fail(string error) => new(false, null, error);
}
