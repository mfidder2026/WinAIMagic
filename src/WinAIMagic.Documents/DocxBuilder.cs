using System.Text.RegularExpressions;

namespace WinAIMagic.Documents;

/// The bytes and extension of an image a document refers to, or null.
public delegate (byte[] Data, string Ext)? ImageResolver(string source);

/// Turns Markdown into OOXML in the house style of the document it goes into.
public sealed class DocxBuilder
{
    private static readonly Rx LeadingNumber = new("^\\s*(?:(?:hoofdstuk|chapter|bijlage)\\s+)?\\d+(?:\\.\\d+)*\\.?\\s+", RegexOptions.IgnoreCase);
    private static readonly Dictionary<string, string> ImageTypes = new()
    {
        ["png"] = "image/png", ["jpg"] = "image/jpeg", ["jpeg"] = "image/jpeg", ["gif"] = "image/gif", ["bmp"] = "image/bmp",
        ["svg"] = "image/svg+xml", ["emf"] = "image/x-emf", ["wmf"] = "image/x-wmf", ["tif"] = "image/tiff", ["tiff"] = "image/tiff",
    };
    private const int EmuPerTwip = 635;

    public DocModel Model { get; }
    public HouseStyle Style { get; }
    public int WidthTwips { get; }
    private readonly DocxPackage _pkg;
    private readonly ImageResolver? _images;
    private readonly NumberingWriter _numbering;
    private long _bookmarkID;
    private long _drawingID;
    private readonly Dictionary<int, string> _listNumbers = [];
    private int _figure;

    public DocxBuilder(DocModel model, ImageResolver? images = null)
    {
        Model = model;
        _images = images;
        _pkg = model.Pkg;
        Style = new HouseStyle(model);
        _numbering = new NumberingWriter(model.Pkg);
        _bookmarkID = MaxAttribute(_pkg.Document, "w:bookmarkStart", "w:id") + 1;
        _drawingID = MaxAttribute(_pkg.Document, "wp:docPr", "id") + 1;
        WidthTwips = HouseStyle.TextWidthTwips(model);
    }

    private static long MaxAttribute(XNode root, string element, string attribute) =>
        root.Descendants(element).Select(e => Str.ToLong(e[attribute])).Max() ?? 0;

    public List<XNode> Build(string markdown) => MarkdownBlocks.Parse(markdown).SelectMany(BlockNodes).ToList();

    private List<XNode> BlockNodes(MDBlock s)
    {
        switch (s.Kind)
        {
            case MDKind.Heading: return [Heading(s)];
            case MDKind.Paragraph: return [Paragraph(Style.BodyFormat, s.Inlines)];
            case MDKind.List: return [ListItem(s)];
            case MDKind.Quote: return [Quote(s)];
            case MDKind.Code: return Code(s);
            case MDKind.Caption: return [Paragraph(CaptionFormat, s.Inlines)];
            case MDKind.PageBreak:
                var p = new XNode("w:p");
                p.Add("w:r").Add("w:br", new() { ["w:type"] = "page" });
                return [p];
            case MDKind.Image: return Image(s);
            default: return Table(s);
        }
    }

    private XNode Heading(MDBlock s)
    {
        var parts = s.Inlines.ToList();
        // the template numbers its headings itself: "1.1 Aanleiding" would become "1.1 1.1 Aanleiding"
        if (Style.HeadingsNumbered && parts.Count > 0)
            parts[0] = parts[0] with { Text = LeadingNumber.Replace(parts[0].Text, _ => "") };
        var p = Paragraph(Style.Heading(s.Level), parts, plain: true);
        var start = new XNode("w:bookmarkStart", new() { ["w:id"] = _bookmarkID.ToString(), ["w:name"] = "_TocMM" + _bookmarkID.ToString("D5") });
        var end = new XNode("w:bookmarkEnd", new() { ["w:id"] = _bookmarkID.ToString() });
        _bookmarkID++;
        if (p.Find("w:pPr") is XNode ppr) p.InsertAfter(ppr, start);
        else p.Insert(start, 0);
        p.Append(end);
        return p;
    }

