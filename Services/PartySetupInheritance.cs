using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Carrying a camp's party setup board from one pop to the next.
//
// The problem this solves: ending an event DELETES the Event row (EndEventCoreAsync), so
// Event.PartySetupId goes with it. An HNM camp ended from the addon and re-created from its ToD
// therefore came back with no board attached, and an officer had to re-pick it every single pop.
//
// Two rules run through everything here:
//
//   * Only ever a TEMPLATE (PartySetup.OwnerEventId == null). A per-event snapshot is
//     cascade-deleted with its event, so inheriting one would hand the next pop a reference that
//     is about to become dangling — or, worse, a board shared with a camp that already ended.
//   * Never a cross-linkshell id. Every lookup here starts from data that can be edited by a
//     client, so the linkshell is re-checked on the way out rather than trusted.
public static class PartySetupInheritance
{
    // The template an ENDING event should be remembered as having run with.
    //
    // An unedited board points straight at the template. An edited one points at its own snapshot,
    // whose ClonedFromPartySetupId names the template it was cloned from — which is the entire
    // reason that column exists. Returns null when the origin template has since been deleted, or
    // for a snapshot created before the provenance column existed.
    public static async Task<int?> ResolveTemplateIdAsync(
        ApplicationDbContext db, Event eventEntity, CancellationToken cancellationToken)
    {
        if (eventEntity.PartySetupId is not { } setupId) return null;

        var setup = await db.PartySetups
            .AsNoTracking()
            .Where(item => item.Id == setupId)
            .Select(item => new { item.Id, item.OwnerEventId, item.ClonedFromPartySetupId, item.LinkshellId })
            .FirstOrDefaultAsync(cancellationToken);
        if (setup is null || setup.LinkshellId != eventEntity.LinkshellId) return null;

        // Already a template.
        if (setup.OwnerEventId is null) return setup.Id;

        // A snapshot: fall back to the template it was cloned from, if that still exists AND is
        // still a template. (An officer can delete a template while a camp is running.)
        if (setup.ClonedFromPartySetupId is not { } originId) return null;
        return await TemplateIdIfUsableAsync(db, originId, eventEntity.LinkshellId, cancellationToken);
    }

    // The board a NEWLY created pop of `monster` should start with, in preference order:
    //
    //   1. the monster's recurring board, when one is configured — that is the officer's explicit
    //      standing choice for this camp, so it outranks anything inferred;
    //   2. the most recent closed event for the same monster/name, via EventHistory.
    //
    // Mirrors how HnmAutoEventService already inherits DkpPerHour from the previous camp: same
    // idea, same fallback shape, so the two can't disagree about which camp "the last one" was.
    public static async Task<int?> ResolveForNewPopAsync(
        ApplicationDbContext db,
        int linkshellId,
        string? monster,
        string? eventName,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(monster))
        {
            var monsterNames = HnmConfig.MonsterMatchNamesLower(monster);
            var boardSetupId = await db.HnmRecurringBoards
                .AsNoTracking()
                .Where(board => board.LinkshellId == linkshellId
                                && board.PartySetupId != null
                                && monsterNames.Contains(board.MonsterName.ToLower()))
                .OrderByDescending(board => board.UpdatedAt)
                .Select(board => board.PartySetupId)
                .FirstOrDefaultAsync(cancellationToken);
            if (boardSetupId is { } fromBoard)
            {
                var usable = await TemplateIdIfUsableAsync(db, fromBoard, linkshellId, cancellationToken);
                if (usable is not null) return usable;
            }
        }

        // Closed-event fallback. Matched on the event NAME rather than the monster because that is
        // what EventHistory stores — and on the bare monster too, since an HNM name carries a
        // "D<n>" day suffix that changes every pop and so never matches the previous one.
        var candidateNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(eventName)) candidateNames.Add(eventName.Trim());
        if (!string.IsNullOrWhiteSpace(monster)) candidateNames.Add(monster.Trim());
        if (candidateNames.Count == 0) return null;

        var historySetupIds = await db.EventHistories
            .AsNoTracking()
            .Where(history => history.LinkshellId == linkshellId
                              && history.PartySetupId != null
                              && history.EventName != null
                              && candidateNames.Contains(history.EventName))
            .OrderByDescending(history => history.EndTime)
            .ThenByDescending(history => history.Id)
            .Select(history => history.PartySetupId!.Value)
            .Take(5)
            .ToListAsync(cancellationToken);

        foreach (var id in historySetupIds)
        {
            var usable = await TemplateIdIfUsableAsync(db, id, linkshellId, cancellationToken);
            if (usable is not null) return usable;
        }
        return null;
    }

    // `setupId` if it still exists, belongs to `linkshellId`, and is a template. Null otherwise.
    // Every path above funnels through this so a deleted / re-owned / cross-linkshell setup can
    // never be attached to a new event.
    private static async Task<int?> TemplateIdIfUsableAsync(
        ApplicationDbContext db, int setupId, int linkshellId, CancellationToken cancellationToken)
    {
        var ok = await db.PartySetups
            .AsNoTracking()
            .AnyAsync(item => item.Id == setupId
                              && item.LinkshellId == linkshellId
                              && item.OwnerEventId == null,
                cancellationToken);
        return ok ? setupId : null;
    }
}
