using System.IO.Compression;
using System.Text;
using Xunit;

namespace WinAIMagic.Documents.Tests;

/// The Word engine on a template with a cover, a table of contents, numbered headings and a table.
/// WINAIMAGIC_DOCX_OUT=<dir> keeps the results, to check them with other programs.
public class DocumentEngineTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sjabloon.docx");

    private const string Markdown = """
    ---
    klant: RIVM
    versie: 0.1
    datum: 1 jan
    auteur: MF
    ---

    # Inleiding
    ## 1.1 Aanleiding
    Tekst met **vet**, *cursief*, `code` en een [link](https://example.org).

    - punt
      - subpunt
    - punt twee

    1. eerste
    2. tweede

    # Bevindingen
    | Thema | Bevinding |
    |---|---|
    | IAM | Geen MFA |

    ![Plaat](plaat.png)

    > Citaat

    ```
    code regel
    ```
    """;

    private static byte[] Template() => File.ReadAllBytes(Fixture);

    /// A real PNG of 200 by 100 pixels, without pHYs.
    internal static byte[] Png(int width = 200, int height = 100, byte[]? extraChunk = null)
    {
        static void Chunk(MemoryStream s, string type, byte[] body)
        {
            var typed = Encoding.ASCII.GetBytes(type).Concat(body).ToArray();
            s.Write(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(body.Length)));
            s.Write(typed);
            s.Write(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder((int)Zip.Crc32(typed))));
        }
        var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(width)).CopyTo(ihdr, 0);
        BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(height)).CopyTo(ihdr, 4);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk(png, "IHDR", ihdr);
        if (extraChunk != null) Chunk(png, "pHYs", extraChunk);
        var pixels = new MemoryStream();
        using (var z = new ZLibStream(pixels, CompressionLevel.Fastest, leaveOpen: true)) z.Write(new byte[height * (1 + width * 3)]);
        Chunk(png, "IDAT", pixels.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static string Output(string name)
    {
        var dir = Environment.GetEnvironmentVariable("WINAIMAGIC_DOCX_OUT") ?? Path.GetTempPath();
        return Path.Combine(dir, name + ".docx");
    }

    [Fact]
    public void TestZipKeepsEveryPart()
    {
        var data = Template();
        var again = Zip.Read(Zip.Write(Zip.Read(data)));
        var original = Zip.Read(data);
        Assert.Equal(original.Select(e => e.Name), again.Select(e => e.Name));
        for (int i = 0; i < again.Count; i++) Assert.Equal(original[i].Data, again[i].Data);
    }

    [Fact]
    public void TestPartsThatAreNotTouchedStayByteForByte()
    {
        var original = Zip.Read(Template()).ToDictionary(e => e.Name, e => e.Data);
        var output = Output("onaangeroerd");
        DocxCompose.Build(Template(), "# Titel\n\nTekst.", output);
        var result = Zip.Read(File.ReadAllBytes(output)).ToDictionary(e => e.Name, e => e.Data);
        foreach (var name in new[] { "word/theme/theme1.xml", "word/fontTable.xml", "word/webSettings.xml", "docProps/thumbnail.jpeg", "customXml/item1.xml" })
            Assert.Equal(original[name], result[name]);
    }

    [Fact]
    public void TestXMLKeepsNamespacesAndText()
    {
        const string source = "<a:r xmlns:a=\"urn:a\" a:x=\"1 &amp; 2\"><!-- c --><a:t xml:space=\"preserve\"> x &lt; y </a:t></a:r>";
        var root = XNode.Parse(Encoding.UTF8.GetBytes(source));
        Assert.Equal("urn:a", root["xmlns:a"]);
        Assert.Equal("1 & 2", root["a:x"]);
        Assert.Equal(" x < y ", root.Find("a:t")?.Text);
        var again = XNode.Parse(root.Serialized());
        Assert.Equal(root.Signature, again.Signature);
    }

    [Fact]
    public void TestOutlineOfTheTemplate()
    {
        var model = new DocModel(new DocxPackage(Template()));
        var outline = model.Outline();
        Assert.Equal("1", outline[0].Number);
        Assert.Equal("Inleiding", outline[0].Title);
        Assert.Contains(outline, n => n.Number == "1.2" && n.Title == "Doel en scope");
    }

    [Fact]
    public void TestNewDocumentInTheHouseStyle()
    {
        var output = Output("nieuw");
        var png = Png();
        var report = DocxCompose.Build(Template(), Markdown, output, s => s == "plaat.png" ? (png, "png") : null);
        Assert.Empty(report.Problems);
        Assert.Equal(3, report.TocEntries);

        var pkg = new DocxPackage(output);
        var model = new DocModel(pkg);
        Assert.Equal(["Inleiding", "Aanleiding"], model.Outline().Select(n => n.Title).Take(2));   // the template numbers headings itself
        var text = string.Join("\n", model.Blocks.Select(b => b.Text));
        Assert.Contains("RIVM", text);
        Assert.DoesNotContain("{{", text);
        Assert.DoesNotContain("[Beschrijf", text);
        var kinds = model.Blocks.Select(b => b.Kind).ToList();
        foreach (var k in new[] { BlockKind.Heading, BlockKind.List, BlockKind.Table, BlockKind.Paragraph, BlockKind.Toc, BlockKind.Image }) Assert.Contains(k, kinds);
        Assert.DoesNotContain("{{klant}}", pkg.Xml("word/header1.xml")?.AllText ?? "");
        Assert.Empty(DocxValidator.Validate(File.ReadAllBytes(output)));
    }

    [Fact]
    public void TestTableTakesTheLookOfTheTemplate()
    {
        var source = new DocModel(new DocxPackage(Template())).Blocks.FirstOrDefault(b => b.Kind == BlockKind.Table);
        var wanted = source?.Element.FindAll("w:tr").FirstOrDefault()?.FindAll("w:tc").FirstOrDefault()?.Find("w:tcPr")?.Find("w:shd")?["w:fill"];
        Assert.NotNull(wanted);   // the template has a coloured header row
        var output = Output("tabel");
        DocxCompose.Build(Template(), "# Kop\n\n| A | B |\n|---|---|\n| 1 | 2 |\n", output);
        var table = new DocModel(new DocxPackage(output)).Blocks.FirstOrDefault(b => b.Kind == BlockKind.Table);
        Assert.Equal(wanted, table?.Element.FindAll("w:tr").FirstOrDefault()?.FindAll("w:tc").FirstOrDefault()?.Find("w:tcPr")?.Find("w:shd")?["w:fill"]);
        Assert.Equal([["A", "B"], ["1", "2"]], table!.Rows);
    }

    [Fact]
    public void TestATemplateIsReadWhole()
    {
        var text = DocxCompose.TemplateText(Template());
        Assert.Contains("Architecture Quick Scan", text);   // what stands before the first heading too
        Assert.Contains("# Inleiding", text);
        Assert.Contains("<!-- ", text);   // its instructions, marked as such
    }

    [Fact]
    public void TestCommentsStayOutOfTheDocument()
    {
        var output = Output("commentaar");
        var report = DocxCompose.Build(Template(), "# Kop\n\n<!-- instructie: niet tonen -->\n\nTekst.\n", output);
        Assert.Empty(report.Problems);
        Assert.DoesNotContain("niet tonen", string.Concat(new DocModel(new DocxPackage(output)).Blocks.Select(b => b.Text)));
    }

    [Fact]
    public void TestEmptyFieldsAreReported()
    {
        var report = DocxCompose.Build(Template(), "---\nklant:\nversie: [Versie]\nauteur: MF\n---\n\n# Kop\n", Output("leeg"));
        Assert.Contains(report.Warnings, w => w.Contains("klant") && w.Contains("versie") && !w.Contains("auteur"));
    }

    [Fact]
    public void TestPlainStyleWithoutTemplate()
    {
        var output = Output("eenvoudig");
        var report = DocxCompose.Build(null, "---\ntitel: Onderzoek naar SSD's\n---\n\n# Antwoord\n\nTekst [1].\n\n## Details\n\n- punt\n\n1. een\n\n# Bronnen\n\n1. [Bron](https://example.org)\n", output);
        Assert.Empty(report.Problems);
        Assert.Equal(3, report.TocEntries);
        var model = new DocModel(new DocxPackage(output));
        Assert.Equal("Onderzoek naar SSD's", model.Blocks[0].Text);
        Assert.Equal(["Antwoord", "Details", "Bronnen"], model.Outline().Select(n => n.Title));
        Assert.Contains(model.Blocks, b => b.Spans.Any(s => s.Link == "https://example.org"));
    }

    [Fact]
    public void TestTemplateFileBecomesADocument()
    {
        // a .dotx declares itself as a template, and Word does not open a .docx that does
        var parts = Zip.Read(Template());
        var entry = parts.First(e => e.Name == "[Content_Types].xml");
        var types = Encoding.UTF8.GetString(entry.Data).Replace("document.main+xml", "template.main+xml");
        Assert.Contains("template.main+xml", types);
        entry.Data = Encoding.UTF8.GetBytes(types);
        var output = Output("uit-dotx");
        DocxCompose.Build(Zip.Write(parts), "# Kop\n", output);
        var result = Zip.Read(File.ReadAllBytes(output)).First(e => e.Name == "[Content_Types].xml");
        Assert.DoesNotContain("template.main+xml", Encoding.UTF8.GetString(result.Data));
    }

    [Fact]
    public void TestFilesThatAreNotWordAreRefused()
    {
        Assert.Throws<Zip.Failure>(() => new DocxPackage(Encoding.UTF8.GetBytes("geen zip")));
        Assert.Throws<Zip.Failure>(() => new DocxPackage(Zip.Write([new ZipEntry("a.txt", Encoding.UTF8.GetBytes("x"))])));
    }

    [Fact]
    public void TestValidatorFindsWhatWordRefuses()
    {
        var parts = Zip.Read(Template());
        var entry = parts.First(e => e.Name == "word/document.xml");
        var root = XNode.Parse(entry.Data);
        var rpr = new XNode("w:rPr", null, [new XNode("w:sz", new() { ["w:val"] = "20" }), new XNode("w:b")]);
        root.Find("w:body")?.Insert(new XNode("w:p", null, [new XNode("w:r", null, [rpr])]), 0);
        entry.Data = root.Serialized();
        Assert.Contains(DocxValidator.Validate(Zip.Write(parts)), p => p.Contains("w:rPr"));
    }

    // MARK: - Markdown

    [Fact]
    public void TestMarkdownBlocks()
    {
        var blocks = MarkdownBlocks.Parse("""
        # Kop één
        Regel een
        regel twee

        - a
          - b
        - c

        1. x
        2. y

        | K1 | K2 |
        |----|:--:|
        | p \| q | r<br>s |

        > quote

        <!-- instructie: niet tonen -->

        <!-- pagebreak -->

        ![Plaat](p.png)

        Figuur 1: uitleg

        ---

        ```swift
        let a = 1
        ```
        """);
        Assert.Equal([MDKind.Heading, MDKind.Paragraph, MDKind.List, MDKind.List, MDKind.List, MDKind.List, MDKind.List, MDKind.Table, MDKind.Quote, MDKind.PageBreak, MDKind.Image, MDKind.Caption, MDKind.Code], blocks.Select(b => b.Kind));
        Assert.Equal("Regel een regel twee", blocks[1].Plain);
        Assert.Equal(1, blocks[3].Level);
        Assert.Equal(blocks[2].ListID, blocks[4].ListID);
        Assert.NotEqual(blocks[2].ListID, blocks[3].ListID);
        Assert.True(blocks[5].Ordered);
        Assert.Equal(2, blocks[7].Header.Count);
        Assert.Equal("p | q", string.Concat(blocks[7].Rows[0][0].Select(i => i.Text)));
        Assert.Contains(blocks[7].Rows[0][1], i => i.IsBreak);
        Assert.Equal("let a = 1", blocks[12].Text);
    }

    [Fact]
    public void TestMarkdownInlines()
    {
        var parts = MarkdownBlocks.Inlines("a **b** *c* ***d*** `e*f` ~~g~~ [h **i**](http://x) 2 * 3 snake_case_naam \\*j\\* [1]");
        Assert.Equal("a b c d e*f g h i 2 * 3 snake_case_naam *j* [1]", string.Concat(parts.Select(p => p.Text)));
        Assert.True(parts.First(p => p.Text == "b").Bold);
        Assert.True(parts.First(p => p.Text == "c").Italic);
        var d = parts.First(p => p.Text == "d");
        Assert.True(d.Bold && d.Italic);
        Assert.True(parts.First(p => p.Text == "e*f").Code);
        Assert.True(parts.First(p => p.Text == "g").Strike);
        Assert.Equal("http://x", parts.First(p => p.Text == "i").Link);
        Assert.True(parts.First(p => p.Text == "i").Bold);
    }
}