    private XNode ListItem(MDBlock s)
    {
        var format = (s.Ordered ? Style.Number : Style.Bullet)
            ?? new ParaFormat(Model.Styles.FindRole("list_paragraph") ?? Style.BodyFormat.StyleID, rpr: Style.BodyFormat.RPr);
        var p = Paragraph(format, s.Inlines);
        if (p.Find("w:pPr") is not XNode ppr) return p;
        var id = ListNumber(s, format);
        var numPr = new XNode("w:numPr", null, [new XNode("w:ilvl", new() { ["w:val"] = Math.Min(s.Level, 8).ToString() }), new XNode("w:numId", new() { ["w:val"] = id })]);
        OOXML.Put(ppr, numPr);
        // deeper levels: the numbering's own indent, not the example's
        if (s.Level > 0) ppr.RemoveAll("w:ind");
        return p;
    }

    /// The numbering of a list item: a numbered list starts at 1 for each Markdown list.
    private string ListNumber(MDBlock s, ParaFormat format)
    {
        if (_listNumbers.TryGetValue(s.ListID, out var known)) return known;
        var baseId = format.PPr?.Find("w:numPr")?.Find("w:numId")?.Val;
        baseId ??= Model.Styles.Chain(format.StyleID).Select(c => c.Element.Find("w:pPr")?.Find("w:numPr")?.Find("w:numId")?.Val).FirstOrDefault(v => v != null);
        string number = baseId != null && baseId != "0"
            ? (s.Ordered ? _numbering.Restart(baseId) : baseId)
            : _numbering.NewList(s.Ordered);
        _listNumbers[s.ListID] = number;
        return number;
    }

    private XNode Quote(MDBlock s)
    {
        if (Style.Quote is ParaFormat q) return Paragraph(q, s.Inlines);
        var p = Paragraph(Style.BodyFormat, s.Inlines.Select(x => x with { Italic = true }));
        if (p.Find("w:pPr") is XNode ppr) OOXML.Put(ppr, "w:ind", new() { ["w:left"] = "567" });
        return p;
    }

    private List<XNode> Code(MDBlock s)
    {
        var format = Style.Code ?? new ParaFormat(Style.BodyFormat.StyleID);
        return s.Text.Split('\n').Select(line =>
        {
            var p = Paragraph(format, line.Length == 0 ? [] : [new MDInline { Text = line, Code = true }]);
            if (p.Find("w:pPr") is XNode ppr)
            {
                OOXML.Put(ppr, "w:spacing", new() { ["w:before"] = "0", ["w:after"] = "0" });
                OOXML.Put(ppr, "w:shd", new() { ["w:val"] = "clear", ["w:color"] = "auto", ["w:fill"] = "F2F2F2" });
            }
            return p;
        }).ToList();
    }

    private ParaFormat CaptionFormat
    {
        get
        {
            if (Style.Caption is ParaFormat c) return c;
            var rpr = new XNode("w:rPr");
            OOXML.Put(rpr, "w:i");
            OOXML.Put(rpr, "w:sz", new() { ["w:val"] = "18" });
            return new ParaFormat(Model.Styles.EnsureStyle("Caption", "caption", Style.BodyFormat.StyleID, rpr: rpr));
        }
    }

    private List<XNode> Image(MDBlock s)
    {
        if (_images?.Invoke(s.Source) is not var (data, ext))
            return [Paragraph(Style.BodyFormat, [new MDInline { Text = L("[Afbeelding niet gevonden: %@]", s.Text.Length == 0 ? s.Source : s.Text), Italic = true }])];
        var p = Paragraph(Style.BodyFormat, []);
        if (p.Find("w:pPr") is XNode ppr)
        {
            OOXML.Put(ppr, "w:keepNext");
            OOXML.Put(ppr, "w:jc", new() { ["w:val"] = "center" });
        }
        p.Append(DrawingRun(data, ext, s.Text));
        if (s.Text.Length == 0) return [p];
        _figure++;
        var caption = Paragraph(CaptionFormat, [new MDInline { Text = L("Figuur") + " " }]);
        var field = caption.Add("w:fldSimple", new() { ["w:instr"] = $" SEQ {L("Figuur")} \\* ARABIC " });
        field.Add("w:r").Append(XNode.TextElement("w:t", _figure.ToString()));
        AppendInline(new MDInline { Text = ": " + s.Text }, caption, CaptionFormat.NewRPr());
        return [p, caption];
    }

