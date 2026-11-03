using System.Text.RegularExpressions;

namespace WinAIMagic.Documents;

/// Which style means what, in any Word language: a built-in style keeps its English name ("heading 1") but gets a
/// translated id ("Kop1" in Dutch Word), and house styles add their own, based on those or with an outline level.
public sealed class StyleSheet
{
    public sealed record Style(string Id, string Name, string Type, string? BasedOn, int? OutlineLevel, bool HasNumbering, XNode Element);

    private static readonly Rx HeadingName = new("^(heading|kop|titre|überschrift|uberschrift|encabezado|titolo|rubrik|overskrift|nagłówek)\\s*(\\d)$", RegexOptions.IgnoreCase);
    private static readonly Rx TocName = new("^(toc|inhopg|inhoudsopgave|tdm|verzeichnis|tm)\\s*(\\d)$", RegexOptions.IgnoreCase);
    private static readonly Dictionary<string, string[]> Roles = new()
    {
        ["title"] = ["title"], ["subtitle"] = ["subtitle"], ["list_bullet"] = ["list bullet"], ["list_number"] = ["list number"],
        ["list_paragraph"] = ["list paragraph"], ["caption"] = ["caption"], ["quote"] = ["quote", "intense quote"],
        ["code"] = ["html preformatted", "plain text", "macro text"], ["table_grid"] = ["table grid"], ["hyperlink"] = ["hyperlink"],
    };

    private readonly DocxPackage _pkg;
    private readonly string _part;
    private readonly List<Style> _ordered = [];
    public Dictionary<string, Style> Styles { get; } = [];
    public string? DefaultParagraph { get; private set; }
    public string? DefaultTable { get; private set; }

    public StyleSheet(DocxPackage pkg)
    {
        _pkg = pkg;
        _part = pkg.RelatedPart(OOXML.RelStyles) ?? "word/styles.xml";
        foreach (var s in pkg.Xml(_part)?.FindAll("w:style") ?? [])
        {
            if (s["w:styleId"] is not string id) continue;
            var type = s["w:type"] ?? "paragraph";
            var ppr = s.Find("w:pPr");
            var style = new Style(id, s.Find("w:name")?.Val ?? id, type, s.Find("w:basedOn")?.Val,
                                  Str.ToInt(ppr?.Find("w:outlineLvl")?.Val), ppr?.Has("w:numPr") == true, s);
            Add(style);
            if (s["w:default"] is "1" or "true")
            {
                if (type == "paragraph") DefaultParagraph = id;
                else if (type == "table") DefaultTable = id;
            }
        }
    }

    private void Add(Style s)
    {
        Styles[s.Id] = s;
        _ordered.Add(s);
    }

    public List<Style> Chain(string? id)
    {
        var output = new List<Style>();
        var seen = new HashSet<string>();
        var next = id;
        while (next != null && !seen.Contains(next) && Styles.TryGetValue(next, out var s))
        {
            seen.Add(next);
            output.Add(s);
            next = s.BasedOn;
        }
        return output;
    }

    private static int? Number(Rx rx, string s) => Str.ToInt(rx.First(s.TrimWS())?.Groups[1]);

    /// The heading level (from 1) of a paragraph style, or null.
    public int? HeadingLevel(string? id)
    {
        foreach (var s in Chain(id))
        {
            if ((Number(HeadingName, s.Name) ?? Number(HeadingName, s.Id)) is int n) return n;
            if (s.OutlineLevel is int o && o < 9) return o + 1;
            if (s.Name.Lower() is "title" or "subtitle") return null;
        }
        return null;
    }

    public int? TocLevel(string? id)
    {
        if (id == null || !Styles.TryGetValue(id, out var s)) return null;
        return Number(TocName, s.Name) ?? Number(TocName, s.Id);
    }

    public bool HasRole(string? id, string role)
    {
        var names = Roles.GetValueOrDefault(role) ?? [];
        return Chain(id).Any(s => names.Contains(s.Name.Lower()));
    }

