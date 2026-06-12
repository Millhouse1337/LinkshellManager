using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace LinkshellManagerDiscordApp.Services;

// Builds a self-contained .xlsx (Office Open XML) showing the canonical
// "LSM DKP" template — a branded title bar, a summary row (TOTAL MEMBERS /
// TOTAL DKP / BIDDABLE DKP / TOTAL DKP SPENT), the 7-column member table, and a
// few empty ✦ rows to fill in — so a linkshell sees exactly how to format a tab
// they want to IMPORT. Hand-rolled (an .xlsx is just a ZIP of a few XML parts)
// to avoid pulling in an Excel library for one static download.
//
// Styling mirrors the LIVE Google Sheets export (DkpTemplateSheetService) one
// for one: navy title bar (#262939, white bold), lavender summary block
// (#EDF0FA, bold labels + big values), periwinkle header row (#6366F2, white
// bold), and alternating white / #F2F2FF member stripes — relying on the
// spreadsheet's default light gridlines rather than a heavy black grid, so it
// reads the same in Excel or Google Sheets. The importable layout (Member Name |
// Alt 1 | Alt 2 | Current DKP | Biddable DKP | Total DKP | Total DKP Spent) is
// what the importer matches on.
public static class SampleDkpTemplateWorkbook
{
    public const string FileName = "LSM-DKP-Template-Sample.xlsx";
    public const string ContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // The 7 import columns map to spreadsheet columns A..G.
    private static readonly string[] Columns = { "A", "B", "C", "D", "E", "F", "G" };

    // Sample members. Strings render as text; doubles as numeric cells (fractional
    // values show DKP can be quarter/half). Summary totals below are summed from
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

    // Style indices into cellXfs below (named so the sheet builder reads clearly).
    private const int STitle = 1;          // navy title bar, white bold (merged)
    private const int SSumLabel = 2;       // summary label (lavender, bold)
    private const int SSumInt = 3;         // summary value, integer (member count)
    private const int SSumNum = 4;         // summary value, #,##0.00
    private const int SHeader = 5;         // periwinkle header row, white bold
    private const int SDataTextWhite = 6;  // member text cell, white stripe
    private const int SDataTextBand = 7;   // member text cell, lavender stripe
    private const int SDataNumWhite = 8;   // member #,##0.00 cell, white stripe
    private const int SDataNumBand = 9;    // member #,##0.00 cell, lavender stripe

    // Palette lifted verbatim from the live export (DkpTemplateSheetService):
    //   title #262939 · summary #EDF0FA · header #6366F2 · band #F2F2FF.
    private const string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"6\">" +
            "<font><sz val=\"11\"/><color rgb=\"FF000000\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" + // 0 default
            "<font><b/><sz val=\"18\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font>" +                // 1 title (white)
            "<font><b/><sz val=\"10\"/><color rgb=\"FF000000\"/><name val=\"Calibri\"/></font>" +                // 2 summary label
            "<font><b/><sz val=\"16\"/><color rgb=\"FF000000\"/><name val=\"Calibri\"/></font>" +                // 3 summary value
            "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font>" +                // 4 header (white)
            "<font><sz val=\"11\"/><color rgb=\"FF000000\"/><name val=\"Calibri\"/></font>" +                    // 5 data normal
        "</fonts>" +
        "<fills count=\"6\">" +
            "<fill><patternFill patternType=\"none\"/></fill>" +     // 0
            "<fill><patternFill patternType=\"gray125\"/></fill>" +  // 1
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF262939\"/></patternFill></fill>" + // 2 title navy
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEDF0FA\"/></patternFill></fill>" + // 3 summary lavender
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF6366F2\"/></patternFill></fill>" + // 4 header periwinkle
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2FF\"/></patternFill></fill>" + // 5 band
        "</fills>" +
        // Thin gray cell borders so the grid is clearly visible over the colored
        // fills (the title bar stays borderless to read as one solid band).
        "<borders count=\"2\">" +
            "<border><left/><right/><top/><bottom/><diagonal/></border>" + // 0 none
            "<border>" +
                "<left style=\"thin\"><color rgb=\"FFB9BECC\"/></left>" +
                "<right style=\"thin\"><color rgb=\"FFB9BECC\"/></right>" +
                "<top style=\"thin\"><color rgb=\"FFB9BECC\"/></top>" +
                "<bottom style=\"thin\"><color rgb=\"FFB9BECC\"/></bottom>" +
                "<diagonal/>" +
            "</border>" + // 1 thin gray grid
        "</borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"10\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +                                                                                                              // 0 default
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" + // 1 title
            "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" +  // 2 summary label
            "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" indent=\"1\"/></xf>" + // 3 summary int
            "<xf numFmtId=\"4\" fontId=\"3\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" indent=\"1\"/></xf>" + // 4 summary num
            "<xf numFmtId=\"0\" fontId=\"4\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" + // 5 header
            "<xf numFmtId=\"0\" fontId=\"5\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" +      // 6 data text white
            "<xf numFmtId=\"0\" fontId=\"5\" fillId=\"5\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" + // 7 data text band
            "<xf numFmtId=\"4\" fontId=\"5\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" indent=\"1\"/></xf>" +  // 8 data num white
            "<xf numFmtId=\"4\" fontId=\"5\" fillId=\"5\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" indent=\"1\"/></xf>" + // 9 data num band
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";