    private XNode DrawingRun(byte[] data, string givenExt, string description)
    {
        var ext = givenExt.Lower();
        var name = _pkg.UniqueMediaName(ext);
        _pkg.SetData(name, data);
        _pkg.EnsureDefaultType(ext, ImageTypes.GetValueOrDefault(ext) ?? "application/octet-stream");
        var id = _pkg.AddRelationship(_pkg.MainPart, OOXML.RelImage, name["word/".Length..]);
        var (cx, cy) = ImageSize(data, WidthTwips * EmuPerTwip);
        var d = _drawingID;
        _drawingID++;
        var xmlns = OOXML.Drawing.ToDictionary(kv => "xmlns:" + kv.Key, kv => kv.Value);
        var inline = new XNode("wp:inline", new() { ["distT"] = "0", ["distB"] = "0", ["distL"] = "0", ["distR"] = "0" });
        inline.Add("wp:extent", new() { ["cx"] = cx.ToString(), ["cy"] = cy.ToString() });
        inline.Add("wp:effectExtent", new() { ["l"] = "0", ["t"] = "0", ["r"] = "0", ["b"] = "0" });
        inline.Add("wp:docPr", new() { ["id"] = d.ToString(), ["name"] = L("Afbeelding") + $" {d}", ["descr"] = description.Length == 0 ? L("Afbeelding") + $" {d}" : description });
        inline.Add("wp:cNvGraphicFramePr").Add("a:graphicFrameLocks", new() { ["noChangeAspect"] = "1" });
        var pic = inline.Add("a:graphic").Add("a:graphicData", new() { ["uri"] = "http://schemas.openxmlformats.org/drawingml/2006/picture" }).Add("pic:pic");
        var nv = pic.Add("pic:nvPicPr");
        nv.Add("pic:cNvPr", new() { ["id"] = d.ToString(), ["name"] = NSPath.LastPathComponent(name) });
        nv.Add("pic:cNvPicPr");
        var fill = pic.Add("pic:blipFill");
        fill.Add("a:blip", new() { ["r:embed"] = id });
        fill.Add("a:stretch").Add("a:fillRect");
        var sp = pic.Add("pic:spPr");
        var xfrm = sp.Add("a:xfrm");
        xfrm.Add("a:off", new() { ["x"] = "0", ["y"] = "0" });
        xfrm.Add("a:ext", new() { ["cx"] = cx.ToString(), ["cy"] = cy.ToString() });
        sp.Add("a:prstGeom", new() { ["prst"] = "rect" }).Add("a:avLst");
        var run = new XNode("w:r");
        // the template's document element need not declare these namespaces
        run.Add("w:drawing", xmlns).Append(inline);
        return run;
    }

    /// The image's size at its own resolution, at most the text width and a bit more than that in height.
    public static (int Cx, int Cy) ImageSize(byte[] data, int maxWidth)
    {
        if (ImageHeader.Read(data) is not var (w, h, resolution) || w <= 0 || h <= 0) return (maxWidth, maxWidth * 6 / 10);
        double dpi = Math.Max(resolution ?? 96, 1);
        double cx = w / dpi * 914_400, cy = h / dpi * 914_400;
        double scale = Math.Min(Math.Min(1, maxWidth / cx), maxWidth * 1.1 / cy);
        return ((int)(cx * scale), (int)(cy * scale));
    }

