using System.Globalization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Imports a linkshell's existing DKP from an uploaded Excel/CSV (the inverse of
// the .xlsx the DkpSheet export produces). The whole linkshell comes in at once:
// rows that match an existing member UPDATE that member's balance + lifetime
// seed; rows with no match CREATE an unsynced PLACEHOLDER member (via
// ManualMemberService) that carries the imported DKP until the real person first
// launches the app and CLAIMS it (PlaceholderClaimService). Lifetime totals are
// stored as a per-member seed (SeededDkpEarned/Spent) plus a watermark
// (DkpSeedLedgerId) so a migrating linkshell keeps its history without recording
// every past transaction — identical to the model DkpSheetService reads back.
//
// Header-aware parse: a reformatted sheet with a different column order still
// imports as long as the labels are recognizable. No Google, no Excel library —
// XlsxImportReader hand-parses the file into a 2D grid.
public sealed class DkpImportService
{
    // Reconciliation rows (importing a balance) are NOT real earns or spends, so
    // they never count toward lifetime Total / Total Spent. Mirrors DkpSheetService.
    private const string ImportEntryType = "SheetImport";
    private static readonly HashSet<string> ReconciliationEntryTypes =
        new(StringComparer.OrdinalIgnoreCase) { "TemplateImport", "SheetImport" };

    private readonly ApplicationDbContext _db;
    private readonly ManualMemberService _members;
    private readonly DkpPoolResolver _dkpPools;

    public DkpImportService(ApplicationDbContext db, ManualMemberService members, DkpPoolResolver dkpPools)
    {
        _db = db;
        _members = members;
        _dkpPools = dkpPools;
    }

    // ---- parse --------------------------------------------------------------

    // Turn a raw uploaded grid (from XlsxImportReader) into parsed rows.
    public List<DkpImportRow> ParseImport(IList<IList<object>> raw)
    {
        var rows = new List<DkpImportRow>();
        if (raw.Count == 0) { return rows; }

        int headerIndex = -1, nameCol = -1, alt1Col = -1, alt2Col = -1, currentCol = -1, totalCol = -1, spentCol = -1;
        for (var r = 0; r < raw.Count && headerIndex < 0; r++)
        {
            int n = -1, a1 = -1, a2 = -1, cur = -1, tot = -1, sp = -1;
            for (var c = 0; c < raw[r].Count; c++)
            {
                var text = Normalize(raw[r][c]);
                if (text.Length == 0) { continue; }
                // Order matters: "Total DKP Spent" contains both "spent" and
                // "total" — check spent first so it maps to the spent column.
                if (text.Contains("spent")) { sp = c; }
                else if (text.Contains("total")) { tot = c; }
                else if (text.Contains("current")) { cur = c; }
                // "Biddable DKP" matches none of these branches and is ignored — it's
                // derived (current minus locked bids), not a meaningful import input.
                else if (text.Contains("alt"))
                {
                    if (text.Contains('2') || text.Contains("two")) { a2 = c; }
                    else { a1 = c; }
                }
                else if (text.Contains("member") || text.Contains("character") || (text.Contains("name") && !text.Contains("alt"))) { n = c; }
            }
            if (n >= 0 && cur >= 0)
            {
                headerIndex = r; nameCol = n; alt1Col = a1; alt2Col = a2; currentCol = cur; totalCol = tot; spentCol = sp;
            }
        }

        if (headerIndex < 0)
        {
            // No recognizable header — assume the canonical export layout:
            // A=Name B=Alt1 C=Alt2 D=Current E=Biddable F=Total G=Total Spent.
            headerIndex = 0; nameCol = 0; alt1Col = 1; alt2Col = 2; currentCol = 3; totalCol = 5; spentCol = 6;
        }

        for (var r = headerIndex + 1; r < raw.Count; r++)
        {
            var name = CellText(raw[r], nameCol);
            if (string.IsNullOrWhiteSpace(name)) { continue; }
            if (string.Equals(name.Trim(), "TOTAL", StringComparison.OrdinalIgnoreCase)) { continue; }
            // The sample template leaves "✦" placeholder rows to fill in — skip
            // any the user didn't replace so they don't import as bogus members.
            if (name.Trim() == "✦") { continue; }

            rows.Add(new DkpImportRow(
                name.Trim(),
                NullIfBlank(CellText(raw[r], alt1Col)),
                NullIfBlank(CellText(raw[r], alt2Col)),
                TryNumber(raw[r], currentCol),
                TryNumber(raw[r], totalCol),
                TryNumber(raw[r], spentCol)));
        }
        return rows;
    }