    public bool IsNumbered(string? id) => Chain(id).Any(s => s.HasNumbering);

    public string? FindRole(string role)
    {
        foreach (var name in Roles.GetValueOrDefault(role) ?? [])
        {
            if (_ordered.FirstOrDefault(s => s.Name.Lower() == name) is Style s) return s.Id;
        }
        return null;
    }

    public string? FindHeading(int level)
    {
        string? best = null;
        foreach (var s in _ordered.Where(s => s.Type == "paragraph"))
        {
            if (Number(HeadingName, s.Name) == level) return s.Id;
            if (best == null && HeadingLevel(s.Id) == level) best = s.Id;
        }
        return best;
    }

    public string? FindTOC(int level) => _ordered.FirstOrDefault(s => Number(TocName, s.Name) == level)?.Id;

    /// A minimal style when the template lacks one (for example for captions).
    public string EnsureStyle(string id, string name, string? basedOn = null, XNode? ppr = null, XNode? rpr = null)
    {
        if (Styles.ContainsKey(id)) return id;
        if (_pkg.Xml(_part) is not XNode root) return id;
        var s = root.Add("w:style", new() { ["w:type"] = "paragraph", ["w:styleId"] = id });
        s.Add("w:name", new() { ["w:val"] = name });
        if (basedOn != null) s.Add("w:basedOn", new() { ["w:val"] = basedOn });
        s.Add("w:qFormat");
        if (ppr != null) s.Append(ppr);
        if (rpr != null) s.Append(rpr);
        Add(new Style(id, name, "paragraph", basedOn, null, false, s));
        return id;
    }
}

/// Just enough of numbering.xml: is a list a bullet list or a numbered one?
public sealed class NumberingReader
{
    private readonly Dictionary<string, string> _formats = [];

    public NumberingReader(DocxPackage pkg)
    {
        if (pkg.RelatedPart(OOXML.RelNumbering) is not string part || pkg.Xml(part) is not XNode root) return;
        var abstractNums = new Dictionary<string, Dictionary<int, string>>();
        foreach (var a in root.FindAll("w:abstractNum"))
        {
            var levels = new Dictionary<int, string>();
            foreach (var l in a.FindAll("w:lvl")) levels[Str.ToInt(l["w:ilvl"] ?? "0") ?? 0] = l.Find("w:numFmt")?.Val ?? "decimal";
            abstractNums[a["w:abstractNumId"] ?? ""] = levels;
        }
        foreach (var n in root.FindAll("w:num"))
        {
            if (!abstractNums.TryGetValue(n.Find("w:abstractNumId")?.Val ?? "", out var levels)) continue;
            foreach (var (level, format) in levels) _formats[$"{n["w:numId"] ?? ""}/{level}"] = format;
        }
    }

    public (int Level, bool Ordered)? Numbering(XNode p, StyleSheet styles)
    {
        var ppr = p.Find("w:pPr");
        var numPr = ppr?.Find("w:numPr");
        numPr ??= styles.Chain(ppr?.Find("w:pStyle")?.Val).FirstOrDefault(s => s.HasNumbering)?.Element.Find("w:pPr")?.Find("w:numPr");
        if (numPr?.Find("w:numId")?.Val is not string id || id == "0") return null;
        int level = Str.ToInt(numPr.Find("w:ilvl")?.Val ?? "0") ?? 0;
        var format = _formats.GetValueOrDefault($"{id}/{level}") ?? "bullet";
        return (level, format != "bullet" && format != "none");
    }
}

public record struct Span(string Text)
{
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Code { get; set; }
    public string? Link { get; set; }

    public readonly bool SameFormat(Span o) => Bold == o.Bold && Italic == o.Italic && Code == o.Code && Link == o.Link;
}

