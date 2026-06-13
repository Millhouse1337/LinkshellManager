using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinkshellManagerDiscordApp.Controllers;

// Post-event discussion for the Activity: any member can read + comment (with an
// anonymous option); the author or an officer (CanManageEvents) can delete. New
// comments mirror to the linkshell's discussion Discord channel when configured.
public sealed partial class ActivityDataController
{
    [HttpGet("event-history/{id:int}/comments")]
    public async Task<IActionResult> GetEventCommentsAsync(int id, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to view the discussion." });

        var history = await _dbContext.EventHistories.AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (history is null) return NotFound(new { error = "Event not found." });

        var membership = await GetMembershipAsync(appUser.Id, history.LinkshellId, cancellationToken);
        if (membership is null) return Forbid();
        var canManage = await CanAsync(membership, r => r.CanManageEvents, cancellationToken);

        var service = HttpContext.RequestServices.GetRequiredService<EventCommentService>();
        var comments = await service.ListAsync(id, cancellationToken);
        var result = comments.Select(c => new
        {
            id = c.Id,
            author = c.IsAnonymous ? "Anonymous" : (c.CharacterName ?? "Member"),
            isAnonymous = c.IsAnonymous,
            body = c.Body,
            createdAt = c.CreatedAt,
            canDelete = canManage || string.Equals(c.AppUserId, appUser.Id, StringComparison.Ordinal),
        });
        return Ok(new { canManage, comments = result });
    }

    [HttpPost("event-history/{id:int}/comments")]
    public async Task<IActionResult> AddEventCommentAsync(int id, [FromBody] ActivityAddCommentRequest request, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to comment." });

        var history = await _dbContext.EventHistories.AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (history is null) return NotFound(new { error = "Event not found." });

        var membership = await GetMembershipAsync(appUser.Id, history.LinkshellId, cancellationToken);
        if (membership is null) return Forbid();
        if (string.IsNullOrWhiteSpace(request.Body)) return BadRequest(new { error = "Write something first." });

        var character = membership.CharacterName ?? appUser.CharacterName;
        var service = HttpContext.RequestServices.GetRequiredService<EventCommentService>();
        var comment = await service.AddAsync(id, appUser.Id, character, request.Body, request.IsAnonymous, cancellationToken);
        return comment is null
            ? BadRequest(new { error = "Could not post the comment." })
            : Ok(new { success = true });
    }

    [HttpPost("event-history/comments/{commentId:int}/delete")]
    public async Task<IActionResult> DeleteEventCommentAsync(int commentId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to delete." });

        var comment = await _dbContext.EventComments.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null) return NotFound(new { error = "Comment not found." });

        var membership = await GetMembershipAsync(appUser.Id, comment.LinkshellId, cancellationToken);
        if (membership is null) return Forbid();
        var canManage = await CanAsync(membership, r => r.CanManageEvents, cancellationToken);

        var service = HttpContext.RequestServices.GetRequiredService<EventCommentService>();
        var ok = await service.DeleteAsync(commentId, appUser.Id, canManage, cancellationToken);
        return ok ? Ok(new { success = true }) : Forbid();
    }
}

public sealed record ActivityAddCommentRequest(string Body, bool IsAnonymous);
