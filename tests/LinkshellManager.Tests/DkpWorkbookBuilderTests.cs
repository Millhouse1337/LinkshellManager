using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The .xlsx is hand-rolled OOXML, so member/linkshell names must be XML-escaped AND
// stripped of characters illegal in XML 1.0 — otherwise Excel rejects the workbook as
// corrupt. These guard DkpWorkbookBuilder.Escape.
public class DkpWorkbookBuilderTests
{
    // BEL (U+0007) — a control char illegal in XML 1.0. Built from its code point so no
    // literal control char appears in source. Ampersand/angle brackets are XML specials.
    private static readonly string Bel = ((char)7).ToString();

    private static DkpSheetData DataWithName(string memberName, string linkshellName) =>
        new(
            LinkshellId: 1,
            LinkshellName: linkshellName,
            Members: new[] { new DkpSheetMemberRow(1, memberName, "alt1", "alt2", 1.0, 2.0, 3.0, 4.0) },
            TotalMembers: 1,
            TotalDkp: 3.0,
            Biddable: 2.0,
            TotalSpent: 4.0);

    [Fact]
    public void Build_WithSpecialAndControlChars_ProducesWellFormedXlsx()
    {
        // Name carries XML specials (& < >) and an illegal control char (BEL).
        var data = DataWithName("A&B<C>" + Bel + "D", "LS <&>" + Bel + " Name");

        var bytes = DkpWorkbookBuilder.Build(data);

        // Every XML part must parse — a stray control char or unescaped '<' would throw here,
        // which is exactly the "Excel can't open it" corruption we're guarding against.
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        string? worksheetXml = null;
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.EndsWith(".xml") && !entry.FullName.EndsWith(".rels")) continue;
            using var reader = new StreamReader(entry.Open());
            var xml = reader.ReadToEnd();
            XDocument.Parse(xml); // throws on malformed XML → test fails
            if (entry.FullName.Contains("worksheets")) worksheetXml = xml;
        }

        Assert.NotNull(worksheetXml);
        Assert.Contains("A&amp;B&lt;C&gt;D", worksheetXml);   // escaped, control char dropped
        // Ordinal search: a culture-aware Contains treats control chars as ignorable and
        // would falsely "find" the BEL, so use IndexOf(char) (ordinal) to prove it's gone.
        Assert.True(worksheetXml!.IndexOf((char)7) < 0, "worksheet must not contain a raw BEL control char");
    }
}