public enum BlockKind { Heading, Paragraph, List, Table, Toc, Image, PageBreak, Section, Other }

/// One thing in the body, with a pointer to its real XML: later steps work on the document itself, not on a copy.
public sealed class Block(BlockKind kind, XNode element, string? styleID = null)
{
    public BlockKind Kind { get; set; } = kind;
    public XNode Element { get; } = element;
    public string? StyleID { get; set; } = styleID;
    public int Level { get; set; }
    public bool Ordered { get; set; }
    public List<Span> Spans { get; set; } = [];
    public List<List<string>> Rows { get; set; } = [];
    public List<string> Images { get; set; } = [];
    public bool HasSectionBreak { get; set; }

    public string Text => Kind == BlockKind.Table
        ? string.Join("\n", Rows.Select(r => string.Join(" | ", r)))
        : string.Concat(Spans.Select(s => s.Text));

    public bool IsBlank => Text.TrimWSNL().Length == 0;
}

/// `Start`: the heading's place in `Blocks`; `End`: where the section ends (with its subsections).
public sealed record OutlineNode(Block Block, string Title, int Level, string Number, int Start, int End);

/// A .docx read into blocks.
public sealed class DocModel
{
    public DocxPackage Pkg { get; }
    public StyleSheet Styles { get; }
    public NumberingReader Numbering { get; }
    public List<Block> Blocks { get; } = [];

    public DocModel(DocxPackage pkg)
    {
        Pkg = pkg;
        Styles = new StyleSheet(pkg);
        Numbering = new NumberingReader(pkg);
        foreach (var child in pkg.Body.Elements)
        {
            switch (child.Name)
            {
                case "w:p": Blocks.Add(Paragraph(child)); break;
                case "w:tbl": Blocks.Add(Table(child)); break;
                case "w:sdt": Blocks.Add(ContentControl(child)); break;
                case "w:sectPr": Blocks.Add(new Block(BlockKind.Section, child)); break;
                default: Blocks.Add(new Block(BlockKind.Other, child)); break;
            }
        }
    }

    private static readonly Rx TocInstruction = new("\\bTOC\\b");

    private Block Paragraph(XNode p)
    {
        var ppr = p.Find("w:pPr");
        var id = ppr?.Find("w:pStyle")?.Val ?? Styles.DefaultParagraph;
        var b = new Block(BlockKind.Paragraph, p, id);
        (b.Spans, b.Images) = Spans(p);
        b.HasSectionBreak = ppr?.Has("w:sectPr") == true;
        if (p.Descendants("w:instrText").Any(e => TocInstruction.Test(e.Text)) || Styles.TocLevel(id) != null)
        {
            b.Kind = BlockKind.Toc;
            return b;
        }
        var level = Styles.HeadingLevel(id);
        if (level == null && Str.ToInt(ppr?.Find("w:outlineLvl")?.Val) is int o && o < 9) level = o + 1;
        if (level is int found && !b.IsBlank)
        {
            b.Kind = BlockKind.Heading;
            b.Level = found;
            return b;
        }
        if (Numbering.Numbering(p, Styles) is var (numberLevel, ordered) && !b.IsBlank)
        {
            b.Kind = BlockKind.List;
            b.Level = numberLevel;
            b.Ordered = ordered;
            return b;
        }
        if (b.IsBlank)
        {
            if (b.Images.Count > 0) b.Kind = BlockKind.Image;
            else if (p.Descendants("w:br").Any(e => e["w:type"] == "page")) b.Kind = BlockKind.PageBreak;
        }
        return b;
    }

