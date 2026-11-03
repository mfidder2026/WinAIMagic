using System.Globalization;
using System.Text.RegularExpressions;

namespace WinAIMagic.Documents;

/// The variable data of a house-style template: {{veld}}, <<veld>> and «veld» anywhere, [Veld] on the cover, content
/// controls by their name, custom document properties with their DOCPROPERTY fields, TITLE and the like, and cover
/// lines such as "Betreft<tab>:<tab>...".
public static class DocxFields
{
    private static readonly Rx Braces = new("\\{\\{\\s*([^{}]{1,60}?)\\s*\\}\\}|<<\\s*([^<>]{1,60}?)\\s*>>|«\\s*([^«»]{1,60}?)\\s*»");
    private static readonly Rx Brackets = new("\\[([^\\[\\]]{2,60})\\]");
    private static readonly Rx LabelLine = new("^\\s*([^\\t:]{2,40}?)\\s*\\t*:\\s*\\t*(.*)$", RegexOptions.Singleline);
    private static readonly Rx NoiseProperties = new("^(order|xd_|compliance|templateurl|triggerflow|mediaservice|contenttype|_|msip_|taxcatchall|lcf76f155ced4ddcb4097134ff3c332f)", RegexOptions.IgnoreCase);
    private static readonly string[] CoreFields = ["TITLE", "AUTHOR", "SUBJECT", "KEYWORDS", "LASTSAVEDBY"];
    private const string CustomPart = "docProps/custom.xml";
    private const string CorePart = "docProps/core.xml";
    private static readonly Rx NotKey = new("[^a-z0-9]+");
    private static readonly Rx OtherParts = new("^word/(header|footer|footnotes|endnotes)\\d*\\.xml$");

    public sealed record Placeholder(string Key, string Label, string Source, string Current);

    public static string Normalized(string s) => NotKey.Replace(s.Lower(), _ => "");

    /// The main document, headers, footers, footnotes and endnotes.
    public static List<string> TextParts(DocxPackage pkg) => [pkg.MainPart, .. pkg.Names.Where(OtherParts.Test)];

    // MARK: - fields

    public sealed class FieldRef(string instruction = "", List<XNode>? results = null)
    {
        public string Instruction { get; set; } = instruction;
        public List<XNode> Results { get; set; } = results ?? [];

        public string Kind
        {
            get
            {
                int a = 0;
                while (a < Instruction.Length && char.IsWhiteSpace(Instruction[a])) a++;
                int b = a;
                while (b < Instruction.Length && !char.IsWhiteSpace(Instruction[b])) b++;
                return Instruction[a..b].ToUpperInvariant();
            }
        }

        private static readonly Rx ArgumentRx = new("\\s*\\S+\\s+(?:\"([^\"]+)\"|(\\S+))");
        public string Argument => ArgumentRx.First(Instruction)?.First ?? "";

        public void SetResult(string value)
        {
            if (Results.Count == 0) return;
            var first = Results[0];
            first.Text = value;
            first["xml:space"] = "preserve";
            foreach (var r in Results.Skip(1)) r.Text = "";
        }
    }

    /// Simple and complex fields of a part in document order, nested ones too.
    public static List<FieldRef> Fields(XNode root)
    {
        var output = new List<FieldRef>();
        var stack = new List<(FieldRef Field, bool InResult)>();
        root.Walk(e =>
        {
            if (e.Kind != XKind.Element) return;
            switch (e.Name)
            {
                case "w:fldSimple":
                    output.Add(new FieldRef(e["w:instr"] ?? "", e.Descendants("w:t")));
                    break;
                case "w:fldChar":
                    switch (e["w:fldCharType"])
                    {
                        case "begin": stack.Add((new FieldRef(), false)); break;
                        case "separate": if (stack.Count > 0) stack[^1] = (stack[^1].Field, true); break;
                        case "end":
                            if (stack.Count > 0)
                            {
                                output.Add(stack[^1].Field);
                                stack.RemoveAt(stack.Count - 1);
                            }
                            break;
                    }
                    break;
                case "w:instrText":
                    if (stack.Count > 0) stack[^1].Field.Instruction += e.Text;
                    break;
                case "w:t":
                    if (stack.Count > 0 && stack[^1].InResult && !e.HasAncestor("w:fldSimple")) stack[^1].Field.Results.Add(e);
                    break;
            }
        });
        return output;
    }

    // MARK: - properties

