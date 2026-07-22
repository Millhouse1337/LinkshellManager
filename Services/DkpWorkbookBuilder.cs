using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace LinkshellManagerDiscordApp.Services;

// Builds a real, downloadable .xlsx of a linkshell's live DKP (from DkpSheetData)
// — the same styled layout as the in-app DKP sheet: a branded title bar, a summary
// row (TOTAL MEMBERS / TOTAL DKP / BIDDABLE DKP / TOTAL DKP SPENT), then the
// 7-column member table. Hand-rolled OOXML (an .xlsx is just a ZIP of a few XML
// parts) so no Excel library is needed. Styling: navy title (#262939), lavender
// summary (#EDF0FA), periwinkle header (#6366F2), alternating white / #F2F2FF
// member stripes.
public static class DkpWorkbookBuilder
{
    public const string ContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly string[] Columns = { "A", "B", "C", "D", "E", "F", "G" };

    // A safe, dated download name, e.g. "MyLinkshell-DKP-2026-06-24.xlsx".
    public static string FileName(string linkshellName, DateTime utcNow)
    {
        var slug = new string((linkshellName ?? "Linkshell")
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray()).Trim('-');
        if (string.IsNullOrEmpty(slug)) { slug = "Linkshell"; }
        return $"{slug}-DKP-{utcNow:yyyy-MM-dd}.xlsx";
    }

    public static byte[] Build(DkpSheetData data)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", ContentTypesXml);
            WriteEntry(archive, "_rels/.rels", RootRelsXml);
            WriteEntry(archive, "xl/workbook.xml", WorkbookXml);
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
            WriteEntry(archive, "xl/styles.xml", StylesXml);
            WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildSheetXml(data));
        }
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string xml)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
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

    private const int STitle = 1;
    private const int SSumLabel = 2;
    private const int SSumInt = 3;
    private const int SSumNum = 4;
    private const int SHeader = 5;
    private const int SDataTextWhite = 6;
    private const int SDataTextBand = 7;
    private const int SDataNumWhite = 8;
    private const int SDataNumBand = 9;

    private const string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"6\">" +
            "<font><sz val=\"11\"/><color rgb=\"FF000000\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "<font><b/><sz val=\"18\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font>" +
            "<font><b/><sz val=\"10\"/><color rgb=\"FF000000\"/><name val=\"Calibri\"/></font>" +
            "<font><b/><sz val=\"16\"/><color rgb=\"FF000000\"/><name val=\"Calibri\"/></font>" +
            "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font>" +
            "<font><sz val=\"11\"/><color rgb=\"FF000000\"/><name val=\"Calibri\"/></font>" +
        "</fonts>" +
        "<fills count=\"6\">" +
            "<fill><patternFill patternType=\"none\"/></fill>" +
            "<fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF262939\"/></patternFill></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEDF0FA\"/></patternFill></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF6366F2\"/></patternFill></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2FF\"/></patternFill></fill>" +
        "</fills>" +
        "<borders count=\"2\">" +
            "<border><left/><right/><top/><bottom/><diagonal/></border>" +
            "<border>" +
                "<left style=\"thin\"><color rgb=\"FFB9BECC\"/></left>" +
                "<right style=\"thin\"><color rgb=\"FFB9BECC\"/></right>" +
                "<top style=\"thin\"><color rgb=\"FFB9BECC\"/></top>" +
                "<bottom style=\"thin\"><color rgb=\"FFB9BECC\"/></bottom>" +
                "<diagonal/>" +
            "</border>" +
        "</borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"10\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
            "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" +
            "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" indent=\"1\"/></xf>" +
            "<xf numFmtId=\"4\" fontId=\"3\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" indent=\"1\"/></xf>" +
            "<xf numFmtId=\"0\" fontId=\"4\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
            "<xf numFmtId=\"0\" fontId=\"5\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" +
            "<xf numFmtId=\"0\" fontId=\"5\" fillId=\"5\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" indent=\"1\"/></xf>" +
            "<xf numFmtId=\"4\" fontId=\"5\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" indent=\"1\"/></xf>" +
            "<xf numFmtId=\"4\" fontId=\"5\" fillId=\"5\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" indent=\"1\"/></xf>" +
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";

    private static string BuildSheetXml(DkpSheetData data)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"4\" topLeftCell=\"A5\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        sb.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");
        sb.Append("<cols>");
        sb.Append("<col min=\"1\" max=\"1\" width=\"24\" customWidth=\"1\"/>");
        sb.Append("<col min=\"2\" max=\"3\" width=\"16\" customWidth=\"1\"/>");
        sb.Append("<col min=\"4\" max=\"7\" width=\"16\" customWidth=\"1\"/>");
        sb.Append("</cols>");
        sb.Append("<sheetData>");

        // Row 1 — navy title bar (merged A1:G1).
        Row(sb, 1, 40, () => { Text(sb, "A", 1, STitle, $"{data.LinkshellName} — DKP"); BlankRange(sb, 1, STitle, 1); });

        // Row 2 — summary labels (A | B:C | D:E | F:G).
        Row(sb, 2, 22, () =>
        {
            Text(sb, "A", 2, SSumLabel, "TOTAL MEMBERS");
            Text(sb, "B", 2, SSumLabel, "TOTAL DKP");        Blank(sb, "C", 2, SSumLabel);
            Text(sb, "D", 2, SSumLabel, "BIDDABLE DKP");     Blank(sb, "E", 2, SSumLabel);
            Text(sb, "F", 2, SSumLabel, "TOTAL DKP SPENT");  Blank(sb, "G", 2, SSumLabel);
        });

        // Row 3 — summary values.
        Row(sb, 3, 30, () =>
        {
            Num(sb, "A", 3, SSumInt, data.TotalMembers);
            Num(sb, "B", 3, SSumNum, data.TotalDkp);    Blank(sb, "C", 3, SSumNum);
            Num(sb, "D", 3, SSumNum, data.Biddable);    Blank(sb, "E", 3, SSumNum);
            Num(sb, "F", 3, SSumNum, data.TotalSpent);  Blank(sb, "G", 3, SSumNum);
        });

        // Row 4 — periwinkle table header.
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

        // Rows 5.. — member data, alternating white / lavender stripes.
        var row = 5;
        var bandIndex = 0;
        foreach (var m in data.Members)
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

        sb.Append("</sheetData>");

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

    private static void Row(StringBuilder sb, int rowNumber, int heightPx, Action cells)
    {
        sb.Append("<row r=\"").Append(rowNumber).Append("\" ht=\"").Append(heightPx).Append("\" customHeight=\"1\">");
        cells();
        sb.Append("</row>");
    }

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

    // Escape XML special chars AND drop characters illegal in XML 1.0 (everything
    // below 0x20 except tab/LF/CR) — member/alt/linkshell names are user-editable, and
    // a stray control char would make Excel reject the whole workbook as corrupt.
    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c < 0x20 && c is not '\t' and not '\n' and not '\r')
            {
                continue; // illegal in XML 1.0 — skip
            }
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