    private (List<Span>, List<string>) Spans(XNode p)
    {
        var output = new List<Span>();
        var images = new List<string>();
        void Add(Span s)
        {
            if (output.Count > 0 && output[^1].SameFormat(s)) output[^1] = output[^1] with { Text = output[^1].Text + s.Text };
            else output.Add(s);
        }
        void Walk(XNode node, string? link)
        {
            foreach (var c in node.Elements)
            {
                switch (c.Name)
                {
                    case "w:r":
                        var rpr = c.Find("w:rPr");
                        bool code = rpr?.Find("w:rStyle")?.Val?.Lower().Contains("code") == true;
                        var template = new Span("") { Bold = IsOn(rpr, "w:b"), Italic = IsOn(rpr, "w:i"), Code = code, Link = link };
                        void Text(string s) => Add(template with { Text = s });
                        foreach (var t in c.Elements)
                        {
                            switch (t.Name)
                            {
                                case "w:t": Text(t.Text); break;
                                case "w:tab": Text("\t"); break;
                                case "w:br" or "w:cr": if (t["w:type"] != "page") Text("\n"); break;
                                case "w:noBreakHyphen": Text("-"); break;
                                case "w:drawing" or "w:pict":
                                    t.Walk(e =>
                                    {
                                        var id = e.Name == "a:blip" ? e["r:embed"] : e.Name == "v:imagedata" ? e["r:id"] : null;
                                        if (id != null && Pkg.PartForRelationship(Pkg.MainPart, id) is string part) images.Add(part);
                                    });
                                    break;
                            }
                        }
                        break;
                    case "w:hyperlink":
                        var target = link;
                        if (c["r:id"] is string rid) target = Pkg.Relationship(Pkg.MainPart, rid)?.Target;
                        else if (c["w:anchor"] is string a) target = "#" + a;
                        Walk(c, target);
                        break;
                    case "w:ins" or "w:smartTag" or "w:customXml" or "w:fldSimple" or "w:sdt" or "w:sdtContent":
                        Walk(c, link);
                        break;
                    default:
                        break;   // w:del, bookmarks and the like carry no visible text
                }
            }
        }
        Walk(p, null);
        return (output, images);
    }

    private static bool IsOn(XNode? rpr, string name)
    {
        if (rpr?.Find(name) is not XNode e) return false;
        return (e.Val ?? "1") is not ("0" or "false" or "off");
    }

    private Block Table(XNode tbl)
    {
        var b = new Block(BlockKind.Table, tbl, tbl.Find("w:tblPr")?.Find("w:tblStyle")?.Val);
        b.Rows = tbl.FindAll("w:tr").Select(tr =>
            tr.FindAll("w:tc").Select(tc =>
                string.Join("\n", tc.Descendants("w:p").Select(p => string.Concat(Spans(p).Item1.Select(s => s.Text)))).TrimWSNL()
            ).ToList()
        ).ToList();
        return b;
    }

    private Block ContentControl(XNode sdt)
    {
        var gallery = sdt.Find("w:sdtPr")?.Find("w:docPartObj")?.Find("w:docPartGallery")?.Val ?? "";
        if (gallery.Contains("Table of Contents") || sdt.Descendants("w:instrText").Any(e => e.Text.Contains("TOC")))
            return new Block(BlockKind.Toc, sdt);
        var b = new Block(BlockKind.Other, sdt);
        foreach (var p in sdt.Find("w:sdtContent")?.FindAll("w:p") ?? [])
        {
            if (b.Spans.Count > 0) b.Spans.Add(new Span("\n"));
            b.Spans.AddRange(Spans(p).Item1);
        }
        return b;
    }

    /// Word's numbering of the headings, and where each section ends.
    public List<OutlineNode> Outline()
    {
        var nodes = new List<OutlineNode>();
        var counters = new int[10];
        var heads = Blocks.Select((b, i) => (Offset: i, Block: b)).Where(x => x.Block.Kind == BlockKind.Heading).ToList();
        for (int n = 0; n < heads.Count; n++)
        {
            var b = heads[n].Block;
            counters[b.Level - 1]++;
            for (int k = b.Level; k < 10; k++) counters[k] = 0;
            var number = string.Join(".", counters.Take(b.Level).Where(c => c > 0));
            int end = Blocks.Count;
            for (int m = n + 1; m < heads.Count; m++)
            {
                if (heads[m].Block.Level <= b.Level) { end = heads[m].Offset; break; }
            }
            nodes.Add(new OutlineNode(b, b.Text.TrimWSNL(), b.Level, number.Length == 0 ? counters[0].ToString() : number, heads[n].Offset, end));
        }
        return nodes;
    }