    private static List<(string Name, string Value)> CustomProperties(DocxPackage pkg)
    {
        if (!pkg.Has(CustomPart) || pkg.Xml(CustomPart) is not XNode root) return [];
        var output = new List<(string, string)>();
        foreach (var p in root.Elements)
        {
            if (p["name"] is not string name || name.HasPrefix("ContentTypeId") || name.HasPrefix("MediaService") || name.HasPrefix("_")) continue;
            output.Add((name, p.AllText.TrimWSNL()));
        }
        return output;
    }

    public static void UpdateCoreProperties(DocxPackage pkg, string? title = null)
    {
        if (!pkg.Has(CorePart) || pkg.Xml(CorePart) is not XNode root) return;
        if (title != null) (root.Find("dc:title") ?? root.Add("dc:title")).Text = title;
        var modified = root.Find("dcterms:modified") ?? root.Add("dcterms:modified", new() { ["xsi:type"] = "dcterms:W3CDTF" });
        modified.Text = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// Headers often show the file name.
    public static void UpdateFilenameFields(DocxPackage pkg, string filename)
    {
        foreach (var part in TextParts(pkg))
        {
            if (pkg.Xml(part) is not XNode root) continue;
            foreach (var f in Fields(root).Where(f => f.Kind == "FILENAME"))
                f.SetResult(f.Instruction.Lower().Contains("\\p") ? filename : NSPath.DeletingPathExtension(filename));
        }
    }

    // MARK: - finding

    /// `frontUntil`: the first element of the body; before it [Veld] and label lines count too.
    public static List<Placeholder> Placeholders(DocxPackage pkg, XNode? frontUntil)
    {
        var found = new List<Placeholder>();
        var keys = new HashSet<string>();
        void Add(string label, string source, string current = "")
        {
            var key = Normalized(label);
            if (key.Length == 0 || !keys.Add(key)) return;
            found.Add(new Placeholder(key, label.TrimWS(), source, current.TrimWSNL()));
        }
        var usedProperties = new HashSet<string>();
        foreach (var part in TextParts(pkg))
        {
            if (pkg.Xml(part) is not XNode root) continue;
            foreach (var f in Fields(root))
            {
                if (f.Kind == "DOCPROPERTY" && f.Argument.Length > 0) usedProperties.Add(f.Argument.Lower());
            }
        }
        foreach (var (name, value) in CustomProperties(pkg))
        {
            if (usedProperties.Contains(name.Lower()) || !NoiseProperties.Test(name)) Add(name, "docproperty", value);
        }
        foreach (var part in TextParts(pkg))
        {
            if (pkg.Xml(part) is not XNode root) continue;
            foreach (var sdt in root.Descendants("w:sdt"))
            {
                if (sdt.Find("w:sdtPr") is not XNode pr || pr.Has("w:docPartObj")) continue;
                if ((pr.Find("w:alias")?.Val ?? pr.Find("w:tag")?.Val) is string label)
                    Add(label, "sdt", sdt.Find("w:sdtContent")?.AllText ?? "");
            }
            foreach (var f in Fields(root))
            {
                if (CoreFields.Contains(f.Kind)) Add(f.Kind.Lower(), "coreproperty", string.Concat(f.Results.Select(r => r.Text)));
            }
            foreach (var p in root.Descendants("w:p"))
            {
                foreach (var m in Braces.Matches(ParagraphText(p)))
                {
                    if (m.First is string label) Add(label, "braces");
                }
            }
        }
        if (frontUntil == null) return found;
        foreach (var child in pkg.Body.Elements)
        {
            if (ReferenceEquals(child, frontUntil)) break;
            foreach (var p in Paragraphs(child))
            {
                if (p.HasAncestor("w:sdt") && child.Name != "w:sdt") continue;
                var text = string.Concat(p.Descendants("w:t").Where(t => !t.HasAncestor("w:sdt", below: p)).Select(t => t.Text));
                foreach (var m in Brackets.Matches(text))
                {
                    if (m.First is string label) Add(label, "brackets");
                }
                var tabbed = TextWithTabs(p);
                if (tabbed.GraphemeCount() < 200 && tabbed.Contains('\t') && LabelLine.First(tabbed) is Rx.Match line && line.Groups[0] is string name)
                    Add(name, "label", (line.Groups[1] ?? "").Replace("\t", " "));
            }
        }
        return found;
    }

    private static List<XNode> Paragraphs(XNode e)
    {
        var output = new List<XNode>();
        if (e.Name == "w:p") output.Add(e);
        output.AddRange(e.Descendants("w:p"));
        return output;
    }

    private static string ParagraphText(XNode p) => string.Concat(p.Descendants("w:t").Select(t => t.Text));

    private static string TextWithTabs(XNode p) =>
        string.Concat(p.Descendants("w:t", "w:tab").Select(e => e.Name == "w:t" ? e.Text : (e.HasAncestor("w:r", below: p) ? "\t" : "")));

    // MARK: - filling

    /// Fills every kind of placeholder whose name is in `values`; gives the number of places changed.
    public static int Fill(DocxPackage pkg, IEnumerable<KeyValuePair<string, string>> values, XNode? frontUntil)
    {
        var vals = new Dictionary<string, string>();
        foreach (var (k, v) in values)
        {
            if (v.Length > 0) vals[Normalized(k)] = v;
        }
        if (vals.Count == 0) return 0;
        int count = 0;
        var propertyValues = new Dictionary<string, string>();
        foreach (var (name, _) in CustomProperties(pkg))
        {
            if (vals.TryGetValue(Normalized(name), out var v))
            {
                foreach (var p in pkg.Xml(CustomPart)?.Elements ?? [])
                {
                    if (p["name"] != name) continue;
                    foreach (var e in p.Elements) e.Text = v;
                }
                propertyValues[name.Lower()] = v;
                count++;
            }
        }
        var core = new Dictionary<string, string>();
        foreach (var k in CoreFields)
        {
            if (vals.TryGetValue(k.Lower(), out var v)) core[k] = v;
        }
        if (core.TryGetValue("TITLE", out var coreTitle)) UpdateCoreProperties(pkg, coreTitle);

        foreach (var part in TextParts(pkg))
        {
            if (pkg.Xml(part) is not XNode root) continue;
            foreach (var f in Fields(root))
            {
                if (f.Kind == "DOCPROPERTY" && propertyValues.TryGetValue(f.Argument.Lower(), out var pv)) f.SetResult(pv);
                else if (core.TryGetValue(f.Kind, out var cv)) f.SetResult(cv);
            }
            foreach (var sdt in root.Descendants("w:sdt"))
            {
                if (sdt.Find("w:sdtPr") is not XNode pr) continue;
                var alias = Normalized(pr.Find("w:alias")?.Val ?? "");
                var key = alias.Length == 0 ? Normalized(pr.Find("w:tag")?.Val ?? "") : alias;
                if (vals.TryGetValue(key, out var v))
                {
                    SetControlText(sdt, v);
                    count++;
                }
            }
            foreach (var p in root.Descendants("w:p"))
                count += Replace(p, Braces, m => m.First is string first ? vals.GetValueOrDefault(Normalized(first)) : null);
        }
        if (frontUntil == null) return count;
        foreach (var child in pkg.Body.Elements)
        {
            if (ReferenceEquals(child, frontUntil)) break;
            foreach (var p in Paragraphs(child))
            {
                count += Replace(p, Brackets, m => m.First is string first ? vals.GetValueOrDefault(Normalized(first)) : null);
                count += FillLabelLine(p, vals);
            }
        }
        return count;
    }

    /// A replacement over all w:t of a paragraph; the text takes the look of the first run it touches.
    private static int Replace(XNode p, Rx rx, Func<Rx.Match, string?> with)
    {
        var ts = p.Descendants("w:t");
        int count = 0;
        var matches = rx.Matches(string.Concat(ts.Select(t => t.Text)));
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            if (with(matches[i]) is not string replacement) continue;
            count++;
            Splice(ts, matches[i].Range, replacement);
        }
        return count;
    }