    // ---- preview ------------------------------------------------------------

    public async Task<DkpImportPreview> BuildPreviewAsync(
        int linkshellId, IReadOnlyList<DkpImportRow> parsed, string sourceLabel, CancellationToken cancellationToken)
    {
        var linkshell = await _db.Linkshells.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken)
            ?? throw new InvalidOperationException("Linkshell not found.");
        var step = DkpRounding.StepFor(linkshell.DkpRoundingIncrement);

        var members = await _db.AppUserLinkshells.AsNoTracking()
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        var index = BuildNameIndex(members);
        var withActivity = await LoadPostSeedActivityAsync(linkshellId, members, cancellationToken);

        var rows = new List<DkpImportPreviewRow>(parsed.Count);
        foreach (var row in parsed)
        {
            var (kind, member) = Classify(row, index);
            switch (kind)
            {
                case DkpImportRowKind.Create:
                    rows.Add(new DkpImportPreviewRow(
                        row.Name, row.Alt1, row.Alt2, DkpImportRowKind.Create, null, null,
                        0, row.Current.HasValue ? DkpRounding.Round(row.Current.Value, step) : 0,
                        DkpRounding.Round(row.Total ?? 0, step), DkpRounding.Round(row.Spent ?? 0, step),
                        "New member — will be created and can be claimed on first launch."));
                    break;

                case DkpImportRowKind.Update when member is not null:
                    var oldCurrent = DkpRounding.Round(member.LinkshellDkp ?? 0, step);
                    var note = (member.AppUserId is not null && withActivity.Contains(member.AppUserId))
                        ? "Has earned/spent DKP in-app since the last import — reimport resets lifetime totals to the sheet values."
                        : null;
                    rows.Add(new DkpImportPreviewRow(
                        row.Name, row.Alt1, row.Alt2, DkpImportRowKind.Update, member.Id, ResolveName(member),
                        oldCurrent, row.Current.HasValue ? DkpRounding.Round(row.Current.Value, step) : oldCurrent,
                        DkpRounding.Round(row.Total ?? 0, step), DkpRounding.Round(row.Spent ?? 0, step),
                        note));
                    break;

                default: // Ambiguous
                    rows.Add(new DkpImportPreviewRow(
                        row.Name, row.Alt1, row.Alt2, DkpImportRowKind.Ambiguous, null, null,
                        0, DkpRounding.Round(row.Current ?? 0, step),
                        DkpRounding.Round(row.Total ?? 0, step), DkpRounding.Round(row.Spent ?? 0, step),
                        "Matches more than one member — skipped. Rename in the sheet to disambiguate."));
                    break;
            }
        }

