using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace LinkshellManagerDiscordApp.Services;

// Builds a self-contained, dark-themed .xlsx (Office Open XML) showing the
// canonical "LSM DKP" template — a branded title, four summary cards, and the
// color-coded member table — so a linkshell sees exactly how to format a tab
// they want to IMPORT. Hand-rolled (an .xlsx is just a ZIP of a few XML parts)
// to avoid pulling in an Excel library for one static download. The visual
// theme lives in a real styles.xml (fonts/fills/borders/number formats); the
// importable 7-column layout (Member Name | Alt 1 | Alt 2 | Current DKP |
// Biddable DKP | Total DKP | Total DKP Spent) is preserved so the file still
// works as a format reference. Excel can't render the design mockup's rounded
// cards, glows, gradients, or crest/icons, so this is the closest faithful
// approximation in a plain spreadsheet.
public static class SampleDkpTemplateWorkbook
{
    public const string FileName = "LSM-DKP-Template-Sample.xlsx";
    public const string ContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // The 7 import columns map to spreadsheet columns A..G.
    private static readonly string[] Columns = { "A", "B", "C", "D", "E", "F", "G" };

    // Sample members. Strings render as text; doubles as numeric cells (fractional
    // values show DKP can be quarter/half). Card totals below are summed from
    // these so the sample is internally consistent.
    private sealed record SampleMember(
        string Name, string Alt1, string Alt2,
        double Current, double Biddable, double Total, double Spent);

    private static readonly SampleMember[] Members =
    {
        new("Member1", "Member1Alt1", "Member1Alt2", 1250.50, 1000.50, 5400.00, 4149.50),
        new("Member2", "Member2Alt1", "Member2Alt2",  875.00,  875.00, 3200.00, 2325.00),
        new("Member3", "Member3Alt1", "Member3Alt2", 1500.25, 1500.25, 6000.00, 4499.75),
    };