    private static void Splice(List<XNode> ts, NSRange range, string replacement)
    {
        int start = range.Location, end = range.Location + range.Length;
        int position = 0;
        bool placed = false;
        foreach (var t in ts)
        {
            var text = t.Text;
            int a = position, b = position + text.Length;
            position = b;
            if (!placed && a <= start && (start < b || ReferenceEquals(t, ts[^1])))
            {
                int low = Math.Min(start - a, text.Length);
                int high = Math.Max(Math.Min(end, b) - a, low);
                t.Text = text[..low] + replacement + text[high..];
                t["xml:space"] = "preserve";
                placed = true;
            }
            else if (placed && start <= a && a < end)
            {
                t.Text = text[(Math.Min(end, b) - a)..];
                t["xml:space"] = "preserve";
            }
        }
    }

    /// "Betreft<tab>:<tab>oude waarde": the value changes, the label, tabs and look stay.
    private static int FillLabelLine(XNode p, Dictionary<string, string> vals)
    {
        var tabbed = TextWithTabs(p);
        if (!tabbed.Contains('\t') || LabelLine.First(tabbed) is not Rx.Match m || m.Groups[0] is not string label
            || !vals.TryGetValue(Normalized(label), out var value)) return 0;
        var ts = p.Descendants("w:t");
        int full = ts.Sum(t => t.Text.Length);
        var old = (m.Groups[1] ?? "").Replace("\t", "");
        if (old.Length > 0)
        {
            Splice(ts, new NSRange(full - old.Length, old.Length), value);
            return 1;
        }
        // no value yet: a new run with the look of the last one
        var runs = p.Descendants("w:r");
        if (runs.Count == 0 || runs[^1].Parent is not XNode parent) return 0;
        var last = runs[^1];
        var r = last.Copy();
        foreach (var c in r.Children.ToList())
        {
            if (c.Name != "w:rPr") r.Remove(c);
        }
        r.Append(XNode.TextElement("w:t", value));
        parent.InsertAfter(last, r);
        return 1;
    }

