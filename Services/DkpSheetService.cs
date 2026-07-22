using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// The linkshell's DKP table, computed straight from the app's own data (DKP is
// DB-authoritative). One row per member with the canonical columns
//   Member Name | Alt 1 | Alt 2 | Current DKP | Biddable DKP | Total DKP | Total DKP Spent
// plus the column totals shown as summary cards. This is the single source the
// always-on DKP sheet (web + Activity) renders and the Excel export writes — no
// Google connection involved. Lifetime totals come from the DkpLedgerEntry ledger
// plus each member's seed (so a migrated linkshell keeps its history). Biddable =
// Current minus DKP locked in winning bids on active auctions (export/display only).
public sealed class DkpSheetService
{
    // Reconciliation rows (importing a balance, seeding) are NOT real earns or
    // spends, so they never count toward the lifetime Total / Total Spent.
    private static readonly HashSet<string> ReconciliationEntryTypes =
        new(StringComparer.OrdinalIgnoreCase) { "TemplateImport", "SheetImport" };

    private readonly ApplicationDbContext _db;
    private readonly DkpPoolResolver _dkpPools;

    public DkpSheetService(ApplicationDbContext db, DkpPoolResolver dkpPools)
    {
        _db = db;
        _dkpPools = dkpPools;
    }

    public async Task<DkpSheetData> BuildAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var linkshell = await _db.Linkshells.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken)
            ?? throw new InvalidOperationException("Linkshell not found.");
        var step = DkpRounding.StepFor(linkshell.DkpRoundingIncrement);

        // No DB ORDER BY: rows are sorted below by the RESOLVED display name (the shown
        // name falls back past the nullable CharacterName), so DB order and shown order
        // match and the sort is case-insensitive.
        var members = await _db.AppUserLinkshells
            .AsNoTracking()
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        var ledgerByUser = await LoadLedgerByUserAsync(linkshellId, cancellationToken);
        // AppUserId -> BIDDABLE DKP (the single source of truth used by the roster and
        // live-event UIs): Current − DKP locked in bids being won − DKP spent on loot in a
        // still-live event (not yet committed to the ledger). Use this canonical formula so
        // the sheet, the .xlsx, and the Discord post never disagree with the bidding screen.
        var biddableByUser = await AuctionDkpService.ComputeBiddableDkpByUserAsync(_db, linkshellId, cancellationToken);

        // The per-pool split. Costs NOTHING extra: the ledger rows above are already all in memory,
        // so the derivation (DkpPoolBalanceService.Project) just runs over them again. Pool columns
        // only render when the linkshell actually has more than one pool.
        var poolMap = await _dkpPools.GetMapAsync(linkshellId, cancellationToken);
        var poolColumns = poolMap.HasMultiplePools
            ? poolMap.Pools.Select(pool => new DkpSheetPoolColumn(pool.Id, pool.Name, pool.Accent ?? DkpPoolAccents.Default)).ToList()
            : new List<DkpSheetPoolColumn>();
        var poolTotals = new double[poolColumns.Count];

        var rows = new List<DkpSheetMemberRow>(members.Count);
        double sumBiddable = 0, sumEarned = 0, sumSpent = 0;
        foreach (var m in members)
        {
            var name = m.CharacterName ?? m.AppUser?.CharacterName ?? m.AppUser?.UserName ?? "Unknown";
            var current = DkpRounding.Round(m.LinkshellDkp ?? 0, step);
            var biddableRaw = m.AppUserId is not null
                ? biddableByUser.GetValueOrDefault(m.AppUserId, m.LinkshellDkp ?? 0)
                : (m.LinkshellDkp ?? 0);
            var biddable = DkpRounding.Round(biddableRaw, step);
            var (earned, spent) = ComputeTotals(m, ledgerByUser, step);
            sumBiddable += biddable;
            sumEarned += earned;
            sumSpent += spent;

            var poolCurrent = new double[poolColumns.Count];
            if (poolColumns.Count > 0)
            {
                var memberRows = (m.AppUserId is not null
                        ? ledgerByUser.GetValueOrDefault(m.AppUserId)
                        : null)
                    ?? new List<LedgerLite>();
                var byPool = DkpPoolBalanceService.Project(
                    m.LinkshellDkp ?? 0,
                    memberRows.Select(e => new LedgerPoolRow(e.Id, e.DkpPoolId, e.Amount)),
                    m.DkpPoolLedgerFromId,
                    poolMap.DefaultPoolId,
                    poolColumns.Select(c => c.PoolId).ToList());

                for (var i = 0; i < poolColumns.Count; i++)
                {
                    poolCurrent[i] = DkpRounding.Round(byPool.GetValueOrDefault(poolColumns[i].PoolId), step);
                    poolTotals[i] += poolCurrent[i];
                }
            }

            rows.Add(new DkpSheetMemberRow(
                m.Id,
                name,
                m.AppUser?.AltCharacterName1 ?? string.Empty,
                m.AppUser?.AltCharacterName2 ?? string.Empty,
                current, biddable, earned, spent,
                poolCurrent));
        }

        // Sort by the resolved display name (case-insensitive) so the web table, the
        // .xlsx export and the Discord board share one stable, alphabetical order.
        rows = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        return new DkpSheetData(
            linkshellId,
            string.IsNullOrWhiteSpace(linkshell.LinkshellName) ? "Linkshell" : linkshell.LinkshellName!,
            rows,
            members.Count,
            DkpRounding.Round(sumEarned, step),
            DkpRounding.Round(sumBiddable, step),
            DkpRounding.Round(sumSpent, step),
            poolColumns,
            poolTotals.Select(total => DkpRounding.Round(total, step)).ToArray());
    }

    // ---- ledger helpers (lifetime totals = seed + post-watermark ledger) -----

    private sealed record LedgerLite(int Id, double Amount, string EntryType, int? DkpPoolId);

    private async Task<Dictionary<string, List<LedgerLite>>> LoadLedgerByUserAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var rows = await _db.DkpLedgerEntries
            .AsNoTracking()
            .Where(e => e.LinkshellId == linkshellId && e.AppUserId != null)
            .Select(e => new { e.AppUserId, e.Id, e.Amount, e.EntryType, e.DkpPoolId })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(e => e.AppUserId!)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => new LedgerLite(e.Id, e.Amount, e.EntryType ?? string.Empty, e.DkpPoolId)).ToList(),
                StringComparer.Ordinal);
    }

    private static (double Earned, double Spent) ComputeTotals(
        AppUserLinkshell member, Dictionary<string, List<LedgerLite>> ledgerByUser, double step)
    {
        var earned = member.SeededDkpEarned;
        var spent = member.SeededDkpSpent;

        if (member.AppUserId is not null && ledgerByUser.TryGetValue(member.AppUserId, out var entries))
        {
            foreach (var e in entries)
            {
                if (e.Id <= member.DkpSeedLedgerId) { continue; }
                if (ReconciliationEntryTypes.Contains(e.EntryType)) { continue; }
                if (e.Amount > 0) { earned += e.Amount; }
                else if (e.Amount < 0) { spent += -e.Amount; }
            }
        }

        return (DkpRounding.Round(earned, step), DkpRounding.Round(spent, step));
    }
}