    public static byte[] Build()
    {
        using var buffer = new MemoryStream();
        // leaveOpen so we can read the buffer after the archive is flushed/disposed.
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", ContentTypesXml);
            WriteEntry(archive, "_rels/.rels", RootRelsXml);
            WriteEntry(archive, "xl/workbook.xml", WorkbookXml);
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
            WriteEntry(archive, "xl/styles.xml", StylesXml);
            WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildSheetXml());
        }
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string xml)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        // No BOM — Excel parses the XML declaration's encoding.
        var bytes = new UTF8Encoding(false).GetBytes(xml);
        stream.Write(bytes, 0, bytes.Length);
    }

    private const string ContentTypesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "</Types>";

    private const string RootRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string WorkbookXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"LSM DKP\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        "</workbook>";

    private const string WorkbookRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    // Style indices into cellXfs below (kept as named constants so the sheet
    // builder reads clearly and an off-by-one is obvious).
    private const int SPage = 1;        // dark page background, blank
    private const int STitle = 2;       // big serif title
    private const int SSubtitle = 3;    // cyan subtitle
    private const int SCardLabel = 4;   // muted card label
    private const int SCardMembers = 5; // card value, white integer
    private const int SCardCyan = 6;    // card value, cyan number
    private const int SCardGold = 7;    // card value, gold number
    private const int SHeader = 8;      // table header
    private const int SName = 9;        // member name (bold white)
    private const int SAlt = 10;        // alt name (muted)
    private const int SValCyan = 11;    // Current / Biddable DKP
    private const int SValWhite = 12;   // Total DKP
    private const int SValGold = 13;    // Total DKP Spent
    private const int SEmptyA = 14;     // empty row, band A blank
    private const int SEmptyB = 15;     // empty row, band B blank
    private const int SSparkleA = 16;   // empty row, band A ✦
    private const int SSparkleB = 17;   // empty row, band B ✦
    private const int SFooter = 18;     // footer line

    private const string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"15\">" +
            "<font><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +              // 0 default
            "<font><b/><sz val=\"26\"/><color rgb=\"FFD7E2EC\"/><name val=\"Georgia\"/></font>" +                          // 1 title
            "<font><b/><sz val=\"11\"/><color rgb=\"FF5FC8E0\"/><name val=\"Calibri\"/></font>" +                          // 2 subtitle
            "<font><b/><sz val=\"9\"/><color rgb=\"FF8AA0B8\"/><name val=\"Calibri\"/></font>" +                           // 3 card label
            "<font><b/><sz val=\"18\"/><color rgb=\"FF5FC8E0\"/><name val=\"Calibri\"/></font>" +                          // 4 card cyan
            "<font><b/><sz val=\"18\"/><color rgb=\"FFE0A23C\"/><name val=\"Calibri\"/></font>" +                          // 5 card gold
            "<font><b/><sz val=\"18\"/><color rgb=\"FFE8EEF5\"/><name val=\"Calibri\"/></font>" +                          // 6 card white
            "<font><b/><sz val=\"11\"/><color rgb=\"FFB6CBE0\"/><name val=\"Calibri\"/></font>" +                          // 7 header
            "<font><b/><sz val=\"11\"/><color rgb=\"FFE8EEF5\"/><name val=\"Calibri\"/></font>" +                          // 8 name
            "<font><sz val=\"11\"/><color rgb=\"FF9FB2C6\"/><name val=\"Calibri\"/></font>" +                              // 9 alt
            "<font><sz val=\"11\"/><color rgb=\"FF5FC8E0\"/><name val=\"Calibri\"/></font>" +                              // 10 value cyan
            "<font><b/><sz val=\"11\"/><color rgb=\"FFE8EEF5\"/><name val=\"Calibri\"/></font>" +                          // 11 value white
            "<font><b/><sz val=\"11\"/><color rgb=\"FFE0A23C\"/><name val=\"Calibri\"/></font>" +                          // 12 value gold
            "<font><b/><sz val=\"9\"/><color rgb=\"FF6E8BA0\"/><name val=\"Calibri\"/></font>" +                           // 13 footer
            "<font><sz val=\"11\"/><color rgb=\"FF2E4257\"/><name val=\"Calibri\"/></font>" +                             // 14 sparkle
        "</fonts>" +
        "<fills count=\"7\">" +
            "<fill><patternFill patternType=\"none\"/></fill>" +                                                            // 0
            "<fill><patternFill patternType=\"gray125\"/></fill>" +                                                         // 1
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF0B0F1A\"/><bgColor indexed=\"64\"/></patternFill></fill>" + // 2 page
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF0F1826\"/><bgColor indexed=\"64\"/></patternFill></fill>" + // 3 card
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF12233A\"/><bgColor indexed=\"64\"/></patternFill></fill>" + // 4 header
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF0E1622\"/><bgColor indexed=\"64\"/></patternFill></fill>" + // 5 band A
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF0B121C\"/><bgColor indexed=\"64\"/></patternFill></fill>" + // 6 band B
        "</fills>" +
        "<borders count=\"3\">" +
            "<border><left/><right/><top/><bottom/><diagonal/></border>" +                                                  // 0 none
            "<border><left style=\"thin\"><color rgb=\"FF24405F\"/></left><right style=\"thin\"><color rgb=\"FF24405F\"/></right><top style=\"thin\"><color rgb=\"FF24405F\"/></top><bottom style=\"thin\"><color rgb=\"FF24405F\"/></bottom><diagonal/></border>" + // 1 card box
            "<border><left/><right/><top/><bottom style=\"medium\"><color rgb=\"FF2A6E86\"/></bottom><diagonal/></border>" + // 2 header underline
        "</borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"19\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +                                                                                                                                          // 0 default
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFill=\"1\"/>" +                                                                                                                          // 1 page
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +                            // 2 title
            "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +                            // 3 subtitle
            "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" + // 4 card label
            "<xf numFmtId=\"3\" fontId=\"6\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" + // 5 card members
            "<xf numFmtId=\"4\" fontId=\"4\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" + // 6 card cyan
            "<xf numFmtId=\"4\" fontId=\"5\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" + // 7 card gold
            "<xf numFmtId=\"0\" fontId=\"7\" fillId=\"4\" borderId=\"2\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" + // 8 header
            "<xf numFmtId=\"0\" fontId=\"8\" fillId=\"5\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" +                  // 9 name
            "<xf numFmtId=\"0\" fontId=\"9\" fillId=\"5\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" +                  // 10 alt
            "<xf numFmtId=\"4\" fontId=\"10\" fillId=\"5\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" + // 11 value cyan
            "<xf numFmtId=\"4\" fontId=\"11\" fillId=\"5\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" + // 12 value white
            "<xf numFmtId=\"4\" fontId=\"12\" fillId=\"5\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" + // 13 value gold
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"5\" borderId=\"0\" xfId=\"0\" applyFill=\"1\"/>" +                                                                                                                          // 14 empty A
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"6\" borderId=\"0\" xfId=\"0\" applyFill=\"1\"/>" +                                                                                                                          // 15 empty B
            "<xf numFmtId=\"0\" fontId=\"14\" fillId=\"5\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" +                 // 16 sparkle A
            "<xf numFmtId=\"0\" fontId=\"14\" fillId=\"6\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" +                 // 17 sparkle B
            "<xf numFmtId=\"0\" fontId=\"13\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +                            // 18 footer
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";

    private static string BuildSheetXml()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        // Hide gridlines so the dark theme reads as a designed panel.
        sb.Append("<sheetViews><sheetView showGridLines=\"0\" workbookViewId=\"0\"/></sheetViews>");
        sb.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");
        sb.Append("<cols>");
        sb.Append("<col min=\"1\" max=\"1\" width=\"30\" customWidth=\"1\"/>");
        sb.Append("<col min=\"2\" max=\"3\" width=\"20\" customWidth=\"1\"/>");
        sb.Append("<col min=\"4\" max=\"5\" width=\"17\" customWidth=\"1\"/>");
        sb.Append("<col min=\"6\" max=\"6\" width=\"15\" customWidth=\"1\"/>");
        sb.Append("<col min=\"7\" max=\"7\" width=\"20\" customWidth=\"1\"/>");
        sb.Append("</cols>");
        sb.Append("<sheetData>");

        // Row 1 — spacer
        Row(sb, 1, 8, () => FillRow(sb, 1, SPage));
        // Row 2 — title (merged A2:G2)
        Row(sb, 2, 40, () => { Text(sb, "A", 2, STitle, "LSM DKP TEMPLATE"); BlankRange(sb, 2, STitle, 1); });
        // Row 3 — subtitle (merged A3:G3)
        Row(sb, 3, 18, () => { Text(sb, "A", 3, SSubtitle, "✦   SAMPLE LINKSHELL DKP   ✦"); BlankRange(sb, 3, SSubtitle, 1); });
        // Row 4 — spacer
        Row(sb, 4, 8, () => FillRow(sb, 4, SPage));

        // Row 5 — summary card labels (cards: A | B:C | D:E | F:G)
        Row(sb, 5, 16, () =>
        {
            Text(sb, "A", 5, SCardLabel, "TOTAL MEMBERS");
            Text(sb, "B", 5, SCardLabel, "TOTAL DKP");   Blank(sb, "C", 5, SCardLabel);
            Text(sb, "D", 5, SCardLabel, "BIDDABLE DKP"); Blank(sb, "E", 5, SCardLabel);
            Text(sb, "F", 5, SCardLabel, "TOTAL DKP SPENT"); Blank(sb, "G", 5, SCardLabel);
        });
        // Row 6 — summary card values
        var totalDkp = 0.0; var biddable = 0.0; var spent = 0.0;
        foreach (var m in Members) { totalDkp += m.Total; biddable += m.Biddable; spent += m.Spent; }
        Row(sb, 6, 34, () =>
        {
            Num(sb, "A", 6, SCardMembers, Members.Length);
            Num(sb, "B", 6, SCardCyan, totalDkp);  Blank(sb, "C", 6, SCardCyan);
            Num(sb, "D", 6, SCardCyan, biddable);  Blank(sb, "E", 6, SCardCyan);
            Num(sb, "F", 6, SCardGold, spent);     Blank(sb, "G", 6, SCardGold);
        });
        // Row 7 — spacer
        Row(sb, 7, 10, () => FillRow(sb, 7, SPage));

        // Row 8 — table header (the row the importer matches on)
        Row(sb, 8, 26, () =>
        {
            Text(sb, "A", 8, SHeader, "Member Name");
            Text(sb, "B", 8, SHeader, "Alt 1");
            Text(sb, "C", 8, SHeader, "Alt 2");
            Text(sb, "D", 8, SHeader, "Current DKP");
            Text(sb, "E", 8, SHeader, "Biddable DKP");
            Text(sb, "F", 8, SHeader, "Total DKP");
            Text(sb, "G", 8, SHeader, "Total DKP Spent");
        });

        // Rows 9.. — member data
        var row = 9;
        foreach (var m in Members)
        {
            var r = row;
            Row(sb, r, 24, () =>
            {
                Text(sb, "A", r, SName, m.Name);
                Text(sb, "B", r, SAlt, m.Alt1);
                Text(sb, "C", r, SAlt, m.Alt2);
                Num(sb, "D", r, SValCyan, m.Current);
                Num(sb, "E", r, SValCyan, m.Biddable);
                Num(sb, "F", r, SValWhite, m.Total);
                Num(sb, "G", r, SValGold, m.Spent);
            });
            row++;
        }

        // Empty banded rows (decorative, like the mockup) — 5 rows.
        for (var i = 0; i < 5; i++)
        {
            var r = row;
            var bandA = i % 2 == 0;
            var sparkle = bandA ? SSparkleA : SSparkleB;
            var blank = bandA ? SEmptyA : SEmptyB;
            Row(sb, r, 22, () =>
            {
                Text(sb, "A", r, sparkle, "✦");
                for (var c = 1; c < Columns.Length; c++) { Blank(sb, Columns[c], r, blank); }
            });
            row++;
        }

        // Spacer + footer (merged across A:G)
        Row(sb, row, 10, () => FillRow(sb, row, SPage));
        var footerRow = row + 1;
        Row(sb, footerRow, 22, () =>
        {
            Text(sb, "A", footerRow, SFooter, "TRACK · CONTRIBUTE · EARN          ◆          BID WISELY · LOOT FAIRLY");
            BlankRange(sb, footerRow, SFooter, 1);
        });
        // A couple trailing dark rows so the panel doesn't end on a hard edge.
        Row(sb, footerRow + 1, 8, () => FillRow(sb, footerRow + 1, SPage));
        Row(sb, footerRow + 2, 8, () => FillRow(sb, footerRow + 2, SPage));

        sb.Append("</sheetData>");

        // Merges: title, subtitle, the three multi-column cards (label + value), footer.
        sb.Append("<mergeCells count=\"9\">");
        sb.Append("<mergeCell ref=\"A2:G2\"/>");
        sb.Append("<mergeCell ref=\"A3:G3\"/>");
        sb.Append("<mergeCell ref=\"B5:C5\"/>");
        sb.Append("<mergeCell ref=\"D5:E5\"/>");
        sb.Append("<mergeCell ref=\"F5:G5\"/>");
        sb.Append("<mergeCell ref=\"B6:C6\"/>");
        sb.Append("<mergeCell ref=\"D6:E6\"/>");
        sb.Append("<mergeCell ref=\"F6:G6\"/>");
        sb.Append("<mergeCell ref=\"A").Append(footerRow).Append(":G").Append(footerRow).Append("\"/>");
        sb.Append("</mergeCells>");

        sb.Append("</worksheet>");
        return sb.ToString();
    }

    // ---- cell emit helpers ----

    private static void Row(StringBuilder sb, int rowNumber, int heightPx, Action cells)
    {
        sb.Append("<row r=\"").Append(rowNumber).Append("\" ht=\"").Append(heightPx)
          .Append("\" customHeight=\"1\">");
        cells();
        sb.Append("</row>");
    }

    // Fills every column A..G of a row with one blank style (page/spacer rows).
    private static void FillRow(StringBuilder sb, int rowNumber, int style)
    {
        foreach (var col in Columns) { Blank(sb, col, rowNumber, style); }
    }

    // Blank styled cells for columns B..G (the non-anchor cells of a full-width
    // merge, so the merged area carries the fill/border across the whole span).
    private static void BlankRange(StringBuilder sb, int rowNumber, int style, int fromColumnIndex)
    {
        for (var c = fromColumnIndex; c < Columns.Length; c++) { Blank(sb, Columns[c], rowNumber, style); }
    }

    private static void Blank(StringBuilder sb, string col, int row, int style)
        => sb.Append("<c r=\"").Append(col).Append(row).Append("\" s=\"").Append(style).Append("\"/>");

    private static void Text(StringBuilder sb, string col, int row, int style, string text)
        => sb.Append("<c r=\"").Append(col).Append(row).Append("\" s=\"").Append(style)
             .Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(Escape(text))
             .Append("</t></is></c>");

    private static void Num(StringBuilder sb, string col, int row, int style, double value)
        => sb.Append("<c r=\"").Append(col).Append(row).Append("\" s=\"").Append(style)
             .Append("\"><v>").Append(value.ToString(CultureInfo.InvariantCulture)).Append("</v></c>");

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