    private static void SetControlText(XNode sdt, string value)
    {
        sdt.Find("w:sdtPr")?.RemoveAll("w:showingPlcHdr");
        var all = sdt.Find("w:sdtContent")?.Descendants("w:t");
        if (all == null || all.Count == 0) return;
        var first = all[0];
        first.Text = value;
        first["xml:space"] = "preserve";
        foreach (var t in all.Skip(1)) t.Text = "";
        if (first.Parent?.Find("w:rPr") is XNode rpr)
        {
            foreach (var s in rpr.FindAll("w:rStyle"))
            {
                var v = s.Val?.Lower() ?? "";
                if (v.Contains("placeholder") || v.Contains("tijdelijke")) rpr.Remove(s);
            }
            rpr.RemoveAll("w:color");
        }
    }

    /// Asks Word to refresh the page numbers of the table of contents when the document opens.
    public static void SetUpdateFieldsOnOpen(DocxPackage pkg)
    {
        if (pkg.RelatedPart(OOXML.RelSettings) is not string part || pkg.Xml(part) is not XNode settings) return;
        OOXML.Put(settings, "w:updateFields", new() { ["w:val"] = "true" });
    }
}

/// Rebuilds the table of contents, so it is right the moment the file opens. Entries link to `_Toc` bookmarks on the
/// headings; Word fills in the page numbers on opening.
public static class DocxTOC
{
    private static readonly Rx TocInstruction = new("\\bTOC\\b");
    private static readonly Rx Levels = new("\\\\o\\s+\"?(\\d)\\s*-\\s*(\\d)\"?");

    private static (XNode Container, int First, int Last, string Instruction)? Find(DocModel model)
    {
        var body = model.Pkg.Body;
        var containers = new List<XNode> { body };
        foreach (var sdt in body.FindAll("w:sdt"))
        {
            if (sdt.Find("w:sdtContent") is XNode content) containers.Add(content);
        }
        foreach (var c in containers)
        {
            var kids = c.Elements;
            for (int i = 0; i < kids.Count; i++)
            {
                var p = kids[i];
                if (p.Name != "w:p" || !TocInstruction.Test(string.Concat(p.Descendants("w:instrText").Select(e => e.Text)))) continue;
                int depth = 0;
                bool started = false;
                var instruction = "";
                for (int j = i; j < kids.Count; j++)
                {
                    foreach (var e in kids[j].Descendants("w:fldChar", "w:instrText"))
                    {
                        if (e.Name == "w:instrText")
                        {
                            if (depth == 1) instruction += e.Text;
                            continue;
                        }
                        if (e["w:fldCharType"] == "begin") { depth++; started = true; }
                        else if (e["w:fldCharType"] == "end")
                        {
                            depth--;
                            if (started && depth == 0) return (c, i, j, instruction);
                        }
                    }
                }
                return (c, i, kids.Count - 1, instruction);
            }
        }
        return null;
    }

