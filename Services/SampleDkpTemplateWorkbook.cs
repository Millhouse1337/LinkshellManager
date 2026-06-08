using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace LinkshellManagerDiscordApp.Services;

// Builds a tiny, self-contained .xlsx (Office Open XML) showing the canonical
// "LSM DKP" template layout with sample data, so a linkshell can see exactly how
// to format a tab they want to IMPORT. Hand-rolled (an .xlsx is just a ZIP of a
// few XML parts) to avoid pulling in an Excel library for one static download.
// Uses inline strings so there's no shared-strings part and no styles part —
// the minimum Excel will open cleanly.
public static class SampleDkpTemplateWorkbook
{
    public const string FileName = "LSM-DKP-Template-Sample.xlsx";
    public const string ContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // Title row, then the header row the importer matches on, then a few sample
    // members. Mirrors what Export produces (Biddable DKP included even though
    // import ignores it) so the sample == the real format. Strings render as
    // text cells; doubles as numeric cells (fractional values show DKP can be
    // quarter/half). Empty string = a blank alt slot.
    private static readonly object?[][] Rows =
    {
        new object?[] { "Sample Linkshell — DKP" },
        new object?[] { "Member Name", "Alt 1", "Alt 2", "Current DKP", "Biddable DKP", "Total DKP", "Total DKP Spent" },
        new object?[] { "Member1", "Member1Alt1", "Member1Alt2", 1250.5, 1000.5, 5400.0, 4149.5 },
        new object?[] { "Member2", "Member2Alt1", "Member2Alt2", 875.0, 875.0, 3200.0, 2325.0 },
        new object?[] { "Member3", "Member3Alt1", "Member3Alt2", 1500.25, 1500.25, 6000.0, 4499.75 },
    };

    private static readonly string[] Columns = { "A", "B", "C", "D", "E", "F", "G" };

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
        "</Relationships>";

    private static string BuildSheetXml()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < Rows.Length; r++)
        {
            var rowNumber = r + 1;
            sb.Append("<row r=\"").Append(rowNumber).Append("\">");
            var cells = Rows[r];
            for (var c = 0; c < cells.Length && c < Columns.Length; c++)
            {
                var value = cells[c];
                var reference = Columns[c] + rowNumber.ToString(CultureInfo.InvariantCulture);
                switch (value)
                {
                    case null:
                        break;
                    case double number:
                        sb.Append("<c r=\"").Append(reference).Append("\"><v>")
                          .Append(number.ToString(CultureInfo.InvariantCulture))
                          .Append("</v></c>");
                        break;
                    case string text when text.Length == 0:
                        break; // skip empty cells (sparse rows are valid)
                    case string text:
                        sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                          .Append(Escape(text))
                          .Append("</t></is></c>");
                        break;
                }
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