    private List<XNode> Table(MDBlock s)
    {
        var rows = new List<List<List<MDInline>>>();
        if (s.Header.Count > 0) rows.Add(s.Header);
        rows.AddRange(s.Rows);
        int columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        if (columns <= 0) return [];
        var example = Style.TableExample;
        var tbl = new XNode("w:tbl");
        var tblPr = example?.Find("w:tblPr")?.Copy() ?? new XNode("w:tblPr");
        if (example?.Find("w:tblPr") == null && (Model.Styles.FindRole("table_grid") ?? Model.Styles.DefaultTable) is string id)
            OOXML.Put(tblPr, "w:tblStyle", new() { ["w:val"] = id });
        OOXML.Put(tblPr, "w:tblW", new() { ["w:w"] = "5000", ["w:type"] = "pct" });
        tbl.Append(tblPr);

        int total = WidthTwips;
        int grid = example?.Find("w:tblGrid")?.FindAll("w:gridCol").Sum(c => Str.ToInt(c["w:w"] ?? "") ?? 0) ?? 0;
        if (grid > 1000) total = Math.Min(grid, total);
        var widths = ColumnWidths(rows, columns, total);
        var tblGrid = tbl.Add("w:tblGrid");
        foreach (var w in widths) tblGrid.Add("w:gridCol", new() { ["w:w"] = w.ToString() });

        var exampleRows = example?.FindAll("w:tr") ?? [];
        var headerExample = exampleRows.FirstOrDefault();
        var bodyExamples = exampleRows.Count > 1 ? exampleRows.Skip(1).ToList() : (s.Header.Count == 0 ? exampleRows : []);
        for (int ri = 0; ri < rows.Count; ri++)
        {
            bool isHeader = s.Header.Count > 0 && ri == 0;
            var rowExample = isHeader ? headerExample
                : bodyExamples.Count == 0 ? null : bodyExamples[(ri - (s.Header.Count == 0 ? 0 : 1)) % bodyExamples.Count];
            tbl.Append(TableRow(rows[ri], columns, widths, rowExample, isHeader));
        }
        // a paragraph after the table, as Word does; otherwise tables next to each other merge
        return [tbl, Paragraph(Style.BodyFormat, [])];
    }

    private XNode TableRow(List<List<MDInline>> cells, int columns, int[] widths, XNode? example, bool isHeader)
    {
        var tr = new XNode("w:tr");
        var exampleCells = example?.FindAll("w:tc") ?? [];
        if (example?.Find("w:trPr")?.Copy() is XNode trPr)
        {
            foreach (var t in new[] { "w:trHeight", "w:gridBefore", "w:gridAfter", "w:wBefore", "w:wAfter" }) trPr.RemoveAll(t);
            tr.Append(trPr);
        }
        else if (isHeader)
        {
            tr.Add("w:trPr").Add("w:tblHeader");
        }
        for (int c = 0; c < columns; c++)
        {
            var cellExample = exampleCells.Count == 0 ? null : exampleCells[Math.Min(c, exampleCells.Count - 1)];
            tr.Append(TableCell(c < cells.Count ? cells[c] : [], widths[c], cellExample, isHeader));
        }
        return tr;
    }

    private XNode TableCell(List<MDInline> inlines, int width, XNode? example, bool isHeader)
    {
        var tc = new XNode("w:tc");
        var tcPr = example?.Find("w:tcPr")?.Copy() ?? new XNode("w:tcPr");
        foreach (var t in new[] { "w:gridSpan", "w:vMerge", "w:hMerge" }) tcPr.RemoveAll(t);
        OOXML.Put(tcPr, "w:tcW", new() { ["w:w"] = width.ToString(), ["w:type"] = "dxa" });
        tc.Append(tcPr);
        var exampleP = example?.Find("w:p");
        var ppr = exampleP?.Find("w:pPr");
        var exampleRun = exampleP?.Descendants("w:r").FirstOrDefault(r => r.Has("w:t"));
        var rpr = exampleRun?.Find("w:rPr");
        if (example == null && isHeader)
        {
            rpr = new XNode("w:rPr");
            OOXML.Put(rpr, "w:b");
        }
        // a line break in a cell becomes a paragraph
        var lines = new List<List<MDInline>> { new() };
        foreach (var i in inlines)
        {
            if (i.IsBreak) lines.Add([]);
            else lines[^1].Add(i);
        }
        foreach (var line in lines)
        {
            var p = tc.Add("w:p");
            if (ppr != null) p.Append(ppr.Copy());
            foreach (var i in line) AppendInline(i, p, rpr?.Copy());
        }
        return tc;
    }