    /// The number of entries; 0 when the document has no table of contents.
    public static int Rebuild(DocModel model)
    {
        if (Find(model) is not var (container, first, last, foundInstruction)) return 0;
        int maxLevel = Str.ToInt(Levels.First(foundInstruction)?.Groups[1]) ?? 3;
        long nextID = (model.Pkg.Document.Descendants("w:bookmarkStart").Select(e => Str.ToLong(e["w:id"])).Max() ?? 0) + 1;

        var entries = new List<(int Level, string Number, string Title, string Bookmark, bool Numbered)>();
        foreach (var node in model.Outline())
        {
            var p = node.Block.Element;
            var name = p.FindAll("w:bookmarkStart").Select(e => e["w:name"]).FirstOrDefault(n => n != null && n.HasPrefix("_Toc"));
            if (name == null)
            {
                var created = "_TocMM" + nextID.ToString("D6");
                var start = new XNode("w:bookmarkStart", new() { ["w:id"] = nextID.ToString(), ["w:name"] = created });
                if (p.Find("w:pPr") is XNode ppr) p.InsertAfter(ppr, start);
                else p.Insert(start, 0);
                p.Add("w:bookmarkEnd", new() { ["w:id"] = nextID.ToString() });
                nextID++;
                name = created;
            }
            if (node.Level <= maxLevel) entries.Add((node.Level, node.Number, node.Title, name, IsNumbered(model, p)));
        }

        int width = HouseStyle.TextWidthTwips(model);
        var paragraphs = new List<XNode>();
        if (entries.Count == 0)
        {
            var p = Paragraph(model, 1, width, false);
            FieldStart(p, foundInstruction);
            RunText(p, L("Geen koppen gevonden."));
            FieldChar(p, "end");
            paragraphs.Add(p);
        }
        for (int n = 0; n < entries.Count; n++)
        {
            var e = entries[n];
            var p = Paragraph(model, e.Level, width, e.Numbered);
            if (n == 0) FieldStart(p, foundInstruction);
            var h = p.Add("w:hyperlink", new() { ["w:anchor"] = e.Bookmark, ["w:history"] = "1" });
            if (e.Numbered && e.Number.Length > 0)
            {
                RunText(h, e.Number);
                Tab(h);
            }
            RunText(h, e.Title);
            Tab(h);
            FieldChar(h, "begin");
            Instruction(h, $" PAGEREF {e.Bookmark} \\h ");
            FieldChar(h, "separate");
            RunText(h, "");
            FieldChar(h, "end");
            if (n == entries.Count - 1) FieldChar(p, "end");
            paragraphs.Add(p);
        }
        var kids = container.Elements;
        var anchor = kids[first];
        foreach (var p in paragraphs) container.InsertBefore(anchor, p);
        for (int k = first; k <= last; k++) container.Remove(kids[k]);
        DocxFields.SetUpdateFieldsOnOpen(model.Pkg);
        return entries.Count;
    }

    private static bool IsNumbered(DocModel model, XNode p)
    {
        var ppr = p.Find("w:pPr");
        if (ppr?.Find("w:numPr") is XNode numPr)
        {
            var id = numPr.Find("w:numId")?.Val;
            return id != null && id != "0";
        }
        return model.Styles.IsNumbered(ppr?.Find("w:pStyle")?.Val);
    }

    private static XNode Paragraph(DocModel model, int level, int width, bool numbered)
    {
        var id = model.Styles.FindTOC(level);
        if (id == null)
        {
            var stylePPr = new XNode("w:pPr");
            OOXML.Put(stylePPr, "w:spacing", new() { ["w:after"] = "100" });
            OOXML.Put(stylePPr, "w:ind", new() { ["w:left"] = (220 * (level - 1)).ToString() });
            id = model.Styles.EnsureStyle($"TOC{level}", $"toc {level}", model.Styles.DefaultParagraph, stylePPr);
        }
        var p = new XNode("w:p");
        var ppr = p.Add("w:pPr");
        OOXML.Put(ppr, "w:pStyle", new() { ["w:val"] = id });
        var tabs = new XNode("w:tabs");
        if (numbered)
            tabs.Add("w:tab", new() { ["w:val"] = "left", ["w:pos"] = (220 * (level - 1) + 440 + 220 * Math.Max(0, level - 2)).ToString() });
        tabs.Add("w:tab", new() { ["w:val"] = "right", ["w:leader"] = "dot", ["w:pos"] = width.ToString() });
        OOXML.Put(ppr, tabs);
        return p;
    }

    private static XNode Run(XNode parent)
    {
        var r = parent.Add("w:r");
        r.Add("w:rPr").Add("w:noProof");
        return r;
    }

    private static void RunText(XNode parent, string text)
    {
        var t = XNode.TextElement("w:t", text);
        t["xml:space"] = "preserve";
        Run(parent).Append(t);
    }

    private static void Tab(XNode parent) => Run(parent).Add("w:tab");
    private static void FieldChar(XNode parent, string kind) => Run(parent).Add("w:fldChar", new() { ["w:fldCharType"] = kind });

    private static void Instruction(XNode parent, string text)
    {
        var t = XNode.TextElement("w:instrText", text);
        t["xml:space"] = "preserve";
        Run(parent).Append(t);
    }

    private static void FieldStart(XNode p, string text)
    {
        FieldChar(p, "begin");
        Instruction(p, text.TrimWSNL().Length == 0 ? " TOC \\o \"1-3\" \\h \\z \\u " : text);
        FieldChar(p, "separate");
    }
}