        return new DkpImportPreview(
            sourceLabel,
            rows.Count(r => r.Kind == DkpImportRowKind.Create),
            rows.Count(r => r.Kind == DkpImportRowKind.Update),
            rows.Count(r => r.Kind == DkpImportRowKind.Ambiguous),
            rows);
    }

    // ---- commit -------------------------------------------------------------

    public async Task<DkpImportResult> CommitAsync(
        int linkshellId, IReadOnlyList<DkpImportRow> parsed, string sourceLabel, CancellationToken cancellationToken)
    {
        var linkshell = await _db.Linkshells.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken)
            ?? throw new InvalidOperationException("Linkshell not found.");
        var step = DkpRounding.StepFor(linkshell.DkpRoundingIncrement);
        var now = DateTime.UtcNow;

        // Tracked load — these get mutated in place.
        var members = await _db.AppUserLinkshells
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);
        var index = BuildNameIndex(members);

        // Watermark: each member's max ledger Id BEFORE this import. Pre-import
        // activity (Id <= watermark) is represented by the seed; later real
        // activity (Id > watermark, non-reconciliation) adds on top.
        var maxIdByUser = await _db.DkpLedgerEntries
            .Where(e => e.LinkshellId == linkshellId && e.AppUserId != null)
            .GroupBy(e => e.AppUserId!)
            .Select(g => new { AppUserId = g.Key, MaxId = g.Max(e => e.Id) })
            .ToDictionaryAsync(x => x.AppUserId, x => x.MaxId, cancellationToken);

        var defaultPoolId = (await _dkpPools.GetMapAsync(linkshellId, cancellationToken)).DefaultPoolId;

        var created = new List<string>();
        var skipped = new List<string>();
        int createdCount = 0, updatedCount = 0, ambiguousCount = 0;

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var row in parsed)
        {
            var (kind, member) = Classify(row, index);

            if (kind == DkpImportRowKind.Ambiguous)
            {
                ambiguousCount++;
                skipped.Add(row.Name);
                continue;
            }

            if (kind == DkpImportRowKind.Update && member is not null)
            {
                var watermark = member.AppUserId is not null ? maxIdByUser.GetValueOrDefault(member.AppUserId, 0) : 0;
                ApplySeed(member, row, step, watermark, now, defaultPoolId);
                updatedCount++;
                continue;
            }

            // Create: make/adopt a placeholder, then seed it (watermark 0 — no
            // prior ledger). FindOrCreate shares our DbContext, so its SaveChanges
            // runs inside this transaction.
            var result = await _members.FindOrCreateByNameAsync(linkshellId, row.Name, row.Alt1, row.Alt2, cancellationToken);
            if (!result.Success || result.AppUserId is null)
            {
                skipped.Add(row.Name);
                continue;
            }

            var newMember = await _db.AppUserLinkshells
                .FirstOrDefaultAsync(m => m.LinkshellId == linkshellId && m.AppUserId == result.AppUserId, cancellationToken);
            if (newMember is null) { skipped.Add(row.Name); continue; }

            ApplySeed(newMember, row, step, maxIdByUser.GetValueOrDefault(result.AppUserId, 0), now, defaultPoolId);
            created.Add(row.Name);
            createdCount++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new DkpImportResult(sourceLabel, createdCount, updatedCount, ambiguousCount, skipped.Count, created, skipped);
    }

    // ---- shared classification + seed ---------------------------------------

    private void ApplySeed(AppUserLinkshell member, DkpImportRow row, double step, int watermark, DateTime now, int defaultPoolId)
    {
        if (row.Current.HasValue)
        {
            var newCurrent = DkpRounding.Round(row.Current.Value, step);
            var oldCurrent = member.LinkshellDkp ?? 0;
            if (Math.Abs(oldCurrent - newCurrent) >= 0.0001)
            {
                member.LinkshellDkp = newCurrent;
                // Reconciliation entry (excluded from lifetime totals) for the audit trail. It
                // carries the balance DELTA, not the new total, so the ledger and LinkshellDkp stay
                // in lockstep (INV-1) and the member's existing per-pool split survives the import.
                //
                // PINNED to the default pool: imported DKP has no event-type provenance, and a
                // later remap must not go looking for one.
                _db.DkpLedgerEntries.Add(new DkpLedgerEntry
                {
                    AppUserId = member.AppUserId,
                    LinkshellId = member.LinkshellId,
                    EntryType = ImportEntryType,
                    EventName = "DKP Import",
                    EventType = "Sheet",
                    Amount = newCurrent - oldCurrent,
                    Sequence = 1,
                    OccurredAt = now,
                    CharacterName = member.CharacterName,
                    DkpPoolId = defaultPoolId,
                    DkpPoolPinned = true,
                    Details = $"Current DKP set from import ({oldCurrent} -> {newCurrent}).",
                });
            }
        }

        if (row.Total.HasValue) { member.SeededDkpEarned = DkpRounding.Round(row.Total.Value, step); }
        if (row.Spent.HasValue) { member.SeededDkpSpent = DkpRounding.Round(row.Spent.Value, step); }
        member.DkpSeedLedgerId = watermark;
    }

    // 0 matches -> Create, exactly 1 -> Update, >1 (name collision) -> Ambiguous.
    private static (DkpImportRowKind Kind, AppUserLinkshell? Member) Classify(
        DkpImportRow row, IReadOnlyDictionary<string, List<AppUserLinkshell>> index)
    {
        if (!index.TryGetValue(row.Name.Trim(), out var matches) || matches.Count == 0)
        {
            return (DkpImportRowKind.Create, null);
        }
        return matches.Count == 1
            ? (DkpImportRowKind.Update, matches[0])
            : (DkpImportRowKind.Ambiguous, null);
    }

    // name (case-insensitive) -> distinct memberships that go by it (main, the
    // resolved AppUser name, or either alt). Count-aware so collisions surface
    // instead of being silently mis-attributed to the first match.
    private static Dictionary<string, List<AppUserLinkshell>> BuildNameIndex(IEnumerable<AppUserLinkshell> members)
    {
        var index = new Dictionary<string, List<AppUserLinkshell>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? name, AppUserLinkshell m)
        {
            if (string.IsNullOrWhiteSpace(name)) { return; }
            var key = name.Trim();
            if (key.Length == 0) { return; }
            if (!index.TryGetValue(key, out var list)) { list = new List<AppUserLinkshell>(); index[key] = list; }
            if (!list.Contains(m)) { list.Add(m); }
        }
        foreach (var m in members)
        {
            Add(m.CharacterName, m);
            Add(m.AppUser?.CharacterName, m);
            Add(m.AppUser?.AltCharacterName1, m);
            Add(m.AppUser?.AltCharacterName2, m);
        }
        return index;
    }

    // AppUserIds that have non-reconciliation ledger activity AFTER their seed
    // watermark — i.e. real in-app earns/spends that a reimport would discard.
    private async Task<HashSet<string>> LoadPostSeedActivityAsync(
        int linkshellId, List<AppUserLinkshell> members, CancellationToken cancellationToken)
    {
        var seedByUser = members
            .Where(m => m.AppUserId is not null)
            .GroupBy(m => m.AppUserId!)
            .ToDictionary(g => g.Key, g => g.Min(m => m.DkpSeedLedgerId), StringComparer.Ordinal);

        var entries = await _db.DkpLedgerEntries.AsNoTracking()
            .Where(e => e.LinkshellId == linkshellId && e.AppUserId != null)
            .Select(e => new { e.AppUserId, e.Id, e.EntryType, e.Amount })
            .ToListAsync(cancellationToken);

        var withActivity = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.AppUserId is null || !seedByUser.TryGetValue(e.AppUserId, out var seed)) { continue; }
            if (e.Id <= seed) { continue; }
            if (ReconciliationEntryTypes.Contains(e.EntryType ?? string.Empty)) { continue; }
            if (Math.Abs(e.Amount) >= 0.0001) { withActivity.Add(e.AppUserId); }
        }
        return withActivity;
    }

    private static string ResolveName(AppUserLinkshell m)
        => m.CharacterName ?? m.AppUser?.CharacterName ?? m.AppUser?.UserName ?? "Unknown";

    private static string Normalize(object? cell) => (cell?.ToString() ?? string.Empty).Trim().ToLowerInvariant();

    private static string CellText(IList<object> row, int col)
        => col >= 0 && col < row.Count ? (row[col]?.ToString() ?? string.Empty) : string.Empty;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double? TryNumber(IList<object> row, int col)
    {
        if (col < 0 || col >= row.Count) { return null; }
        var text = row[col]?.ToString();
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            || double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out v))
        {
            return v;
        }
        return null;
    }
}

// One parsed row from an uploaded import file (after header-aware mapping).
public sealed record DkpImportRow(string Name, string? Alt1, string? Alt2, double? Current, double? Total, double? Spent);

public enum DkpImportRowKind { Update, Create, Ambiguous, Skipped }

// One classified preview row shown before commit.
public sealed record DkpImportPreviewRow(
    string Name, string? Alt1, string? Alt2,
    DkpImportRowKind Kind,
    int? MatchedMembershipId,
    string? MatchedExistingName,
    double OldCurrent, double NewCurrent,
    double NewTotal, double NewSpent,
    string? Note);

public sealed record DkpImportPreview(
    string SourceLabel,
    int CreateCount,
    int UpdateCount,
    int AmbiguousCount,
    IReadOnlyList<DkpImportPreviewRow> Rows);

public sealed record DkpImportResult(
    string SourceLabel,
    int Created,
    int Updated,
    int Ambiguous,
    int Skipped,
    IReadOnlyList<string> CreatedNames,
    IReadOnlyList<string> SkippedNames);