    private static int[] ColumnWidths(List<List<List<MDInline>>> rows, int columns, int total)
    {
        var lengths = Enumerable.Repeat(10, columns).ToArray();
        foreach (var r in rows)
        {
            for (int i = 0; i < r.Count && i < columns; i++)
                lengths[i] = Math.Max(lengths[i], Math.Min(r[i].Sum(x => x.Text.GraphemeCount()), 60));
        }
        int sum = lengths.Sum();
        var widths = lengths.Select(l => total * l / sum).ToArray();
        widths[columns - 1] += total - widths.Sum();
        return widths;
    }

    // MARK: - paragraphs and runs

    public XNode Paragraph(ParaFormat format, IEnumerable<MDInline> inlines, bool plain = false)
    {
        var p = new XNode("w:p");
        p.Append(format.NewPPr());
        foreach (var i in inlines) AppendInline(i, p, format.NewRPr(), plain);
        return p;
    }

    private void AppendInline(MDInline i, XNode parent, XNode? baseRPr, bool plain = false)
    {
        if (i.IsBreak)
        {
            parent.Add("w:r").Add("w:br");
            return;
        }
        if (i.Text.Length == 0) return;
        var r = Run(i.Text, baseRPr, i.Bold && !plain, i.Italic && !plain, i.Code, i.Strike);
        if (i.Link is not string link || plain) { parent.Append(r); return; }
        var h = parent.Add("w:hyperlink", new() { ["w:history"] = "1" });
        if (link.HasPrefix("#"))
        {
            h["w:anchor"] = link[1..];
        }
        else
        {
            h["r:id"] = _pkg.AddRelationship(_pkg.MainPart, OOXML.RelHyperlink, link, external: true);
            // the template's document element need not declare this namespace
            if (_pkg.Document["xmlns:r"] == null) _pkg.Document["xmlns:r"] = OOXML.Drawing["r"];
        }
        var rpr = r.Find("w:rPr");
        if (rpr == null)
        {
            rpr = new XNode("w:rPr");
            r.Insert(rpr, 0);
        }
        if (Model.Styles.FindRole("hyperlink") is string id)
        {
            OOXML.Put(rpr, "w:rStyle", new() { ["w:val"] = id });
        }
        else
        {
            OOXML.Put(rpr, "w:color", new() { ["w:val"] = "0563C1" });
            OOXML.Put(rpr, "w:u", new() { ["w:val"] = "single" });
        }
        h.Append(r);
    }

    public static XNode Run(string text, XNode? baseRPr, bool bold = false, bool italic = false, bool code = false, bool strike = false)
    {
        var r = new XNode("w:r");
        var rpr = baseRPr ?? new XNode("w:rPr");
        if (bold) OOXML.Put(rpr, "w:b");
        if (italic) OOXML.Put(rpr, "w:i");
        if (strike) OOXML.Put(rpr, "w:strike");
        if (code) OOXML.Put(rpr, "w:rFonts", new() { ["w:ascii"] = "Consolas", ["w:hAnsi"] = "Consolas", ["w:cs"] = "Consolas" });
        if (rpr.Elements.Count > 0) r.Append(rpr);
        var parts = text.Split('\t');
        for (int n = 0; n < parts.Length; n++)
        {
            if (n > 0) r.Add("w:tab");
            if (parts[n].Length > 0) r.Append(XNode.TextElement("w:t", parts[n]));
        }
        return r;
    }
}

/// Adds to numbering.xml, and creates it for a template without lists.
public sealed class NumberingWriter
{
    private readonly DocxPackage _pkg;
    private string? _part;

    public NumberingWriter(DocxPackage pkg)
    {
        _pkg = pkg;
        _part = pkg.RelatedPart(OOXML.RelNumbering);
    }

    private XNode Root()
    {
        if (_part != null && _pkg.Xml(_part) is XNode found) return found;
        const string name = "word/numbering.xml";
        var x = new XNode("w:numbering", new() { ["xmlns:w"] = OOXML.WordNamespace });
        _pkg.SetXml(name, x);
        _pkg.AddRelationship(_pkg.MainPart, OOXML.RelNumbering, "numbering.xml");
        _pkg.EnsureOverrideType(name, OOXML.CtNumbering);
        _part = name;
        return x;
    }