    /// Where the body starts: the first real heading after the cover and the table of contents.
    public int BodyStart
    {
        get
        {
            int i = Blocks.FindIndex(b => b.Kind == BlockKind.Heading);
            if (i >= 0) return i;
            return Blocks.FindLastIndex(b => b.Kind == BlockKind.Toc || b.HasSectionBreak) + 1;
        }
    }
}

/// The Markdown form of what was read.
public static class DocxMarkdown
{
    public static string? Block(Block b)
    {
        switch (b.Kind)
        {
            case BlockKind.Heading:
                return new string('#', Math.Min(b.Level, 6)) + " " + Spans(b.Spans).TrimWSNL();
            case BlockKind.Paragraph:
                var t = Spans(b.Spans).TrimWSNL();
                return t.Length == 0 ? null : t;
            case BlockKind.List:
                return string.Concat(Enumerable.Repeat("  ", b.Level)) + (b.Ordered ? "1. " : "- ") + Spans(b.Spans).TrimWSNL();
            case BlockKind.Table:
                return Table(b.Rows);
            case BlockKind.Image:
                return string.Join("\n", b.Images.Select(i => $"![]({i})"));
            case BlockKind.Other:
                return b.IsBlank ? null : b.Text.TrimWSNL();
            default:
                return null;
        }
    }

    public static string Spans(IEnumerable<Span> spans)
    {
        var output = "";
        foreach (var s in spans)
        {
            if (s.Text.Length == 0) continue;
            var core = s.Text.TrimWSNL();
            if (core.Length == 0) { output += s.Text; continue; }
            var characters = s.Text.Graphemes();
            var lead = string.Concat(characters.TakeWhile(Str.CharIsWhitespace));
            var trail = string.Concat(characters.Reverse().TakeWhile(Str.CharIsWhitespace).Reverse());
            var t = core;
            if (s.Code) t = $"`{core}`";
            else if (s.Bold && s.Italic) t = $"***{core}***";
            else if (s.Bold) t = $"**{core}**";
            else if (s.Italic) t = $"*{core}*";
            if (s.Link is string link && !link.HasPrefix("#")) t = $"[{t}]({link})";
            output += lead + t + trail;
        }
        return output.Replace("\t", " ");
    }

    public static string Table(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        int width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        if (width <= 0) return "";
        var cells = rows.Select(row =>
            row.Select(c => c.Replace("\n", "<br>").Replace("|", "\\|")).Concat(Enumerable.Repeat("", width - row.Count)).ToList()
        ).ToList();
        var lines = new List<string> { "| " + string.Join(" | ", cells[0]) + " |", "|" + string.Concat(Enumerable.Repeat("---|", width)) };
        lines.AddRange(cells.Skip(1).Select(c => "| " + string.Join(" | ", c) + " |"));
        return string.Join("\n", lines);
    }
}

/// A paragraph's look, cloned from an example.
public sealed class ParaFormat(string? styleID, XNode? ppr = null, XNode? rpr = null)
{
    public string? StyleID { get; set; } = styleID;
    public XNode? PPr { get; set; } = ppr;
    public XNode? RPr { get; set; } = rpr;

    public XNode NewPPr()
    {
        var p = PPr?.Copy() ?? new XNode("w:pPr");
        p.RemoveAll("w:sectPr");
        p.RemoveAll("w:pPrChange");
        if (StyleID != null) OOXML.Put(p, "w:pStyle", new() { ["w:val"] = StyleID });
        return p;
    }