// One member row for the DKP sheet / export. Id is the AppUserLinkshell.Id — a
// stable unique key for client-side row tracking (member names can collide or be
// "Unknown"), carried through to the Activity DTO.
//
// PoolCurrent is a parallel array aligned to DkpSheetData.Pools (not a dictionary), so every
// renderer can walk columns and cells in the same order without a lookup. Empty when the linkshell
// has a single pool — in which case the sheet renders exactly as it did before pools existed.
public sealed record DkpSheetMemberRow(
    int Id, string Name, string Alt1, string Alt2,
    double Current, double Biddable, double Total, double Spent,
    IReadOnlyList<double>? PoolCurrent = null)
{
    public IReadOnlyList<double> PoolCurrent { get; init; } = PoolCurrent ?? Array.Empty<double>();
}

// One per-pool column on the sheet.
public sealed record DkpSheetPoolColumn(int PoolId, string Name, string Accent);

// The whole DKP sheet: rows + the four summary-card totals + (optionally) the per-pool split.
public sealed record DkpSheetData(
    int LinkshellId,
    string LinkshellName,
    IReadOnlyList<DkpSheetMemberRow> Members,
    int TotalMembers,
    double TotalDkp,
    double Biddable,
    double TotalSpent,
    // Empty unless the linkshell has more than one pool. Every renderer keys off Pools.Count, so a
    // single-pool linkshell gets no new columns, tiles or cells anywhere.
    IReadOnlyList<DkpSheetPoolColumn>? Pools = null,
    IReadOnlyList<double>? PoolTotals = null)
{
    public IReadOnlyList<DkpSheetPoolColumn> Pools { get; init; } = Pools ?? Array.Empty<DkpSheetPoolColumn>();
    public IReadOnlyList<double> PoolTotals { get; init; } = PoolTotals ?? Array.Empty<double>();
}
