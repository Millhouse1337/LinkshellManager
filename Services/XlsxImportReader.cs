using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
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
    // Guardrails against a pathological upload — the template is a handful of
    // columns and at most a few hundred members. A .xlsx is a ZIP, so a tiny
    // crafted file can claim a cell in column 16,000 (and we'd pad every row
    // out to it) or unpack to gigabytes of XML; either would run the one app
    // process out of memory and take the site down for every linkshell.
    internal const int MaxRows = 5000;

    // Cells past this column are ignored (the template uses about ten).
    internal const int MaxColumns = 64;

    // A ZIP part may unpack to at most this many bytes. A real sheet for a few
    // hundred members is well under 1 MB.
    internal const long MaxEntryBytes = 4 * 1024 * 1024;

    // A real .xlsx has around ten parts; hundreds means it isn't one.
    private const int MaxZipEntries = 500;

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
        if (zip.Entries.Count > MaxZipEntries)
        {
            throw new InvalidOperationException("The .xlsx file has too many parts to be a DKP sheet.");
        }

        var shared = ReadSharedStrings(zip);
        var sheetEntry = FindFirstSheet(zip)
            ?? throw new InvalidOperationException("The .xlsx file has no worksheet.");

        var doc = LoadEntry(sheetEntry);

        var rows = new List<IList<object>>();
        var sheetData = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "sheetData");
        if (sheetData is null) { return rows; }

        foreach (var rowEl in sheetData.Elements().Where(e => e.Name.LocalName == "row"))
        {
            var cells = new List<object>();
            foreach (var c in rowEl.Elements().Where(e => e.Name.LocalName == "c"))
            {
                // A row never grows past MaxColumns, however many <c> it has.
                if (cells.Count >= MaxColumns) { break; }

                // Honor the cell reference (e.g. "C5") so gaps map to the right
                // columns; fall back to append order when a ref is missing.
                var refAttr = (string?)c.Attribute("r");
                var targetCol = refAttr is not null ? ColumnIndex(refAttr) : cells.Count;
                // A stray note far off to the right is skipped, not padded out to.
                if (targetCol >= MaxColumns) { continue; }
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

        var doc = LoadEntry(entry);
        foreach (var si in doc.Root?.Elements().Where(e => e.Name.LocalName == "si")
                           ?? Enumerable.Empty<XElement>())
        {
            list.Add(InlineText(si));
        }
        return list;
    }

    // Parse one ZIP part as XML, refusing anything that unpacks past MaxEntryBytes.
    // The declared size (Length) is checked first, but a crafted file can lie
    // about it, so the read itself is capped as well.
    private static XDocument LoadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes)
        {
            throw new InvalidOperationException("The .xlsx file is too large to be a DKP sheet.");
        }

        using var s = new CappedReadStream(entry.Open(), MaxEntryBytes);
        // Real .xlsx parts never carry a DTD; refusing one stops entity-expansion
        // tricks from growing a small part into a huge document.
        using var reader = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
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

    // "C5" / "AB12" -> 0-based column index (2 / 27). Stops counting once past
    // MaxColumns, so a reference like "ZZZZZZZZ1" can't overflow the int.
    internal static int ColumnIndex(string cellRef)
    {
        var col = 0;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') { col = col * 26 + (ch - 'A' + 1); }
            else if (ch is >= 'a' and <= 'z') { col = col * 26 + (ch - 'a' + 1); }
            else { break; }
            if (col > MaxColumns) { return MaxColumns; }
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
                case ',':
                    // Same column cap as .xlsx: extra fields are dropped, so a
                    // line of a million commas can't build a million-cell row.
                    if (row.Count < MaxColumns) { row.Add(field.ToString()); }
                    field.Clear();
                    break;
                case '\r': break;
                case '\n':
                    if (row.Count < MaxColumns) { row.Add(field.ToString()); }
                    field.Clear();
                    rows.Add(row); row = new List<object>();
                    if (rows.Count >= MaxRows) { return rows; }
                    break;
                default: field.Append(ch); break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            if (row.Count < MaxColumns) { row.Add(field.ToString()); }
            rows.Add(row);
        }
        return rows;
    }

    // Read-only wrapper that throws once more than `limit` bytes have been read,
    // so a ZIP part that lies about its size still can't unpack without bound.
    private sealed class CappedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;

        public CappedReadStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            _read += n;
            if (_read > _limit)
            {
                throw new InvalidOperationException("The .xlsx file is too large to be a DKP sheet.");
            }
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
