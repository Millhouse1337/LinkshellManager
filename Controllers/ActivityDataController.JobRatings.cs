using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// Peer job-rating: members rate each OTHER member's jobs (Gear 1-5, Skill 1-5)
// within a linkshell, plus an optional free-text comment per teammate. A rating
// where rater == target is that member's own self-assessment ("what you think");
// other raters' rows aggregate into "what others think" (averages + an AI summary
// of their comments). Any member of the linkshell may view + rate.
public sealed partial class ActivityDataController
{
    // Clamp a requested character slot to the valid 0 (main) … 2 (alt 2) range.
    private static int ClampSlot(int slot) => slot < 0 ? 0 : slot > 2 ? 2 : slot;

    [HttpGet("job-ratings/{targetAppUserId}")]
    public async Task<IActionResult> GetJobRatingsAsync(
        string targetAppUserId, [FromQuery] int linkshellId, [FromQuery] int slot, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to view job ratings." });

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null) return Forbid();

        var characterSlot = ClampSlot(slot);

        // Load the target's membership (with account, for alt levels) so we can
        // surface the picked character's per-job levels alongside the ratings —
        // the rater needs to see what level a job is before scoring it.
        var targetMembership = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Include(m => m.AppUser)
            .FirstOrDefaultAsync(m => m.LinkshellId == linkshellId && m.AppUserId == targetAppUserId, cancellationToken);
        if (targetMembership is null) return NotFound(new { error = "That member was not found in this linkshell." });

        // Main character levels live on the membership; alts live on the account.
        var storedLevels = characterSlot switch
        {
            1 => targetMembership.AppUser?.Alt1JobLevels,
            2 => targetMembership.AppUser?.Alt2JobLevels,
            _ => targetMembership.JobLevels,
        };
        var jobLevels = ProfileJobLevels.ToCatalogLevels(storedLevels);

        var rows = await _dbContext.JobRatings
            .AsNoTracking()
            .Where(r => r.LinkshellId == linkshellId && r.TargetAppUserId == targetAppUserId && r.CharacterSlot == characterSlot)
            .ToListAsync(cancellationToken);

        var jobRows = rows.Where(r => r.JobIndex >= 0).ToList();

        var jobs = Enumerable.Range(0, EventJobCatalog.MainJobOptions.Length).Select(idx =>
        {
            var forJob = jobRows.Where(r => r.JobIndex == idx).ToList();
            var self = forJob.FirstOrDefault(r => r.RaterAppUserId == targetAppUserId);
            var mine = forJob.FirstOrDefault(r => r.RaterAppUserId == appUser.Id);
            var peers = forJob.Where(r => r.RaterAppUserId != targetAppUserId).ToList();
            var peerGear = peers.Where(p => p.Gear > 0).Select(p => p.Gear).ToList();
            var peerSkill = peers.Where(p => p.Skill > 0).Select(p => p.Skill).ToList();

            return new
            {
                jobIndex = idx,
                level = idx < jobLevels.Count ? jobLevels[idx] : 0,
                hasSelf = self is not null,
                selfGear = self?.Gear ?? 0,
                selfSkill = self?.Skill ?? 0,
                selfRelic = self?.HasRelic ?? false,
                selfRelicNames = self?.RelicNames ?? Array.Empty<string>(),
                myGear = mine?.Gear ?? 0,
                mySkill = mine?.Skill ?? 0,
                myRelic = mine?.HasRelic ?? false,
                myRelicNames = mine?.RelicNames ?? Array.Empty<string>(),
                peerCount = peers.Count,
                peerAvgGear = peerGear.Count > 0 ? Math.Round(peerGear.Average(), 1) : 0.0,
                peerAvgSkill = peerSkill.Count > 0 ? Math.Round(peerSkill.Average(), 1) : 0.0,
                peerRelicYes = peers.Count(p => p.HasRelic),
            };
        }).ToList();

