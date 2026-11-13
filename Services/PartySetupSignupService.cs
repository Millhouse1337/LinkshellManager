using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;

namespace LinkshellManagerDiscordApp.Services;

// The job rules for signing up to a party slot. Members only sign up on an
// EVENT's board now (EventPartySignupService, used by the Activity and the
// Discord board); the old "sign up on the template itself" path was removed
// because those sign-ups never reached the event or Discord board.
public static class PartySetupSignupService
{
    private static readonly HashSet<string> ValidRoles =
        new(EventJobCatalog.JobTypeOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidMainJobs =
        new(EventJobCatalog.MainJobOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidSubJobs =
        new(EventJobCatalog.SubJobOptions, StringComparer.OrdinalIgnoreCase);

    public sealed record JobResolution(bool Success, string? Error, string? Role, string? MainJob, string? SubJob);

    // Resolves the effective role / main / sub for a slot signup: a field the slot
    // pins is taken from the slot (a crafted request can't override a hard
    // requirement); an unpinned field must be supplied and catalog-valid; sub is
    // optional.
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
}