    public XNode? NewRPr() => RPr?.Copy();
}

/// The house style learned from the document's own examples. Templates are rarely clean: some use styles, others
/// formatting by hand. So each role (heading, body, list, table) takes its look from what the template really uses,
/// and new text looks as if the template's author wrote it.
public sealed class HouseStyle
{
    public DocModel Model { get; }
    public Dictionary<int, ParaFormat> Headings { get; } = [];
    public ParaFormat BodyFormat { get; private set; } = new(null);
    public ParaFormat? Bullet { get; private set; }
    public ParaFormat? Number { get; private set; }
    public ParaFormat? Quote { get; private set; }
    public ParaFormat? Caption { get; private set; }
    public ParaFormat? Code { get; private set; }
    public XNode? TableExample { get; private set; }
    public bool HeadingsNumbered { get; private set; }

    private static readonly Rx Guidance = new("^\\s*(\\[.*\\]|<<.*>>|\\{\\{.*\\}\\}|<.*>)\\s*$", RegexOptions.Singleline);
    private static readonly HashSet<string> PlaceholderColors = ["808080", "7F7F7F", "A6A6A6", "BFBFBF", "FF0000", "C00000", "0070C0", "999999", "595959"];
    private static readonly string[] RprStrip = ["w:b", "w:bCs", "w:i", "w:iCs", "w:highlight", "w:vanish", "w:strike", "w:rStyle", "w:lang", "w:noProof", "w:webHidden", "w:u"];

    /// Instruction text of a template such as "[Beschrijf hier ...]", which must not end up in the result.
    public static bool IsGuidance(Block b)
    {
        var t = b.Text.TrimWSNL();
        if (t.Length == 0) return false;
        if (Guidance.Test(t)) return true;
        var runs = b.Element.Descendants("w:r").Where(r => r.AllText.TrimWSNL().Length > 0).ToList();
        return runs.Count > 0 && runs.All(RunIsPlaceholder);
    }

    private static bool RunIsPlaceholder(XNode r)
    {
        if (r.Find("w:rPr") is not XNode rpr) return false;
        var style = rpr.Find("w:rStyle")?.Val?.Lower() ?? "";
        if (style.Contains("placeholder") || style.Contains("tijdelijke")) return true;
        if (rpr.Has("w:highlight")) return true;
        return rpr.Has("w:i") && PlaceholderColors.Contains((rpr.Find("w:color")?.Val ?? "").ToUpperInvariant());
    }

    public HouseStyle(DocModel model)
    {
        Model = model;
        var st = model.Styles;
        var blocks = model.Blocks;
        var region = blocks.Skip(Math.Max(blocks.FindIndex(b => b.Kind == BlockKind.Heading), 0)).ToList();

        for (int level = 1; level <= 9; level++)
        {
            var found = region.Where(b => b.Kind == BlockKind.Heading && b.Level == level).ToList();
            if (found.Count > 0) Headings[level] = Format(found);
            else if (st.FindHeading(level) is string id) Headings[level] = new ParaFormat(id);
        }
        var paragraphs = region.Where(b => b.Kind == BlockKind.Paragraph && !b.IsBlank && !IsGuidance(b)).ToList();
        if (paragraphs.Count == 0)
        {
            paragraphs = blocks.Where(b =>
                b.Kind == BlockKind.Paragraph && !b.IsBlank && !IsGuidance(b) && st.HeadingLevel(b.StyleID) == null
                    && !st.HasRole(b.StyleID, "title") && !st.HasRole(b.StyleID, "subtitle")).ToList();
        }
        BodyFormat = paragraphs.Count == 0 ? new ParaFormat(st.DefaultParagraph) : Format(paragraphs);
        // the body's example must not bring list numbering or keep-with-next along
        foreach (var t in new[] { "w:numPr", "w:keepNext", "w:pageBreakBefore", "w:outlineLvl" }) BodyFormat.PPr?.RemoveAll(t);

        var bullets = blocks.Where(b => b.Kind == BlockKind.List && !b.Ordered && !IsGuidance(b)).ToList();
        var numbers = blocks.Where(b => b.Kind == BlockKind.List && b.Ordered && !IsGuidance(b)).ToList();
        Bullet = bullets.Count == 0 ? Role("list_bullet") : Format(bullets);
        Number = numbers.Count == 0 ? Role("list_number") : Format(numbers);
        Quote = Role("quote");
        Caption = Role("caption");
        Code = Role("code");
        TableExample = (region.FirstOrDefault(b => b.Kind == BlockKind.Table) ?? blocks.FirstOrDefault(b => b.Kind == BlockKind.Table))?.Element;
        HeadingsNumbered = Headings.Values.Any(f => f.PPr?.Has("w:numPr") == true || st.IsNumbered(f.StyleID));
    }

