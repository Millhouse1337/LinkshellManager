using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// A .xlsx is a ZIP of XML, so a tiny crafted DKP import could claim a cell in a far-off column
// (every row padded out to it) or unpack to gigabytes, running the one app process out of memory.
// The reader caps columns per row, the unpacked size of each part, and rows.
public class XlsxImportReaderCapsTests
{
    private static MemoryStream Xlsx(string sheetXml, int extraEntries = 0)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml");
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
            {
                w.Write(sheetXml);
            }
            for (var i = 0; i < extraEntries; i++)
            {
                zip.CreateEntry($"junk/{i}.xml");
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static string Sheet(string rows) =>
        "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
        + rows + "</sheetData></worksheet>";

    private static string Cell(string reference, string value) =>
        $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{value}</t></is></c>";

    [Fact]
    public void ANormalSheet_ReadsAsBefore()
    {
        var rows = XlsxImportReader.Read(
            Xlsx(Sheet("<row r=\"1\">" + Cell("A1", "Name") + Cell("C1", "DKP") + "</row>")),
            "import.xlsx");

        var row = Assert.Single(rows);
        Assert.Equal(new object[] { "Name", "", "DKP" }, row.ToArray());
    }

    [Fact]
    public void ACellFarToTheRight_IsSkipped_NotPaddedOutTo()
    {
        // XFD is Excel's last column (16,384); the row must not grow to that width.
        var rows = XlsxImportReader.Read(
            Xlsx(Sheet("<row r=\"1\">" + Cell("A1", "Name") + Cell("XFD1", "far") + Cell("ZZZZZZZZZZ1", "huge") + "</row>")),
            "import.xlsx");

        var row = Assert.Single(rows);
        Assert.Equal(new object[] { "Name" }, row.ToArray());
    }

    [Fact]
    public void ARowWithThousandsOfCells_StopsAtTheColumnCap()
    {
        // No cell references: cells append in order, so the count itself must be capped.
        var cells = string.Concat(Enumerable.Repeat("<c><v>1</v></c>", 5000));
        var rows = XlsxImportReader.Read(Xlsx(Sheet("<row>" + cells + "</row>")), "import.xlsx");

        Assert.Equal(XlsxImportReader.MaxColumns, Assert.Single(rows).Count);
    }

    [Fact]
    public void APartThatUnpacksPastTheCap_IsRefused()
    {
        // Compresses to a few KB, unpacks to over 4 MB.
        var padding = new string(' ', (int)XlsxImportReader.MaxEntryBytes + 1);
        var stream = Xlsx(Sheet("<row r=\"1\">" + Cell("A1", "x") + "</row>" + padding));
        Assert.True(stream.Length < 1_000_000);

        var ex = Assert.Throws<InvalidOperationException>(() => XlsxImportReader.Read(stream, "import.xlsx"));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void AZipWithHundredsOfParts_IsRefused()
    {
        var stream = Xlsx(Sheet(string.Empty), extraEntries: 600);

        Assert.Throws<InvalidOperationException>(() => XlsxImportReader.Read(stream, "import.xlsx"));
    }

    [Fact]
    public void ASheetWithADtd_IsRefused()
    {
        // Entity expansion ("billion laughs") needs a DTD; real .xlsx parts never have one.
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE worksheet [<!ENTITY a \"aaaaaaaaaa\">]>" + Sheet(string.Empty);

        Assert.ThrowsAny<System.Xml.XmlException>(() => XlsxImportReader.Read(Xlsx(xml), "import.xlsx"));
    }

    [Fact]
    public void RowsStopAtMaxRows()
    {
        var rows = string.Concat(Enumerable.Range(1, XlsxImportReader.MaxRows + 50)
            .Select(i => $"<row r=\"{i}\">" + Cell($"A{i}", "m") + "</row>"));

        Assert.Equal(XlsxImportReader.MaxRows, XlsxImportReader.Read(Xlsx(Sheet(rows)), "import.xlsx").Count);
    }

    [Fact]
    public void ACsvLineOfEndlessCommas_StopsAtTheColumnCap()
    {
        var csv = "Name" + new string(',', 100_000) + "\nBob,5\n";
        var rows = XlsxImportReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "import.csv");

        Assert.Equal(2, rows.Count);
        Assert.Equal(XlsxImportReader.MaxColumns, rows[0].Count);
        Assert.Equal(new object[] { "Bob", "5" }, rows[1].ToArray());
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("C5", 2)]
    [InlineData("AB12", 27)]
    [InlineData("BL1", 63)]
    [InlineData("BM1", 64)]
    [InlineData("ZZZZZZZZZZZZ1", 64)]
    public void ColumnIndex_IsCappedAndNeverOverflows(string reference, int expected)
    {
        Assert.Equal(expected, XlsxImportReader.ColumnIndex(reference));
    }
}