/// From DocumentStudioTests.swift: the part that only needs the engine.
public class DocumentPartsTests
{
    [Fact]
    public void TestDraftFieldsSurviveEditing()
    {
        var d = new DocumentDraft([("Betreft", "A: B # C"), ("Klant", "")], "# Kop\n");
        var again = new DocumentDraft(d.Markdown);
        Assert.Equal(["A: B # C", ""], again.Fields.Select(f => f.Value));
        Assert.Equal("# Kop\n", again.Body);
        Assert.Equal("A: B # C", again.Title);
        Assert.Empty(new DocumentDraft("Geen velden\n---\nmeer").Fields);
    }
}

/// What the Mac gets from its system and this port has to do itself. These have no Swift counterpart.
public class WindowsPortTests
{
    private const int Max = 100_000_000;

    [Fact]
    public void ImageSizeOfAPngWithoutResolutionIs96Dpi()
    {
        Assert.Equal((1_905_000, 952_500), DocxBuilder.ImageSize(DocumentEngineTests.Png(), Max));
    }

    [Fact]
    public void ImageSizeReadsPhysOfAPng()
    {
        // 7874 pixels per metre: 200 dpi
        var phys = new byte[] { 0, 0, 0x1E, 0xC2, 0, 0, 0x1E, 0xC2, 1 };
        var (cx, cy) = DocxBuilder.ImageSize(DocumentEngineTests.Png(extraChunk: phys), Max);
        Assert.InRange(cx, 914_400 - 100, 914_400 + 100);
        Assert.InRange(cy, 457_200 - 100, 457_200 + 100);
    }