    private static long Next(List<XNode> nodes, string attribute) => (nodes.Select(n => Str.ToLong(n[attribute])).Max() ?? 0) + 1;

    public string Restart(string id)
    {
        var r = Root();
        var nums = r.FindAll("w:num");
        if (nums.FirstOrDefault(n => n["w:numId"] == id)?.Find("w:abstractNumId")?.Val is not string abstractNum) return id;
        var created = Next(nums, "w:numId").ToString();
        var num = r.Add("w:num", new() { ["w:numId"] = created });
        num.Add("w:abstractNumId", new() { ["w:val"] = abstractNum });
        num.Add("w:lvlOverride", new() { ["w:ilvl"] = "0" }).Add("w:startOverride", new() { ["w:val"] = "1" });
        return created;
    }

    public string NewList(bool ordered)
    {
        var r = Root();
        var abstracts = r.FindAll("w:abstractNum");
        var abstractID = Next(abstracts, "w:abstractNumId").ToString();
        var a = new XNode("w:abstractNum", new() { ["w:abstractNumId"] = abstractID });
        a.Add("w:multiLevelType", new() { ["w:val"] = "hybridMultilevel" });
        string[] bullets = ["•", "◦", "▪"], formats = ["decimal", "lowerLetter", "lowerRoman"];
        for (int i = 0; i < 9; i++)
        {
            var l = a.Add("w:lvl", new() { ["w:ilvl"] = i.ToString() });
            l.Add("w:start", new() { ["w:val"] = "1" });
            l.Add("w:numFmt", new() { ["w:val"] = ordered ? formats[i % 3] : "bullet" });
            l.Add("w:lvlText", new() { ["w:val"] = ordered ? $"%{i + 1}." : bullets[i % 3] });
            l.Add("w:lvlJc", new() { ["w:val"] = "left" });
            l.Add("w:pPr").Add("w:ind", new() { ["w:left"] = (360 * (i + 1)).ToString(), ["w:hanging"] = "360" });
        }
        // abstract numberings come before the numberings
        if (abstracts.Count > 0) r.InsertAfter(abstracts[^1], a);
        else if (r.Find("w:num") is XNode first) r.InsertBefore(first, a);
        else r.Append(a);
        var id = Next(r.FindAll("w:num"), "w:numId").ToString();
        r.Add("w:num", new() { ["w:numId"] = id }).Add("w:abstractNumId", new() { ["w:val"] = abstractID });
        return id;
    }
}

/// Pixel size and resolution from the file's own header, as CGImageSource gives them on the Mac. Other kinds of image
/// (SVG, EMF, WMF) give null and take the default size.
internal static class ImageHeader
{
    public static (double Width, double Height, double? Dpi)? Read(byte[] d)
    {
        if (d.Length >= 24 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return Png(d);
        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8) return Jpeg(d);
        if (d.Length >= 10 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F') return (d[6] | d[7] << 8, d[8] | d[9] << 8, null);
        if (d.Length >= 26 && d[0] == 'B' && d[1] == 'M') return Bmp(d);
        if (d.Length >= 8 && ((d[0] == 'I' && d[1] == 'I' && d[2] == 42) || (d[0] == 'M' && d[1] == 'M' && d[3] == 42)))
        {
            var t = Tiff(d, 0, d.Length);
            return t is { Width: > 0, Height: > 0 } ? (t.Width, t.Height, t.Dpi) : null;
        }
        return null;
    }

    private static uint Be32(byte[] d, int at) => (uint)(d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3]);
    private static int Be16(byte[] d, int at) => d[at] << 8 | d[at + 1];

    private static (double, double, double?)? Png(byte[] d)
    {
        double w = Be32(d, 16), h = Be32(d, 20);
        double? dpi = null;
        // pHYs comes before the image data; the unit 1 is the metre
        for (long at = 8; at + 12 <= d.Length;)
        {
            uint length = Be32(d, (int)at);
            var type = System.Text.Encoding.ASCII.GetString(d, (int)at + 4, 4);
            if (type is "IDAT" or "IEND") break;
            if (type == "pHYs" && length >= 9 && at + 8 + 9 <= d.Length)
            {
                uint perMetre = Be32(d, (int)at + 8);
                if (d[at + 16] == 1 && perMetre > 0) dpi = perMetre * 0.0254;
                break;
            }
            at += 12L + length;
        }
        return (w, h, dpi);
    }