        // Distinct teammates who rated ANY of this character's jobs — one teammate
        // counts once no matter how many jobs they rated.
        var peerRaterCount = jobRows
            .Where(r => r.RaterAppUserId != targetAppUserId)
            .Select(r => r.RaterAppUserId)
            .Distinct()
            .Count();

        // Player-level comment rows. Peer comments (rater != target) are shown to
        // the target anonymously; the caller's own comment is returned for editing.
        var commentRows = rows
            .Where(r => r.JobIndex == JobRating.PlayerCommentJobIndex && !string.IsNullOrWhiteSpace(r.Comment))
            .ToList();
        var peerComments = commentRows
            .Where(r => r.RaterAppUserId != targetAppUserId)
            .OrderByDescending(r => r.UpdatedAt)
            .Select(r => r.Comment!)
            .ToList();
        var myComment = commentRows.FirstOrDefault(r => r.RaterAppUserId == appUser.Id)?.Comment ?? string.Empty;

        return Ok(new
        {
            isSelf = string.Equals(appUser.Id, targetAppUserId, StringComparison.Ordinal),
            jobs,
            // Distinct teammates who rated (one teammate = one count across all jobs).
            peerRaterCount,
            peerComments,
            peerCommentCount = peerComments.Count,
            myComment,
        });
    }

    [HttpPost("job-ratings")]
    public async Task<IActionResult> UpsertJobRatingAsync(
        [FromBody] ActivityJobRatingRequest request, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to rate jobs." });

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (membership is null) return Forbid();

        if (string.IsNullOrWhiteSpace(request.TargetAppUserId))
        {
            return BadRequest(new { error = "A target member is required." });
        }
        if (request.JobIndex < 0 || request.JobIndex >= EventJobCatalog.MainJobOptions.Length)
        {
            return BadRequest(new { error = "Unknown job." });
        }

        var targetIsMember = await _dbContext.AppUserLinkshells
            .AnyAsync(m => m.LinkshellId == request.LinkshellId && m.AppUserId == request.TargetAppUserId, cancellationToken);
        if (!targetIsMember) return NotFound(new { error = "That member was not found in this linkshell." });

        static int Clamp(int value) => value < 0 ? 0 : value > 5 ? 5 : value;
        var gear = Clamp(request.Gear);
        var skill = Clamp(request.Skill);
        var characterSlot = ClampSlot(request.CharacterSlot);
        // Relic weapons: trim/cap each, drop blanks + dupes. HasRelic = any present.
        var relicNames = (request.RelicNames ?? Array.Empty<string>())
            .Select(n => (n ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .Select(n => n.Length > 32 ? n[..32] : n)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hasRelic = request.HasRelic || relicNames.Length > 0;

        var existing = await _dbContext.JobRatings.FirstOrDefaultAsync(r =>
            r.LinkshellId == request.LinkshellId
            && r.TargetAppUserId == request.TargetAppUserId
            && r.RaterAppUserId == appUser.Id
            && r.CharacterSlot == characterSlot
            && r.JobIndex == request.JobIndex, cancellationToken);

        // An all-empty rating clears it (retract).
        if (gear == 0 && skill == 0 && !hasRelic)
        {
            if (existing is not null) { _dbContext.JobRatings.Remove(existing); }
        }
        else if (existing is null)
        {
            _dbContext.JobRatings.Add(new JobRating
            {
                LinkshellId = request.LinkshellId,
                TargetAppUserId = request.TargetAppUserId,
                RaterAppUserId = appUser.Id,
                CharacterSlot = characterSlot,
                JobIndex = request.JobIndex,
                Gear = gear,
                Skill = skill,
                HasRelic = hasRelic,
                RelicNames = relicNames.Length > 0 ? relicNames : null,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Gear = gear;
            existing.Skill = skill;
            existing.HasRelic = hasRelic;
            existing.RelicNames = relicNames.Length > 0 ? relicNames : null;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Upsert the caller's player-level comment about a teammate (a single row per
    // rater/target on the PlayerCommentJobIndex sentinel). Blank clears it.
    [HttpPost("job-ratings/comment")]
    public async Task<IActionResult> UpsertJobRatingCommentAsync(
        [FromBody] ActivityJobRatingCommentRequest request, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to comment." });

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (membership is null) return Forbid();

        if (string.IsNullOrWhiteSpace(request.TargetAppUserId))
        {
            return BadRequest(new { error = "A target member is required." });
        }
        // No self-comments — the comment box only appears when rating a teammate.
        if (string.Equals(appUser.Id, request.TargetAppUserId, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "You cannot comment on yourself." });
        }

        var targetIsMember = await _dbContext.AppUserLinkshells
            .AnyAsync(m => m.LinkshellId == request.LinkshellId && m.AppUserId == request.TargetAppUserId, cancellationToken);
        if (!targetIsMember) return NotFound(new { error = "That member was not found in this linkshell." });

        var trimmed = request.Comment?.Trim() ?? string.Empty;
        if (trimmed.Length > 1000) { trimmed = trimmed[..1000]; }
        var characterSlot = ClampSlot(request.CharacterSlot);

        var existing = await _dbContext.JobRatings.FirstOrDefaultAsync(r =>
            r.LinkshellId == request.LinkshellId
            && r.TargetAppUserId == request.TargetAppUserId
            && r.RaterAppUserId == appUser.Id
            && r.CharacterSlot == characterSlot
            && r.JobIndex == JobRating.PlayerCommentJobIndex, cancellationToken);

        if (trimmed.Length == 0)
        {
            if (existing is not null) { _dbContext.JobRatings.Remove(existing); }
        }
        else if (existing is null)
        {
            _dbContext.JobRatings.Add(new JobRating
            {
                LinkshellId = request.LinkshellId,
                TargetAppUserId = request.TargetAppUserId,
                RaterAppUserId = appUser.Id,
                CharacterSlot = characterSlot,
                JobIndex = JobRating.PlayerCommentJobIndex,
                Comment = trimmed,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Comment = trimmed;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // AI summary of the peer comments a member has received. Anyone in the
    // linkshell can read it; comments stay anonymous. Returns a null summary when
    // the AI service is unconfigured (caller shows the raw comment list instead).
    [HttpGet("job-ratings/{targetAppUserId}/comment-summary")]
    public async Task<IActionResult> GetJobRatingCommentSummaryAsync(
        string targetAppUserId, [FromQuery] int linkshellId, [FromQuery] int slot, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to view feedback." });

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null) return Forbid();

        var characterSlot = ClampSlot(slot);
        var comments = await _dbContext.JobRatings
            .AsNoTracking()
            .Where(r => r.LinkshellId == linkshellId
                && r.TargetAppUserId == targetAppUserId
                && r.RaterAppUserId != targetAppUserId
                && r.CharacterSlot == characterSlot
                && r.JobIndex == JobRating.PlayerCommentJobIndex
                && r.Comment != null && r.Comment != "")
            .OrderByDescending(r => r.UpdatedAt)
            .Select(r => r.Comment!)
            .ToListAsync(cancellationToken);

        var summarizer = HttpContext.RequestServices.GetRequiredService<AiCommentSummaryService>();
        var summary = await summarizer.SummarizeAsync(comments, cancellationToken);

        return Ok(new { commentCount = comments.Count, summary, configured = summarizer.IsConfigured });
    }
}

public sealed record ActivityJobRatingRequest(
    int LinkshellId,
    string TargetAppUserId,
    int JobIndex,
    int Gear,
    int Skill,
    bool HasRelic,
    int CharacterSlot = 0,
    string[]? RelicNames = null);

public sealed record ActivityJobRatingCommentRequest(
    int LinkshellId,
    string TargetAppUserId,
    string? Comment,
    int CharacterSlot = 0);
