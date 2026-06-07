using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Shared "claim / release a party-setup slot" logic, used by both the Activity
// API (ActivityDataController.PartySetup) and the Discord interactions endpoint
// (claim-a-slot dropdown on an event announcement) so the rules stay in one
// place: a field the slot pins is taken from the slot (a crafted request can't
// override a hard requirement); an unpinned field must be supplied and
// catalog-valid; sub is optional; and a member holds at most one slot per setup
// (claiming releases any other). Callers load the slot (with its
// Party -> Alliance -> PartySetup chain) and verify linkshell membership first,
// then commit (this service never calls SaveChanges).
public static class PartySetupSignupService
{
    private static readonly HashSet<string> ValidRoles =
        new(EventJobCatalog.JobTypeOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidMainJobs =
        new(EventJobCatalog.MainJobOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidSubJobs =
        new(EventJobCatalog.SubJobOptions, StringComparer.OrdinalIgnoreCase);

    public sealed record ClaimResult(bool Success, string? Error);

    public sealed record JobResolution(bool Success, string? Error, string? Role, string? MainJob, string? SubJob);

    // Resolves the effective role / main / sub for a slot signup: a field the slot
    // pins is taken from the slot (a crafted request can't override a hard
    // requirement); an unpinned field must be supplied and catalog-valid; sub is
    // optional. Shared by template-slot signups and per-event signups.
    public static JobResolution ResolveSignupJobs(
        PartySetupSlot slot, string? requestedRole, string? requestedMainJob, string? requestedSubJob)
    {
        string? signedRole;
        if (!string.IsNullOrWhiteSpace(slot.Role))
        {
            signedRole = slot.Role;
        }
        else
        {
            var trimmed = requestedRole?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return new JobResolution(false, "Pick a role before signing up.", null, null, null);
            if (!ValidRoles.Contains(trimmed)) return new JobResolution(false, "That role isn't a valid pick.", null, null, null);
            signedRole = trimmed;
        }

        string? signedMain;
        if (!string.IsNullOrWhiteSpace(slot.MainJob))
        {
            signedMain = slot.MainJob;
        }
        else
        {
            var trimmed = requestedMainJob?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return new JobResolution(false, "Pick a main job before signing up.", null, null, null);
            if (!ValidMainJobs.Contains(trimmed)) return new JobResolution(false, "That main job isn't a valid pick.", null, null, null);
            signedMain = trimmed;
        }

        string? signedSub;
        if (!string.IsNullOrWhiteSpace(slot.SubJob))
        {
            signedSub = slot.SubJob;
        }
        else
        {
            var trimmed = requestedSubJob?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                signedSub = null;
            }
            else if (!ValidSubJobs.Contains(trimmed))
            {
                return new JobResolution(false, "That sub job isn't a valid pick.", null, null, null);
            }
            else
            {
                signedSub = trimmed;
            }
        }

        if (!string.IsNullOrWhiteSpace(signedMain)
            && string.Equals(signedMain, signedSub, StringComparison.OrdinalIgnoreCase))
        {
            return new JobResolution(false, "Main and sub job can't be the same.", null, null, null);
        }

        return new JobResolution(true, null, signedRole, signedMain, signedSub);
    }

    public static async Task<ClaimResult> ClaimSlotAsync(
        ApplicationDbContext db,
        PartySetupSlot slot,
        int setupId,
        string appUserId,
        string characterName,
        string? requestedRole,
        string? requestedMainJob,
        string? requestedSubJob,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(slot.SignedUpAppUserId) && slot.SignedUpAppUserId != appUserId)
        {
            return new ClaimResult(false,
                $"That slot was just taken by {slot.SignedUpCharacterName ?? "another member"}.");
        }

        var jobs = ResolveSignupJobs(slot, requestedRole, requestedMainJob, requestedSubJob);
        if (!jobs.Success)
        {
            return new ClaimResult(false, jobs.Error);
        }
        var signedRole = jobs.Role;
        var signedMain = jobs.MainJob;
        var signedSub = jobs.SubJob;

        // One slot per setup: release any other slot in this setup the member holds.
        var heldElsewhere = await db.PartySetupSlots
            .Where(s => s.SignedUpAppUserId == appUserId
                        && s.Party!.Alliance!.PartySetupId == setupId
                        && s.Id != slot.Id)
            .ToListAsync(cancellationToken);
        foreach (var held in heldElsewhere)
        {
            ClearSlotSignup(held);
        }

        slot.SignedUpAppUserId = appUserId;
        slot.SignedUpCharacterName = characterName;
        slot.SignedUpAtUtc = DateTime.UtcNow;
        slot.SignedUpRole = signedRole;
        slot.SignedUpMainJob = signedMain;
        slot.SignedUpSubJob = signedSub;

        return new ClaimResult(true, null);
    }

    public static void ClearSlotSignup(PartySetupSlot slot)
    {
        slot.SignedUpAppUserId = null;
        slot.SignedUpCharacterName = null;
        slot.SignedUpAtUtc = null;
        slot.SignedUpRole = null;
        slot.SignedUpMainJob = null;
        slot.SignedUpSubJob = null;
    }
}