    private static (double, double, double?)? Jpeg(byte[] d)
    {
        double? jfif = null, exif = null;
        int at = 2;
        while (at + 4 <= d.Length)
        {
            if (d[at] != 0xFF) { at++; continue; }
            byte marker = d[at + 1];
            if (marker == 0xFF) { at++; continue; }
            if (marker is 0x00 or 0x01 or (>= 0xD0 and <= 0xD9)) { at += 2; continue; }
            int length = Be16(d, at + 2);
            if (length < 2) return null;
            int body = at + 4, size = Math.Min(length - 2, d.Length - body);
            // every start-of-frame marker: not DHT (C4), JPG (C8) or DAC (CC)
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (size < 5) return null;
                return (Be16(d, body + 3), Be16(d, body + 1), jfif ?? exif);
            }
            if (marker == 0xE0 && size >= 12 && d[body] == 'J' && d[body + 1] == 'F' && d[body + 2] == 'I' && d[body + 3] == 'F' && d[body + 4] == 0)
            {
                int density = Be16(d, body + 8);
                if (density > 0) jfif = d[body + 7] switch { 1 => density, 2 => density * 2.54, _ => jfif };
            }
            else if (marker == 0xE1 && size >= 14 && d[body] == 'E' && d[body + 1] == 'x' && d[body + 2] == 'i' && d[body + 3] == 'f')
            {
                exif = Tiff(d, body + 6, size - 6).Dpi;
            }
            if (marker == 0xDA) return null;   // the image data, and no size yet
            at = body + length - 2;
        }
        return null;
    }

    private static (double, double, double?)? Bmp(byte[] d)
    {
        uint header = (uint)(d[14] | d[15] << 8 | d[16] << 16 | d[17] << 24);
        if (header == 12) return (d[18] | d[19] << 8, d[20] | d[21] << 8, null);
        if (d.Length < 26) return null;
        int w = d[18] | d[19] << 8 | d[20] << 16 | d[21] << 24, h = d[22] | d[23] << 8 | d[24] << 16 | d[25] << 24;
        return (w, Math.Abs((double)h), null);
    }

    /// The first directory of a TIFF structure, also what Exif in a JPEG consists of.
    private static (double Width, double Height, double? Dpi) Tiff(byte[] d, int start, int length)
    {
        (double, double, double?) none = (0, 0, null);
        if (length < 8) return none;
        bool little = d[start] == 'I';
        int U16(int at) => little ? d[start + at] | d[start + at + 1] << 8 : Be16(d, start + at);
        long U32(int at) => little
            ? d[start + at] | (long)d[start + at + 1] << 8 | (long)d[start + at + 2] << 16 | (long)d[start + at + 3] << 24
            : Be32(d, start + at);

        long ifd = U32(4);
        if (ifd + 2 > length) return none;
        double width = 0, height = 0, resolution = 0;
        int unit = 2;
        int count = U16((int)ifd);
        for (int i = 0; i < count; i++)
        {
            int entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > length) break;
            int tag = U16(entry), type = U16(entry + 2);
            long Value() => type == 3 ? U16(entry + 8) : U32(entry + 8);
            switch (tag)
            {
                case 0x0100: width = Value(); break;
                case 0x0101: height = Value(); break;
                case 0x0128: unit = (int)Value(); break;
                case 0x011A:
                    long offset = U32(entry + 8);
                    if (type == 5 && offset + 8 <= length)
                    {
                        long denominator = U32((int)offset + 4);
                        if (denominator > 0) resolution = (double)U32((int)offset) / denominator;
                    }
                    break;
            }
        }
        double? dpi = resolution <= 0 ? null : unit switch { 2 => resolution, 3 => resolution * 2.54, _ => null };
        return (width, height, dpi);
    }
}
