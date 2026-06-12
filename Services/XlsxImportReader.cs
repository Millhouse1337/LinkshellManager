using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace LinkshellManagerDiscordApp.Services;

// Minimal reader for an uploaded DKP import file — the inverse of
// SampleDkpTemplateWorkbook. Reads an .xlsx (Office Open XML: a ZIP of XML
// parts) or a .csv into a simple 2D grid (rows of cell values) that
// DkpTemplateSheetService then interprets header-aware. Hand-rolled to avoid
// taking an Excel dependency for one upload path; only cell text/number values
// are needed (no styles/formulas), so the parse stays tiny and forgiving.
public static class XlsxImportReader
{
    // Guardrail against a pathological upload — the template is a handful of
    // columns and at most a few hundred members.
    private const int MaxRows = 5000;

    public static List<IList<object>> Read(Stream stream, string? fileName)
    {
        // ZipArchive needs a seekable stream; IFormFile's may not be. Buffer once.
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;

        var name = fileName?.Trim() ?? string.Empty;
        var looksCsv = name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        if (!looksCsv)
        {
            try { return ReadXlsx(buffer); }
            catch (InvalidDataException) { /* not a real ZIP — fall back to CSV */ }
        }
        buffer.Position = 0;
        return ReadCsv(buffer);
    }

    // ---- .xlsx ----------------------------------------------------------------

    private static List<IList<object>> ReadXlsx(MemoryStream buffer)
    {
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);

        var shared = ReadSharedStrings(zip);
        var sheetEntry = FindFirstSheet(zip)
            ?? throw new InvalidOperationException("The .xlsx file has no worksheet.");

        XDocument doc;
        using (var s = sheetEntry.Open()) { doc = XDocument.Load(s); }

        var rows = new List<IList<object>>();
        var sheetData = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "sheetData");
        if (sheetData is null) { return rows; }

        foreach (var rowEl in sheetData.Elements().Where(e => e.Name.LocalName == "row"))
        {
            var cells = new List<object>();
            foreach (var c in rowEl.Elements().Where(e => e.Name.LocalName == "c"))
            {
                // Honor the cell reference (e.g. "C5") so gaps map to the right
                // columns; fall back to append order when a ref is missing.
                var refAttr = (string?)c.Attribute("r");
                var targetCol = refAttr is not null ? ColumnIndex(refAttr) : cells.Count;
                while (cells.Count < targetCol) { cells.Add(string.Empty); }
                cells.Add(CellValue(c, shared));
            }
            rows.Add(cells);
            if (rows.Count >= MaxRows) { break; }
        }
        return rows;
    }

    private static object CellValue(XElement c, IReadOnlyList<string> shared)
    {
        var type = (string?)c.Attribute("t");
        if (type == "s")
        {
            var idxText = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
            return int.TryParse(idxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)
                   && idx >= 0 && idx < shared.Count
                ? shared[idx]
                : string.Empty;
        }
        if (type == "inlineStr")
        {
            var inline = c.Elements().FirstOrDefault(e => e.Name.LocalName == "is");
            return inline is null ? string.Empty : InlineText(inline);
        }
        // "str" (formula result), "b" (bool), or a number/date — the raw <v> text
        // is what the header-aware parser wants (numbers are invariant-formatted).
        return c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Empty;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, "xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase));
        if (entry is null) { return list; }

        XDocument doc;
        using (var s = entry.Open()) { doc = XDocument.Load(s); }
        foreach (var si in doc.Root?.Elements().Where(e => e.Name.LocalName == "si")
                           ?? Enumerable.Empty<XElement>())
        {
            list.Add(InlineText(si));
        }
        return list;
    }

    private static ZipArchiveEntry? FindFirstSheet(ZipArchive zip)
    {
        var sheets = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return sheets.FirstOrDefault(e => e.FullName.EndsWith("/sheet1.xml", StringComparison.OrdinalIgnoreCase))
               ?? sheets.OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    // Concatenate every <t> descendant (handles rich-text runs in shared/inline strings).
    private static string InlineText(XElement el) =>
        string.Concat(el.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));

    // "C5" / "AB12" -> 0-based column index (2 / 27).
    private static int ColumnIndex(string cellRef)
    {
        var col = 0;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') { col = col * 26 + (ch - 'A' + 1); }
            else if (ch is >= 'a' and <= 'z') { col = col * 26 + (ch - 'a' + 1); }
            else { break; }
        }
        return col > 0 ? col - 1 : 0;
    }

    // ---- .csv -----------------------------------------------------------------

    private static List<IList<object>> ReadCsv(MemoryStream buffer)
    {
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return ParseCsv(reader.ReadToEnd());
    }

    // RFC-4180-ish: handles quoted fields, escaped "" quotes, and quoted newlines.
    private static List<IList<object>> ParseCsv(string text)
    {
        var rows = new List<IList<object>>();
        var row = new List<object>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else { inQuotes = false; }
                }
                else { field.Append(ch); }
                continue;
            }

            switch (ch)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = new List<object>();
                    if (rows.Count >= MaxRows) { return rows; }
                    break;
                default: field.Append(ch); break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