    private ParaFormat? Role(string name) => Model.Styles.FindRole(name) is string id ? new ParaFormat(id) : null;

    public ParaFormat Heading(int level)
    {
        if (Headings.TryGetValue(level, out var f)) return f;
        // deeper than the template defines: the look of the deepest level it has
        var lower = Headings.Keys.Where(k => k < level).ToList();
        if (lower.Count > 0) return Headings[lower.Max()];
        return new ParaFormat(Model.Styles.FindHeading(level));
    }

    private static ParaFormat Format(List<Block> blocks)
    {
        var counts = new Dictionary<string, int>();
        foreach (var b in blocks) counts[b.StyleID ?? ""] = counts.GetValueOrDefault(b.StyleID ?? "") + 1;
        // the most used style, the first in the document on a tie
        var best = "";
        if (blocks.Count > 0)
        {
            best = blocks[0].StyleID ?? "";
            foreach (var b in blocks.Skip(1))
            {
                if (counts[best] < counts[b.StyleID ?? ""]) best = b.StyleID ?? "";
            }
        }
        var same = blocks.Where(b => (b.StyleID ?? "") == best).ToList();
        return new ParaFormat(best.Length == 0 ? null : best, same.FirstOrDefault()?.Element.Find("w:pPr")?.Copy(), DominantRPr(same));
    }

    /// Formatting by hand shared by most of the text (say Arial 10 applied to everything).
    private static XNode? DominantRPr(List<Block> blocks)
    {
        var weights = new Dictionary<string, int>();
        var samples = new Dictionary<string, XNode>();
        int total = 0;
        foreach (var b in blocks)
        {
            foreach (var r in b.Element.Descendants("w:r"))
            {
                int n = r.FindAll("w:t").Sum(t => t.Text.Length);
                if (n <= 0) continue;
                total += n;
                var key = "";
                if (r.Find("w:rPr")?.Copy() is XNode rpr)
                {
                    foreach (var name in RprStrip) rpr.RemoveAll(name);
                    if (rpr.Elements.Count > 0)
                    {
                        key = rpr.Signature;
                        samples.TryAdd(key, rpr);
                    }
                }
                weights[key] = weights.GetValueOrDefault(key) + n;
            }
        }
        if (total <= 0 || weights.Count == 0) return null;
        var best = weights.MaxBy(kv => kv.Value);
        if (best.Key.Length == 0 || (double)best.Value / total < 0.6) return null;
        return samples.GetValueOrDefault(best.Key);
    }

    public static int TextWidthTwips(DocModel model)
    {
        var sect = model.Pkg.Body.Find("w:sectPr");
        if (sect != null && Str.ToInt(sect.Find("w:pgSz")?["w:w"]) is int w && sect.Find("w:pgMar") is XNode mar
            && Str.ToInt(mar["w:left"]) is int left && Str.ToInt(mar["w:right"]) is int right)
        {
            return w - left - right;
        }
        return 9026;   // A4 with 2.54 cm margins
    }
}