    private static string BuildSheetXml()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
        sb.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");
        sb.Append("<cols>");
        sb.Append("<col min=\"1\" max=\"1\" width=\"24\" customWidth=\"1\"/>");
        sb.Append("<col min=\"2\" max=\"3\" width=\"16\" customWidth=\"1\"/>");
        sb.Append("<col min=\"4\" max=\"7\" width=\"16\" customWidth=\"1\"/>");
        sb.Append("</cols>");
        sb.Append("<sheetData>");

        // Row 1 — navy title bar (merged A1:G1).
        Row(sb, 1, 40, () => { Text(sb, "A", 1, STitle, "LSM DKP TEMPLATE"); BlankRange(sb, 1, STitle, 1); });

        // Row 2 — summary labels (A | B:C | D:E | F:G).
        Row(sb, 2, 22, () =>
        {
            Text(sb, "A", 2, SSumLabel, "TOTAL MEMBERS");
            Text(sb, "B", 2, SSumLabel, "TOTAL DKP");        Blank(sb, "C", 2, SSumLabel);
            Text(sb, "D", 2, SSumLabel, "BIDDABLE DKP");     Blank(sb, "E", 2, SSumLabel);
            Text(sb, "F", 2, SSumLabel, "TOTAL DKP SPENT");  Blank(sb, "G", 2, SSumLabel);
        });

        // Row 3 — summary values.
        var totalDkp = 0.0; var biddable = 0.0; var spent = 0.0;
        foreach (var m in Members) { totalDkp += m.Total; biddable += m.Biddable; spent += m.Spent; }
        Row(sb, 3, 30, () =>
        {
            Num(sb, "A", 3, SSumInt, Members.Length);
            Num(sb, "B", 3, SSumNum, totalDkp);  Blank(sb, "C", 3, SSumNum);
            Num(sb, "D", 3, SSumNum, biddable);  Blank(sb, "E", 3, SSumNum);
            Num(sb, "F", 3, SSumNum, spent);     Blank(sb, "G", 3, SSumNum);
        });

        // Row 4 — periwinkle table header (the row the importer matches on).
        Row(sb, 4, 24, () =>
        {
            Text(sb, "A", 4, SHeader, "Member Name");
            Text(sb, "B", 4, SHeader, "Alt 1");
            Text(sb, "C", 4, SHeader, "Alt 2");
            Text(sb, "D", 4, SHeader, "Current DKP");
            Text(sb, "E", 4, SHeader, "Biddable DKP");
            Text(sb, "F", 4, SHeader, "Total DKP");
            Text(sb, "G", 4, SHeader, "Total DKP Spent");
        });

        // Rows 5.. — member data, alternating white / lavender stripes (the first
        // data row is white, matching the live export's banding).
        var row = 5;
        var bandIndex = 0;
        foreach (var m in Members)
        {
            var r = row;
            var band = bandIndex % 2 == 1;
            var textStyle = band ? SDataTextBand : SDataTextWhite;
            var numStyle = band ? SDataNumBand : SDataNumWhite;
            Row(sb, r, 22, () =>
            {
                Text(sb, "A", r, textStyle, m.Name);
                Text(sb, "B", r, textStyle, m.Alt1);
                Text(sb, "C", r, textStyle, m.Alt2);
                Num(sb, "D", r, numStyle, m.Current);
                Num(sb, "E", r, numStyle, m.Biddable);
                Num(sb, "F", r, numStyle, m.Total);
                Num(sb, "G", r, numStyle, m.Spent);
            });
            row++;
            bandIndex++;
        }

        // Empty ✦ rows to fill in — continue the stripe so the table reads as one.
        for (var i = 0; i < 5; i++)
        {
            var r = row;
            var band = bandIndex % 2 == 1;
            var textStyle = band ? SDataTextBand : SDataTextWhite;
            Row(sb, r, 22, () =>
            {
                Text(sb, "A", r, textStyle, "✦");
                for (var c = 1; c < Columns.Length; c++) { Blank(sb, Columns[c], r, textStyle); }
            });
            row++;
            bandIndex++;
        }

        sb.Append("</sheetData>");

        // Merges: title (A:G) + the three summary cards across label and value rows.
        sb.Append("<mergeCells count=\"7\">");
        sb.Append("<mergeCell ref=\"A1:G1\"/>");
        sb.Append("<mergeCell ref=\"B2:C2\"/>");
        sb.Append("<mergeCell ref=\"D2:E2\"/>");
        sb.Append("<mergeCell ref=\"F2:G2\"/>");
        sb.Append("<mergeCell ref=\"B3:C3\"/>");
        sb.Append("<mergeCell ref=\"D3:E3\"/>");
        sb.Append("<mergeCell ref=\"F3:G3\"/>");
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

    // Blank styled cells for columns from a starting index to G (the non-anchor
    // cells of a merge, so the merged area carries the fill across the span).
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