    [Fact]
    public void ImageSizeReadsAJpegHeader()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 1, 1, 0x2C, 1, 0x2C, 0, 0,     // 300 dpi
            0xFF, 0xC2, 0, 11, 8, 0x01, 0x2C, 0x02, 0x58, 1, 1, 0x11, 0,                                           // 600 by 300, progressive
        ];
        Assert.Equal((1_828_800, 914_400), DocxBuilder.ImageSize(jpeg, Max));
    }

    [Fact]
    public void ImageSizeIsLimitedToTheTextWidth()
    {
        // the height may be 1.1 times the width at most, and the aspect stays
        var (wide, flat) = DocxBuilder.ImageSize(DocumentEngineTests.Png(), 1000);
        Assert.InRange(wide, 999, 1000);
        Assert.InRange(flat, 499, 500);
        var (narrow, high) = DocxBuilder.ImageSize(DocumentEngineTests.Png(100, 200), 1000);
        Assert.InRange(narrow, 549, 550);
        Assert.InRange(high, 1099, 1100);
    }

    [Fact]
    public void AnUnknownImageGetsTheDefaultSize()
    {
        Assert.Equal((1000, 600), DocxBuilder.ImageSize(Encoding.UTF8.GetBytes("<svg/>"), 1000));
    }

    [Fact]
    public void ZipOutputIsReproducibleWithTheFixedDate()
    {
        ZipEntry[] entries = [new ZipEntry("a.xml", Encoding.UTF8.GetBytes(new string('x', 2000))), new ZipEntry("b/c.bin", [1, 2, 3])];
        var first = Zip.Write(entries);
        Assert.Equal(first, Zip.Write(entries));
        Assert.Equal(0, BitConverter.ToUInt16(first, 10));      // time
        Assert.Equal(0x21, BitConverter.ToUInt16(first, 12));   // 1 January 1980
    }

    [Fact]
    public void ABrokenEntryIsRefusedByItsCrc()
    {
        // stored: smaller than deflated, so the content starts after the local header and the name
        var data = Zip.Write([new ZipEntry("a.txt", Encoding.UTF8.GetBytes("abc"))]);
        data[30 + 5 + 1] ^= 0xFF;
        var failure = Assert.Throws<Zip.Failure>(() => Zip.Read(data));
        Assert.Equal("Dit Word-bestand is beschadigd.", failure.Message);
    }

    /// Against another parser: every part of the template, written back, says the same with the same prefixes.
    [Fact]
    public void EveryXmlPartOfTheTemplateSurvivesParsing()
    {
        int compared = 0;
        foreach (var entry in Zip.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sjabloon.docx"))))
        {
            if (!entry.Name.EndsWith(".xml", StringComparison.Ordinal) && !entry.Name.EndsWith(".rels", StringComparison.Ordinal)) continue;
            var root = XNode.Parse(entry.Data);
            var serialized = root.Serialized();
            Assert.Equal(root.Signature, XNode.Parse(serialized).Signature);
            var a = System.Xml.Linq.XDocument.Load(new MemoryStream(entry.Data), System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var b = System.Xml.Linq.XDocument.Load(new MemoryStream(serialized), System.Xml.Linq.LoadOptions.PreserveWhitespace);
            Assert.Equal(Canonical(a.Root!), Canonical(b.Root!));
            compared++;
        }
        Assert.True(compared > 5);
    }

    private static string Canonical(System.Xml.Linq.XElement e)
    {
        var attributes = e.Attributes().Select(a => a.Name + "=" + a.Value).Order(StringComparer.Ordinal);
        var nodes = e.Nodes().Select(n => n is System.Xml.Linq.XElement c ? Canonical(c) : n.ToString());
        var prefix = e.GetPrefixOfNamespace(e.Name.Namespace);
        return $"<{prefix}:{e.Name} {string.Join(" ", attributes)}>{string.Concat(nodes)}</>";
    }

    [Fact]
    public void XmlKeepsWhatIsWritten()
    {
        const string source = "<?xml version=\"1.0\"?><!-- voor --><?pi data?><w:a xmlns:w=\"u\" xmlns:x=\"v\" x:b=\"1\"><w:t>a<![CDATA[<b>]]>c</w:t><!-- in --><w:e></w:e> <x:f/></w:a><!-- na -->";
        var root = XNode.Parse(Encoding.UTF8.GetBytes(source));
        Assert.Equal(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<!-- voor --><?pi data?><w:a xmlns:w=\"u\" xmlns:x=\"v\" x:b=\"1\"><w:t>a&lt;b&gt;c</w:t><!-- in --><w:e/> <x:f/></w:a>",
            Encoding.UTF8.GetString(root.Serialized()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("geen xml")]
    [InlineData("<a><b></a>")]
    [InlineData("<a/><b/>")]
    [InlineData("<a/>tekst")]
    [InlineData("<a x=\"1\" x=\"2\"/>")]
    [InlineData("<a>&onbekend;</a>")]
    public void BrokenXmlIsRefused(string source)
    {
        Assert.Throws<Zip.Failure>(() => XNode.Parse(Encoding.UTF8.GetBytes(source)));
    }

    [Fact]
    public void ACarriageReturnStaysInTheText()
    {
        var root = XNode.Parse(Encoding.UTF8.GetBytes("<a b=\"x&#10;y\">p&#13;q</a>"));
        Assert.Equal("p\rq", root.Text);
        Assert.Equal("x\ny", root["b"]);
        Assert.Equal("<a b=\"x&#10;y\">p&#13;q</a>", root.Signature);
    }

    [Fact]
    public void TheRegularExpressionsTreatLineEndsAsOnTheMac()
    {
        // the dot stops at \r, and $ comes before a \r\n at the end
        Assert.Equal("Kop", new Rx("^# (.*?)[ \\t]*$").First("# Kop \r\n")?.Groups[0]);
        var lines = new Rx("^# (.+?)$", System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.Equal(["Een", "Twee"], lines.Matches("# Een\r\ntekst\r\n# Twee\r\n").Select(m => m.Groups[0]!));
    }

    [Fact]
    public void CharactersAreWhatSwiftCallsCharacters()
    {
        const string emoji = "👩‍💻";   // four scalars, five UTF-16 units, one Character
        Assert.Equal(1, emoji.GraphemeCount());
        Assert.Equal(emoji, (emoji + "x").Prefix(1));
        Assert.Equal("a", "a\U0001F600".Prefix(1));
        Assert.Equal("a\U0001F600", "a\U0001F600b".Prefix(2));
        var parts = MarkdownBlocks.Inlines($"**{emoji}** _{emoji}_");
        Assert.True(parts.First(p => p.Text == emoji).Bold);
    }

    [Fact]
    public void TrimmingIsThatOfCharacterSet()
    {
        Assert.Equal("a\n", "\t a\n ".TrimWS());
        Assert.Equal("a", ((char)0x200B + "" + (char)0xA0 + "a" + (char)0x2029 + "\n").TrimWSNL());
        Assert.Equal(" a ", "\n a \r\n".TrimNL());
    }

    [Fact]
    public void LogicalCompareSortsNumbersAsNumbers()
    {
        Assert.True(Str.LocalizedStandardCompare("Rapport 2", "Rapport 10") < 0);
        Assert.True(Str.LocalizedStandardCompare("b", "A") > 0);
    }

    [Fact]
    public void TheLocalizeHookGetsTheKeyWithPlaceholders()
    {
        var seen = new List<string>();
        var saved = Loc.Localize;
        try
        {
            Loc.Localize = key => { seen.Add(key); return key == "Dubbele %@: %@" ? "%2$@ twice: %1$@" : key; };
            Assert.Equal("x twice: y", Loc.L("Dubbele %@: %@", "y", "x"));
            Assert.Equal("100% van 3", Loc.L("100%% van %@", 3));
            Assert.Contains("Dubbele %@: %@", seen);
        }
        finally
        {
            Loc.Localize = saved;
        }
    }

    [Fact]
    public void PackagePathsAlwaysUseSlashes()
    {
        Assert.Equal("word", NSPath.DeletingLastPathComponent("word/document.xml"));
        Assert.Equal("", NSPath.DeletingLastPathComponent("document.xml"));
        Assert.Equal("word/_rels/a.xml.rels", NSPath.AppendingPathComponent("word", "_rels/a.xml.rels"));
        Assert.Equal("_rels/a.rels", NSPath.AppendingPathComponent("", "_rels/a.rels"));
        Assert.Equal("Rapport 2", NSPath.DeletingPathExtension("Rapport 2.docx"));
        Assert.Equal("png", NSPath.PathExtension("word/media/a.png"));
        Assert.Equal("customXml/a.xml", OOXML.Normalized("word/../customXml/a.xml"));
    }
}
