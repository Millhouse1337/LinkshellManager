using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The sample template is a hand-rolled .xlsx (raw OOXML). These tests guard the
// two ways a hand-built workbook silently corrupts: malformed XML, and a
// styles.xml whose declared counts or referenced style indices don't line up
// (Excel shows a "needs repair" prompt rather than failing loudly). They don't
// prove Excel opens it, but they catch every structural mistake we can check
// without pulling in the OpenXML SDK.
public class SampleDkpTemplateWorkbookTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static Dictionary<string, string> ReadParts()
    {
        var bytes = SampleDkpTemplateWorkbook.Build();
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(
            e => e.FullName,
            e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); });
    }

    [Fact]
    public void Build_EmitsTheExpectedParts()
    {
        var parts = ReadParts();
        Assert.Contains("[Content_Types].xml", parts.Keys);
        Assert.Contains("_rels/.rels", parts.Keys);
        Assert.Contains("xl/workbook.xml", parts.Keys);
        Assert.Contains("xl/_rels/workbook.xml.rels", parts.Keys);
        Assert.Contains("xl/styles.xml", parts.Keys);
        Assert.Contains("xl/worksheets/sheet1.xml", parts.Keys);
    }

    [Fact]
    public void Build_EveryPartIsWellFormedXml()
    {
        foreach (var (name, xml) in ReadParts())
        {
            var ex = Record.Exception(() => XDocument.Parse(xml));
            Assert.True(ex is null, $"{name} is not well-formed XML: {ex?.Message}");
        }
    }

    [Fact]
    public void Styles_DeclaredCountsMatchChildren()
    {
        var styles = XDocument.Parse(ReadParts()["xl/styles.xml"]);
        XName N(string n) => XName.Get(n, Main);

        foreach (var collection in new[] { "fonts", "fills", "borders", "cellXfs", "cellStyleXfs" })
        {
            var element = styles.Root!.Element(N(collection));
            Assert.NotNull(element);
            var declared = int.Parse(element!.Attribute("count")!.Value);
            var actual = element.Elements().Count();
            Assert.True(declared == actual,
                $"styles.xml <{collection}> declares count={declared} but has {actual} children.");
        }
    }

    [Fact]
    public void Sheet_EveryCellStyleIndexExists()
    {
        var parts = ReadParts();
        var styles = XDocument.Parse(parts["xl/styles.xml"]);
        var cellXfsCount = styles.Root!
            .Element(XName.Get("cellXfs", Main))!
            .Elements().Count();

        // Pull every s="N" off the worksheet's cells and confirm it's a valid xf.
        var sheet = parts["xl/worksheets/sheet1.xml"];
        var referenced = Regex.Matches(sheet, "<c [^>]*\\bs=\"(\\d+)\"")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToHashSet();

        Assert.NotEmpty(referenced);
        foreach (var s in referenced)
        {
            Assert.True(s < cellXfsCount,
                $"Worksheet references style index {s} but cellXfs only has {cellXfsCount} entries.");
        }
    }

    [Fact]
    public void Sheet_MergeCountMatchesDeclared()
    {
        var sheet = XDocument.Parse(ReadParts()["xl/worksheets/sheet1.xml"]);
        var merge = sheet.Root!.Element(XName.Get("mergeCells", Main));
        Assert.NotNull(merge);
        var declared = int.Parse(merge!.Attribute("count")!.Value);
        Assert.Equal(declared, merge.Elements().Count());
    }
}
