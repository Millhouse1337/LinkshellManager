using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class TodController : Controller
{
    private static readonly HashSet<string> SupportedCooldowns = new(TodManagerViewModel.SupportedCooldowns, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SupportedIntervals = new(TodManagerViewModel.SupportedIntervals, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LongWindowMonsters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tiamat",
        "Jormungand",
        "Vrtra",
        "Cerberus",
        "Hydra",
        "Khimaira"
    };

    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly AdminOverrideService _adminOverride;
    private readonly TimeZoneConversionService _timeZones;
    private readonly SubmissionApprovalService _submissionApproval;

    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;
    private readonly MonsterTimingResolver _monsterTimings;

    public TodController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        AdminOverrideService adminOverride,
        TimeZoneConversionService timeZones,
        SubmissionApprovalService submissionApproval,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        MonsterTimingResolver monsterTimings)
    {
        _context = context;
        _userManager = userManager;
        _adminOverride = adminOverride;
        _timeZones = timeZones;
        _submissionApproval = submissionApproval;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _monsterTimings = monsterTimings;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var model = await BuildViewModelAsync(user, new TodManagerViewModel
        {
            Tod = new Tod
            {
                LinkshellId = user.PrimaryLinkshellId ?? 0,
                Claim = true,
                Cooldown = TodManagerViewModel.TwentyTwoHourCooldown,
                Interval = TodManagerViewModel.TenMinuteInterval
            }
        });

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var model = await BuildViewModelAsync(user, new TodManagerViewModel
        {
            Tod = new Tod
            {
                LinkshellId = user.PrimaryLinkshellId ?? 0,
                Claim = true,
                Cooldown = TodManagerViewModel.TwentyTwoHourCooldown,
                Interval = TodManagerViewModel.TenMinuteInterval
            }
        });

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(5_500_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 5_500_000)]
    public async Task<IActionResult> Create(
        TodManagerViewModel model,
        [FromForm] IFormFile? uploadImage,
        [FromServices] TodImageUploadService uploads,
        CancellationToken cancellationToken)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        // Save the optional screenshot up front so its path is on the Tod
        // record at insert time. Validation errors here surface in
        // ModelState the same way the rest of the form does.
        string? uploadedImagePath = null;
        if (uploadImage is not null && uploadImage.Length > 0)
        {
            var uploadResult = await uploads.SaveAsync(uploadImage, cancellationToken);
            if (!uploadResult.Success)
            {
                ModelState.AddModelError(nameof(uploadImage), uploadResult.Error ?? "Image upload failed.");
            }
            else
            {
                uploadedImagePath = uploadResult.ImagePath;
            }
        }

        model.Tod ??= new Tod { Claim = true };
        ResolveCustomMonsterName(model);
        model.Tod.LinkshellId = await ResolveActiveLinkshellIdAsync(user);
        await ApplyPostedDurationsAsync(model, HttpContext.RequestAborted);

        var hasLinkshellAccess = model.Tod.LinkshellId > 0 && await HasLinkshellAccessAsync(user.Id, model.Tod.LinkshellId);
        var linkshellCharacterNames = hasLinkshellAccess
            ? await _context.AppUserLinkshells
                .Where(link => link.LinkshellId == model.Tod.LinkshellId)
                .Select(link => link.CharacterName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToListAsync()
            : new List<string>();

        ValidateTodSubmission(model, hasLinkshellAccess, linkshellCharacterNames);
        if (!ModelState.IsValid)
        {
            return View(nameof(Create), await BuildViewModelAsync(user, model));
        }

        var role = await GetEffectiveRoleAsync(user.Id, model.Tod.LinkshellId);
        var canManage = role?.CanManageTods == true;
        var canSubmitForApproval = role?.CanSubmitTodForApproval == true;
        if (!canManage && !canSubmitForApproval)
        {
            return Forbid();
        }

        var todTimeUtc = ConvertUserTimeZoneToUtc(model.Tod.Time, user.TimeZone);
        var occurredAtUtc = DateTime.UtcNow;

        // Member submit-for-approval path: stash the submission as a pending row
        // and bail before touching the live Tod / DKP tables.
        if (!canManage)
        {
            var loot = model.Tod.Claim == true && !model.NoLoot
                ? NormalizeLootDetails(model.TodLootDetails)
                : new List<TodLootDetail>();

            var input = new TodSubmissionInput(
                model.Tod.MonsterName?.Trim(),
                model.Tod.DayNumber,
                model.Tod.Claim,
                todTimeUtc,
                model.Tod.Cooldown,
                model.Tod.Interval,
                todTimeUtc?.AddHours(ResolveCooldownHours(model.Tod.Cooldown)),
                uploadedImagePath,
                loot.Select(l => new TodSubmissionLootInput(l.ItemName, l.ItemWinner, l.WinningDkpSpent)).ToList());

            await _submissionApproval.QueueTodAsync(model.Tod.LinkshellId, user.Id, input, cancellationToken);
            TempData["TodSubmissionPending"] = "ToD submitted for officer approval.";
            return RedirectToAction(nameof(Index));
        }

        var newTod = new Tod
        {
            MonsterName = model.Tod.MonsterName?.Trim(),
            DayNumber = model.Tod.DayNumber,
            Claim = model.Tod.Claim,
            Time = todTimeUtc,
            Cooldown = model.Tod.Cooldown,
            RepopTime = todTimeUtc?.AddHours(ResolveCooldownHours(model.Tod.Cooldown)),
            Interval = model.Tod.Interval,
            LinkshellId = model.Tod.LinkshellId,
            TimeStamp = occurredAtUtc,
            TotalTods = 1,
            TotalClaims = model.Tod.Claim == true ? 1 : 0,
            ImagePath = uploadedImagePath,
        };

        _context.Tods.Add(newTod);
        await _context.SaveChangesAsync();

        var normalizedLootDetails = model.Tod.Claim == true && !model.NoLoot
            ? NormalizeLootDetails(model.TodLootDetails)
            : new List<TodLootDetail>();
        if (normalizedLootDetails.Count > 0)
        {
            foreach (var lootDetail in normalizedLootDetails)
            {
                lootDetail.TodId = newTod.Id;
            }

            await _context.TodLootDetails.AddRangeAsync(normalizedLootDetails);
            // Was a web-only copy of this logic that silently ignored LootStructure — a Hybrid or
            // LootCouncil linkshell recording a ToD here got flat DKP deductions anyway. Now it's
            // the same shared path the Activity and the addon use.
            var insufficient = await ActivityDataController.AdjustTodLootDkpAsync(
                _context, _dkpLedger, _dkpPools, newTod, normalizedLootDetails, occurredAtUtc,
                isRefund: false, HttpContext.RequestAborted);
            if (insufficient is not null)
            {
                // The ToD row is already committed — the kill happened. Only the unaffordable loot
                // is dropped; it can be re-added from Loot History once the DKP is sorted.
                TempData["TodMessage"] = $"{insufficient} The ToD was recorded without its loot.";
                return RedirectToAction(nameof(Index));
            }
            await _context.SaveChangesAsync();
        }

        // A new ToD = a new pop window, so reset any party sign-ups assigned to
        // this monster (the old roster is for the pop that just happened).
        await PartySetupController.ClearSignupsForMonsterAsync(_context, newTod.LinkshellId, newTod.MonsterName);

        // The tracker writes only the Tod row, so any board parked waiting to re-post would keep
        // showing the old pop time until it actually re-posted. Re-point it at this ToD's repop.
        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(
            _context, newTod.LinkshellId, newTod.MonsterName, HttpContext.RequestAborted);

        return RedirectToAction(nameof(Index));
    }

    private async Task<LinkshellRole?> GetEffectiveRoleAsync(string appUserId, int linkshellId)
    {
        // The membership ROW, not just the rank string: a null rank and a missing
        // membership are otherwise indistinguishable, and the override below must
        // only ever fire for an actual member.
        var membership = await _context.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.AppUserId == appUserId && m.LinkshellId == linkshellId);
        if (membership is null) return null;

        if (await _adminOverride.IsActiveForAsync(appUserId, HttpContext.RequestAborted))
        {
            return LinkshellRoleDefaults.BuildFullAccessRole(linkshellId);
        }

        var rank = membership.Rank;
        if (rank is null) return null;
        var rankName = string.IsNullOrWhiteSpace(rank) ? "Member" : rank.Trim();
        return await _context.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == rankName);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Challenge();

        var tod = await _context.Tods
            .Include(t => t.TodLootDetails)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (tod is null) return NotFound();

        if (!await HasLinkshellAccessAsync(user.Id, tod.LinkshellId)) return Forbid();
        var role = await GetEffectiveRoleAsync(user.Id, tod.LinkshellId);
        if (role?.CanManageTods != true) return Forbid();

        // Convert UTC -> user local for the datetime-local inputs the form binds to.
        var localTime = ConvertUtcToUserTimeZone(tod.Time, user.TimeZone);
        var localRepop = ConvertUtcToUserTimeZone(tod.RepopTime, user.TimeZone);

        var model = await BuildViewModelAsync(user, new TodManagerViewModel
        {
            Tod = new Tod
            {
                Id = tod.Id,
                LinkshellId = tod.LinkshellId,
                MonsterName = tod.MonsterName,
                DayNumber = tod.DayNumber,
                Claim = tod.Claim,
                Time = localTime,
                Cooldown = tod.Cooldown,
                Interval = tod.Interval,
                RepopTime = localRepop,
                ImagePath = tod.ImagePath,
            },
            TodLootDetails = tod.TodLootDetails
                .Select(l => new TodLootDetail { Id = l.Id, ItemName = l.ItemName, ItemWinner = l.ItemWinner, WinningDkpSpent = l.WinningDkpSpent })
                .ToList(),
            NoLoot = tod.TodLootDetails.Count == 0,
        });

        return View(nameof(Create), model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(5_500_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 5_500_000)]
    public async Task<IActionResult> Edit(
        int id,
        TodManagerViewModel model,
        [FromForm] IFormFile? uploadImage,
        [FromForm] bool clearImage,
        [FromServices] TodImageUploadService uploads,
        CancellationToken cancellationToken)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Challenge();

        var tod = await _context.Tods
            .Include(t => t.TodLootDetails)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tod is null) return NotFound();

        if (!await HasLinkshellAccessAsync(user.Id, tod.LinkshellId)) return Forbid();
        var role = await GetEffectiveRoleAsync(user.Id, tod.LinkshellId);
        if (role?.CanManageTods != true) return Forbid();

        // Preserve the existing image unless the officer uploaded a new one
        // or explicitly clicked the clear checkbox/button.
        string? newImagePath = tod.ImagePath;
        if (uploadImage is not null && uploadImage.Length > 0)
        {
            var uploadResult = await uploads.SaveAsync(uploadImage, cancellationToken);
            if (!uploadResult.Success)
            {
                ModelState.AddModelError(nameof(uploadImage), uploadResult.Error ?? "Image upload failed.");
            }
            else
            {
                newImagePath = uploadResult.ImagePath;
            }
        }
        else if (clearImage)
        {
            newImagePath = null;
        }

        model.Tod ??= new Tod { Claim = true };
        ResolveCustomMonsterName(model);
        model.Tod.Id = id;
        model.Tod.LinkshellId = tod.LinkshellId;
        await ApplyPostedDurationsAsync(model, HttpContext.RequestAborted);

        var characterNames = await _context.AppUserLinkshells
            .Where(link => link.LinkshellId == tod.LinkshellId)
            .Select(link => link.CharacterName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToListAsync(cancellationToken);

        ValidateTodSubmission(model, hasLinkshellAccess: true, characterNames);
        if (!ModelState.IsValid)
        {
            return View(nameof(Create), await BuildViewModelAsync(user, model));
        }

        var todTimeUtc = ConvertUserTimeZoneToUtc(model.Tod.Time, user.TimeZone);
        var occurredAtUtc = DateTime.UtcNow;

        // A form that carries NO loot rows means "leave the loot alone", not "delete it".
        //
        // This form has never HAD loot inputs -- loot is recorded in the Loot section -- so without
        // this, correcting a typo in the time of an old ToD that still carries legacy loot would
        // refund and destroy it as a side effect. Mirrors ActivityDataController.UpdateTodAsync.
        var replaceLoot = model.TodLootDetails is { Count: > 0 };

        // Reverse DKP impact from existing loot, remove it, then apply the new set.
        if (replaceLoot && tod.TodLootDetails.Count > 0)
        {
            await ActivityDataController.AdjustTodLootDkpAsync(_context, _dkpLedger, _dkpPools, tod, tod.TodLootDetails.ToList(), occurredAtUtc, isRefund: true, cancellationToken);
            _context.TodLootDetails.RemoveRange(tod.TodLootDetails);
        }

        var previousImage = tod.ImagePath;
        tod.MonsterName = model.Tod.MonsterName?.Trim();
        tod.DayNumber = model.Tod.DayNumber;
        tod.Claim = model.Tod.Claim;
        tod.Time = todTimeUtc;
        tod.Cooldown = model.Tod.Cooldown;
        tod.RepopTime = todTimeUtc?.AddHours(ResolveCooldownHours(model.Tod.Cooldown));
        tod.Interval = model.Tod.Interval;
        tod.TimeStamp = occurredAtUtc;
        tod.TotalClaims = model.Tod.Claim == true ? 1 : 0;
        tod.ImagePath = newImagePath;

        await _context.SaveChangesAsync(cancellationToken);

        var normalizedLootDetails = model.Tod.Claim == true && !model.NoLoot
            ? NormalizeLootDetails(model.TodLootDetails)
            : new List<TodLootDetail>();
        if (normalizedLootDetails.Count > 0)
        {
            foreach (var lootDetail in normalizedLootDetails)
            {
                lootDetail.TodId = tod.Id;
            }
            await _context.TodLootDetails.AddRangeAsync(normalizedLootDetails, cancellationToken);
            var insufficient = await ActivityDataController.AdjustTodLootDkpAsync(_context, _dkpLedger, _dkpPools, tod, normalizedLootDetails, occurredAtUtc, isRefund: false, cancellationToken);
            if (insufficient is not null)
            {
                // The ToD edit is already committed; only the unaffordable loot is dropped. The old
                // loot was refunded earlier in this action.
                TempData["TodMessage"] = $"{insufficient} The ToD was updated without its loot.";
                return RedirectToAction(nameof(Index));
            }
            await _context.SaveChangesAsync(cancellationToken);
        }

        // A corrected repop has to reach any board parked waiting on it: re-point its displayed
        // pop / re-post time, and re-open the cycle if the poller had already given up on it.
        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(
            _context, tod.LinkshellId, tod.MonsterName, cancellationToken);

        if (!string.IsNullOrWhiteSpace(previousImage) && !string.Equals(previousImage, newImagePath, StringComparison.Ordinal))
        {
            // Best-effort cleanup of the orphaned old file.
            try
            {
                var webRoot = HttpContext.RequestServices.GetService<IWebHostEnvironment>()?.WebRootPath;
                if (!string.IsNullOrWhiteSpace(webRoot))
                {
                    var rel = previousImage.TrimStart('/');
                    var abs = Path.Combine(webRoot, rel);
                    if (System.IO.File.Exists(abs)) System.IO.File.Delete(abs);
                }
            }
            catch { /* ignore cleanup failures */ }
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> GetLootDetails(int id)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var tod = await _context.Tods
            .AsNoTracking()
            .Include(item => item.TodLootDetails)
            .FirstOrDefaultAsync(item => item.Id == id);
        if (tod is null)
        {
            return NotFound();
        }

        if (!await HasLinkshellAccessAsync(user.Id, tod.LinkshellId))
        {
            return Forbid();
        }

        return Json(tod.TodLootDetails
            .OrderBy(detail => detail.Id)
            .Select(detail => new
            {
                itemName = detail.ItemName,
                itemWinner = detail.ItemWinner,
                winningDkpSpent = detail.WinningDkpSpent
            }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var tod = await _context.Tods
            .Include(item => item.TodLootDetails)
            .FirstOrDefaultAsync(item => item.Id == id);
        if (tod is null)
        {
            return NotFound();
        }

        if (!await HasLinkshellAccessAsync(user.Id, tod.LinkshellId))
        {
            return Forbid();
        }

        await ActivityDataController.AdjustTodLootDkpAsync(
            _context, _dkpLedger, _dkpPools, tod, tod.TodLootDetails.ToList(), DateTime.UtcNow,
            isRefund: true, HttpContext.RequestAborted);
        _context.TodLootDetails.RemoveRange(tod.TodLootDetails);
        _context.Tods.Remove(tod);
        await _context.SaveChangesAsync();

        // Deleting the ToD a parked board was counting on leaves it advertising a pop that no
        // longer exists — fall back to the monster's next-newest ToD, or to no time at all.
        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(
            _context, tod.LinkshellId, tod.MonsterName, HttpContext.RequestAborted);

        return RedirectToAction(nameof(Index));
    }

    private async Task<TodManagerViewModel> BuildViewModelAsync(AppUser user, TodManagerViewModel? source = null)
    {
        var linkshells = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => link.Linkshell!)
            .OrderBy(link => link.LinkshellName)
            .ToListAsync();

        var selectedLinkshellId = user.PrimaryLinkshellId.HasValue && linkshells.Any(link => link.Id == user.PrimaryLinkshellId.Value)
            ? user.PrimaryLinkshellId.Value
            : linkshells.FirstOrDefault()?.Id ?? 0;

        // The linkshell's own monster catalog, resolved once: it drives the picker AND the
        // per-monster pre-fill hints the form's JS reads.
        var monsterTimings = await _monsterTimings.GetMapAsync(selectedLinkshellId, HttpContext.RequestAborted);

        var characterNames = selectedLinkshellId > 0
            ? await _context.AppUserLinkshells
                .Where(link => link.LinkshellId == selectedLinkshellId)
                .Select(link => link.CharacterName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .OrderBy(name => name)
                .ToListAsync()
            : new List<string>();

        // Per-linkshell hidden-mob list (configured from the Activity's
        // Customize panel). Filter the list out before mapping so hidden
        // monsters don't appear in the legacy MVC ToD table either.
        var hiddenMonsters = selectedLinkshellId > 0
            ? await _context.Linkshells
                .AsNoTracking()
                .Where(ls => ls.Id == selectedLinkshellId)
                .Select(ls => ls.HiddenTodMonsters)
                .FirstOrDefaultAsync() ?? string.Empty
            : string.Empty;
        var hiddenSet = new HashSet<string>(
            hiddenMonsters.Split('|', StringSplitOptions.RemoveEmptyEntries)
                          .Select(s => s.Trim())
                          .Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var todEntities = selectedLinkshellId > 0
            ? await _context.Tods
                .AsNoTracking()
                .Include(item => item.TodLootDetails)
                .Where(item => item.LinkshellId == selectedLinkshellId)
                // Time ?? TimeStamp so a "Not entered" ToD (camp ended without a kill) still sorts
                // as the monster's newest row rather than falling to the bottom of the list.
                .OrderByDescending(item => item.Time ?? item.TimeStamp)
                .ThenByDescending(item => item.Id)
                .ToListAsync()
            : new List<Tod>();
        if (hiddenSet.Count > 0)
        {
            todEntities = todEntities
                .Where(t => !hiddenSet.Contains((t.MonsterName ?? string.Empty).Trim()))
                .ToList();
        }
        // NOTE: true HNMs are intentionally NOT filtered here. The Add ToD
        // form's monster picker is the curated HNM list, so excluding HNMs
        // made every web-submitted ToD vanish from this page (it saved but
        // never appeared in "Submitted ToDs"). There is no separate HNM
        // section on this view, so the single table shows them all; the
        // per-linkshell hidden-monsters list above is still honored.

        // Party Setups assigned to a monster, loaded as full trees so the ToD
        // Tracker can show the planned composition inline (expand in place, no
        // page nav) and let members sign up for a slot. Freshest setup per
        // monster (UpdatedAt desc) wins. The simple Id/Name dictionary is
        // derived from the same boards so the two can't disagree.
        var assignedSetupEntities = selectedLinkshellId > 0
            ? await _context.PartySetups
                .AsNoTracking()
                .Include(ps => ps.Alliances).ThenInclude(a => a.Parties).ThenInclude(p => p.Slots)
                .Where(ps => ps.LinkshellId == selectedLinkshellId && ps.AssignedMonsterName != null)
                .OrderByDescending(ps => ps.UpdatedAt)
                .ToListAsync()
            : new List<Models.PartySetup>();

        var assignedPartySetupBoards = assignedSetupEntities
            .GroupBy(ps => ps.AssignedMonsterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var ps = g.First(); // list is UpdatedAt desc, so this is freshest
                return new PartySetupBoardViewModel
                {
                    Id = ps.Id,
                    Name = ps.Name,
                    Alliances = ps.Alliances.OrderBy(a => a.SortOrder).Select(a => new PartySetupAllianceView
                    {
                        Name = string.IsNullOrWhiteSpace(a.Name) ? $"Alliance {a.SortOrder + 1}" : a.Name,
                        Parties = a.Parties.OrderBy(p => p.SortOrder).Select(p => new PartySetupPartyView
                        {
                            Name = string.IsNullOrWhiteSpace(p.Name) ? $"Party {p.SortOrder + 1}" : p.Name!,
                            Slots = p.Slots.OrderBy(s => s.SortOrder).Select(s => new PartySetupSlotView
                            {
                                SlotId = s.Id,
                                Position = s.SortOrder + 1,
                                RequirementType = s.RequirementType,
                                Role = s.Role,
                                MainJob = s.MainJob,
                                SubJob = s.SubJob,
                                Label = s.Label,
                                IsPartyLeader = s.IsPartyLeader,
                                SignedUpAppUserId = s.SignedUpAppUserId,
                                SignedUpCharacterName = s.SignedUpCharacterName,
                                SignedUpRole = s.SignedUpRole,
                                SignedUpMainJob = s.SignedUpMainJob,
                                SignedUpSubJob = s.SignedUpSubJob
                            }).ToList()
                        }).ToList()
                    }).ToList()
                };
            }, StringComparer.OrdinalIgnoreCase);

        var assignedPartySetups = assignedPartySetupBoards
            .ToDictionary(kv => kv.Key, kv => (kv.Value.Id, kv.Value.Name), StringComparer.OrdinalIgnoreCase);

        // Recent claim-shield lottery windows posted from the lsm addon.
        var recentClaimShields = selectedLinkshellId > 0
            ? await _context.ClaimShieldCaptures
                .AsNoTracking()
                .Include(c => c.Members)
                .Where(c => c.LinkshellId == selectedLinkshellId)
                .OrderByDescending(c => c.CapturedAtUtc)
                .ThenByDescending(c => c.Id)
                .Take(10)
                .ToListAsync()
            : new List<ClaimShieldCapture>();

        var todDraft = source?.Tod ?? new Tod();
        todDraft.LinkshellId = selectedLinkshellId;

        todDraft.Claim = source?.Tod?.Claim ?? todDraft.Claim;
        var draftTiming = monsterTimings.For(todDraft.MonsterName);
        todDraft.Cooldown = string.IsNullOrWhiteSpace(todDraft.Cooldown)
            ? TodDurationFormat.Format(draftTiming.CooldownMinutes)
            : todDraft.Cooldown;
        todDraft.Interval = string.IsNullOrWhiteSpace(todDraft.Interval)
            ? TodDurationFormat.Format(draftTiming.TodIntervalMinutes)
            : todDraft.Interval;

        var lootDetails = source?.TodLootDetails?.Count > 0
            ? source.TodLootDetails
            : new List<TodLootDetail> { new() };

        // CanCreateImmediately drives the submit-button label: members who only
        // have CanSubmitTodForApproval see "Submit for Approval" + a hint that
        // an officer will review.
        var role = selectedLinkshellId > 0
            ? await GetEffectiveRoleAsync(user.Id, selectedLinkshellId)
            : null;
        var canCreateImmediately = role?.CanManageTods == true;

        return new TodManagerViewModel
        {
            LinkshellId = selectedLinkshellId,
            Linkshells = linkshells,
            Tod = todDraft,
            TodItems = todEntities.Select(item => new TodTableRowViewModel
            {
                Id = item.Id,
                MonsterName = item.MonsterName ?? string.Empty,
                DayNumber = item.DayNumber,
                // No time / no repop = the camp ended without anyone seeing it die, so nothing was
                // recorded. Say "Not entered" rather than a bare dash, which reads as missing data.
                TodDisplay = ConvertUtcToUserTimeZone(item.Time, user.TimeZone)?.ToString("M/d/yyyy h:mm:ss tt") ?? "Not entered",
                Cooldown = item.Cooldown ?? string.Empty,
                RepopTimeDisplay = ConvertUtcToUserTimeZone(item.RepopTime, user.TimeZone)?.ToString("M/d/yyyy h:mm:ss tt") ?? "Not entered",
                Interval = item.Interval ?? string.Empty,
                RepopTimeUtc = item.RepopTime,
                Claim = item.Claim,
                LootCount = item.TodLootDetails.Count,
                ImagePath = item.ImagePath,
            }).ToList(),
            TodLootDetails = lootDetails,
            NoLoot = source?.NoLoot ?? false,
            Notifications = source?.Notifications ?? new List<string>(),
            // The linkshell's OWN monster catalog, already stored with each NQ/HQ pair as one
            // combined "Base/Stronger" row -- the same list the create-event form and the
            // Activity's ToD picker show, and the same form the sign-up board stores in
            // Event.AssignedMonsterName. Includes any monster the linkshell added itself.
            // "Other" is appended in the view to reveal a free-text field for anything not listed.
            MonsterOptions = monsterTimings.EventMonsterOptions.ToList(),
            // Per-monster cooldown / cadence, so the form can pre-fill the moment a monster is
            // picked without a round trip. Keyed by the exact option text above.
            MonsterTimings = monsterTimings.Rows.Count > 0
                ? monsterTimings.Rows.ToDictionary(
                    row => row.MonsterName,
                    row => new TodMonsterTimingHint(row.CooldownMinutes, row.WindowCadenceMinutes),
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, TodMonsterTimingHint>(StringComparer.OrdinalIgnoreCase),
            CharacterNames = characterNames,
            CanCreateImmediately = canCreateImmediately,
            AssignedPartySetups = assignedPartySetups,
            AssignedPartySetupBoards = assignedPartySetupBoards,
            CurrentAppUserId = user.Id,
            CanManagePartiesForSelected = role?.CanManageParties == true,
            RecentClaimShieldCaptures = recentClaimShields.Select(c => new ClaimShieldCaptureRowViewModel
            {
                Id = c.Id,
                MonsterName = c.MonsterName,
                Won = c.Won,
                TotalPlayers = c.TotalPlayers,
                CapturedAtDisplay = ConvertUtcToUserTimeZone(c.CapturedAtUtc, user.TimeZone)?.ToString("M/d/yyyy h:mm:ss tt") ?? "-",
                Members = c.Members
                    .OrderByDescending(m => m.Matched)
                    .ThenBy(m => m.CharacterName)
                    .Select(m => m.CharacterName)
                    .ToList(),
                MatchedCount = c.MatchedCount,
            }).ToList(),
        };
    }

    // "Other" in the picker means the real name is typed into the free-text
    // field; fold it back into Tod.MonsterName (and trim normal picks) so the
    // rest of the pipeline only ever sees the actual monster name.
    private static void ResolveCustomMonsterName(TodManagerViewModel model)
    {
        if (model.Tod is null)
        {
            return;
        }
        var picked = model.Tod.MonsterName?.Trim();
        if (string.Equals(picked, TodManagerViewModel.OtherMonster, StringComparison.OrdinalIgnoreCase))
        {
            model.Tod.MonsterName = model.CustomMonsterName?.Trim();
        }
        else
        {
            model.Tod.MonsterName = picked;
        }
    }

    private void ValidateTodSubmission(TodManagerViewModel model, bool hasLinkshellAccess, IReadOnlyCollection<string> validCharacterNames)
    {
        if (model.Tod.LinkshellId <= 0 || !hasLinkshellAccess)
        {
            ModelState.AddModelError("Tod.LinkshellId", "Select a linkshell you can access.");
        }

        // The picker now allows the full curated list plus a free-text
        // "Other" name (resolved into MonsterName before this runs), so we
        // only require a non-blank, reasonably bounded value.
        var monsterName = model.Tod.MonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monsterName))
        {
            ModelState.AddModelError("Tod.MonsterName",
                "Select a monster, or choose Other and enter a name.");
        }
        else if (monsterName.Length > 64)
        {
            ModelState.AddModelError("Tod.MonsterName",
                "Monster name is too long (64 characters max).");
        }

        if (!model.Tod.Time.HasValue)
        {
            ModelState.AddModelError("Tod.Time", "Enter a Time of Death.");
        }

        // Free-form rather than preset-only: a monster's cooldown and cadence are configured
        // per-linkshell now, so the form composes an arbitrary "<number> <unit>" and the shared
        // parser decides whether it reads as a positive duration.
        if (!ActivityDataController.IsAcceptableTodCooldown(model.Tod.Cooldown))
        {
            ModelState.AddModelError("Tod.Cooldown", "Enter a valid cooldown (a positive number of hours or minutes).");
        }

        if (!ActivityDataController.IsAcceptableTodInterval(model.Tod.Interval))
        {
            ModelState.AddModelError("Tod.Interval", "Enter a valid interval (a positive number of hours or minutes).");
        }

        for (var index = 0; index < model.TodLootDetails.Count; index++)
        {
            var lootDetail = model.TodLootDetails[index];
            var hasAnyValue = !string.IsNullOrWhiteSpace(lootDetail.ItemName)
                || !string.IsNullOrWhiteSpace(lootDetail.ItemWinner)
                || lootDetail.WinningDkpSpent.HasValue;
            if (!hasAnyValue)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(lootDetail.ItemName))
            {
                ModelState.AddModelError($"TodLootDetails[{index}].ItemName", "Enter an item name.");
            }

            if (string.IsNullOrWhiteSpace(lootDetail.ItemWinner))
            {
                ModelState.AddModelError($"TodLootDetails[{index}].ItemWinner", "Select an item winner.");
            }
            else if (!validCharacterNames.Contains(lootDetail.ItemWinner.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                ModelState.AddModelError($"TodLootDetails[{index}].ItemWinner", "Select a winner from the current linkshell.");
            }

            if (!lootDetail.WinningDkpSpent.HasValue || lootDetail.WinningDkpSpent <= 0)
            {
                ModelState.AddModelError($"TodLootDetails[{index}].WinningDkpSpent", "Enter DKP spent as a positive number.");
            }
        }
    }

    private static List<TodLootDetail> NormalizeLootDetails(IEnumerable<TodLootDetail>? lootDetails)
    {
        return lootDetails?
            .Where(detail =>
                !string.IsNullOrWhiteSpace(detail.ItemName)
                || !string.IsNullOrWhiteSpace(detail.ItemWinner)
                || detail.WinningDkpSpent.HasValue)
            .Select(detail => new TodLootDetail
            {
                ItemName = detail.ItemName?.Trim(),
                ItemWinner = detail.ItemWinner?.Trim(),
                WinningDkpSpent = detail.WinningDkpSpent
            })
            .ToList() ?? new List<TodLootDetail>();
    }

    private async Task<AppUser?> RequireCurrentUserAsync()
    {
        return await _userManager.GetUserAsync(User);
    }

    private async Task<bool> HasLinkshellAccessAsync(string userId, int linkshellId)
    {
        return await _context.AppUserLinkshells.AnyAsync(link => link.AppUserId == userId && link.LinkshellId == linkshellId);
    }

    private async Task<int> ResolveActiveLinkshellIdAsync(AppUser user)
    {
        var linkshellIds = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .OrderBy(link => link.Linkshell!.LinkshellName)
            .Select(link => link.LinkshellId)
            .ToListAsync();

        if (user.PrimaryLinkshellId.HasValue && linkshellIds.Contains(user.PrimaryLinkshellId.Value))
        {
            return user.PrimaryLinkshellId.Value;
        }

        return linkshellIds.FirstOrDefault();
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
        => _timeZones.ToUserTime(utcDateTime, timeZoneId);

    private DateTime? ConvertUserTimeZoneToUtc(DateTime? localDateTime, string? timeZoneId)
        => _timeZones.ToUtc(localDateTime, timeZoneId);

    // Delegates to the Activity's parser, which is the only implementation that reads every label
    // this form actually offers. The local copy this replaced compared against "72 Hour" and fell
    // through to 22 for everything else, so an 84/71/2 Hour or 5 Min ToD picked on the web silently
    // stored a 22-hour repop.
    private static double ResolveCooldownHours(string? cooldown) =>
        ActivityDataController.ResolveTodCooldownHours(cooldown);

    // Composes the posted number + unit into the label form Tod.Cooldown / Tod.Interval store, and
    // falls back to the LINKSHELL'S configured value for the monster — not a hardcoded 22h/72h
    // split, which is what this used to do and which ignored the configuration entirely.
    //
    // Runs before ValidateTodSubmission, so what gets validated is what gets saved.
    private async Task ApplyPostedDurationsAsync(TodManagerViewModel model, CancellationToken cancellationToken)
    {
        var timing = await _monsterTimings.ResolveAsync(
            model.Tod.LinkshellId, model.Tod.MonsterName, cancellationToken);

        var cooldown = model.CooldownValue is > 0
            ? TodDurationFormat.Format(
                TodDurationFormat.FromValueAndUnit(model.CooldownValue.Value, model.CooldownUnit))
            : null;
        model.Tod.Cooldown = cooldown
            ?? (string.IsNullOrWhiteSpace(model.Tod.Cooldown)
                ? TodDurationFormat.Format(timing.CooldownMinutes)
                : model.Tod.Cooldown.Trim());

        // A blank interval is legitimate — it means "not recorded" — so an explicitly cleared
        // field is only backfilled when nothing at all came through.
        var interval = model.IntervalValue is > 0
            ? TodDurationFormat.Format(
                TodDurationFormat.FromValueAndUnit(model.IntervalValue.Value, model.IntervalUnit))
            : null;
        model.Tod.Interval = interval
            ?? (string.IsNullOrWhiteSpace(model.Tod.Interval)
                ? TodDurationFormat.Format(timing.TodIntervalMinutes)
                : model.Tod.Interval.Trim());
    }
}
