namespace WinAIMagic.Documents;

/// A draft in Markdown, with the template's variable fields at the top between --- lines.
public sealed class DocumentDraft : IEquatable<DocumentDraft>
{
    public List<(string Key, string Value)> Fields { get; set; } = [];
    public string Body { get; set; } = "";

    public bool Equals(DocumentDraft? o) =>
        o != null && Body == o.Body && Fields.Select(f => f.Key).SequenceEqual(o.Fields.Select(f => f.Key))
            && Fields.Select(f => f.Value).SequenceEqual(o.Fields.Select(f => f.Value));
    public override bool Equals(object? o) => Equals(o as DocumentDraft);
    public override int GetHashCode() => Body.GetHashCode();

    public DocumentDraft(IEnumerable<(string Key, string Value)>? fields = null, string body = "")
    {
        if (fields != null) Fields = fields.ToList();
        Body = body;
    }

    public DocumentDraft(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n");
        var lines = text.Split('\n');
        int end = -1;
        if (lines[0].TrimWS() == "---")
        {
            for (int i = 1; i < lines.Length && end < 0; i++)
            {
                if (lines[i].TrimWS() == "---") end = i;
            }
        }
        if (end < 0)
        {
            Body = text;
            return;
        }
        foreach (var line in lines[1..end])
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].TrimWS();
            var value = line[(colon + 1)..].TrimWS();
            if (value.Length >= 2 && value.HasPrefix("\"") && value.HasSuffix("\"")) value = value[1..^1];
            if (key.Length > 0) Fields.Add((key, value));
        }
        Body = string.Join("\n", lines[(end + 1)..]).TrimNL() + "\n";
    }

    public string Markdown
    {
        get
        {
            if (Fields.Count == 0) return Body;
            var head = Fields.Select(f =>
            {
                bool plain = f.Value.Length == 0 || !(f.Value.Contains(':') || f.Value.Contains('#') || f.Value.HasPrefix("\""));
                return f.Key + ": " + (plain ? f.Value : "\"" + f.Value.Replace("\"", "'") + "\"");
            });
            return "---\n" + string.Join("\n", head) + "\n---\n\n" + Body;
        }
    }

    private static readonly Rx FieldMark = new("^(\\[.*\\]|\\{\\{.*\\}\\}|<<.*>>|«.*»)$");

    /// No value yet: nothing, or the template's own marking such as "[Klantnaam]".
    public static bool IsEmptyField(string value)
    {
        var t = value.TrimWS();
        return t.Length == 0 || FieldMark.Test(t);
    }

    private static readonly HashSet<string> TitleNames = ["titel", "title", "titre", "onderwerp", "betreft", "subject"];

    /// The title for the cover and the properties: the field with that name, else the first heading.
    public string? Title
    {
        get
        {
            foreach (var f in Fields)
            {
                if (TitleNames.Contains(DocxFields.Normalized(f.Key)) && f.Value.Length > 0) return f.Value;
            }
            return MarkdownBlocks.Parse(Body).FirstOrDefault(b => b.Kind == MDKind.Heading)?.Plain;
        }
    }
}

/// From a template to Markdown and back: the cover, table of contents, sections, headers and footers stay the
/// template's own, and only the body is written anew.
public static class DocxCompose
{
    public sealed class Report
    {
        public string? Url { get; set; }
        public int Blocks { get; set; }
        public int FieldsFilled { get; set; }
        public int TocEntries { get; set; }
        /// What the reader of the document would notice: sections that are gone, fields without a value.
        public List<string> Warnings { get; set; } = [];
        /// What is wrong with the file itself; empty when it is sound.
        public List<string> Problems { get; set; } = [];
    }

    /// `template`: a template's bytes, or null for the built-in plain house style.
    public static Report Build(byte[]? template, string markdown, string to, ImageResolver? images = null)
    {
        var pkg = template != null ? new DocxPackage(template) : PlainTemplate();
        var draft = new DocumentDraft(markdown);
        var model = new DocModel(pkg);
        var report = new Report();
        // the images of the template first: its draft refers to them by their name in the package
        var own = TemplateImages(pkg);
        var builder = new DocxBuilder(model, s => own(s) ?? images?.Invoke(s));   // learns the house style before anything goes
        int removed = 0;
        foreach (var b in model.Blocks.Skip(model.BodyStart))
        {
            if (b.Kind == BlockKind.Section) continue;
            if (b.HasSectionBreak) removed++;
            b.Element.RemoveFromParent();
        }
        if (removed > 0) report.Warnings.Add(L("%@ sectie-einde(n) in de sjabloonbody zijn vervallen; de laatste sectie-opmaak is behouden.", removed));

        var elements = builder.Build(draft.Body);
        var body = pkg.Body;
        if (body.Find("w:sectPr") is XNode last)
        {
            foreach (var e in elements) body.InsertBefore(last, e);
        }
        else
        {
            foreach (var e in elements) body.Append(e);
        }
        report.Blocks = elements.Count;

        var values = new Dictionary<string, string>();
        foreach (var f in draft.Fields)
        {
            if (!DocumentDraft.IsEmptyField(f.Value)) values.TryAdd(f.Key, f.Value);
        }
        // the title goes on the cover under either name, unless the draft says otherwise
        if (draft.Title is string title)
        {
            var given = values.Keys.Select(DocxFields.Normalized).ToHashSet();
            foreach (var name in new[] { "titel", "title" })
            {
                if (!given.Contains(name)) values[name] = title;
            }
        }
        report.FieldsFilled = DocxFields.Fill(pkg, values, elements.FirstOrDefault());
        var empty = draft.Fields.Where(f => DocumentDraft.IsEmptyField(f.Value)).Select(f => f.Key).ToList();
        if (empty.Count > 0) report.Warnings.Add(L("Nog niet ingevuld: %@.", string.Join(", ", empty)));

        DocxFields.UpdateFilenameFields(pkg, Path.GetFileName(to));
        report.TocEntries = DocxTOC.Rebuild(new DocModel(pkg));
        DocxFields.UpdateCoreProperties(pkg, draft.Title);

        var data = pkg.ToData();
        report.Problems = DocxValidator.Validate(data);
        WriteAtomically(to, data);
        report.Url = to;
        return report;
    }

    private static void WriteAtomically(string to, byte[] data)
    {
        var full = Path.GetFullPath(to);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// All text of a template for the model to read, also what stands before the first heading.
    public static string TemplateText(byte[] template)
    {
        var model = new DocModel(new DocxPackage(template));
        var parts = new List<string>();
        foreach (var b in model.Blocks)
        {
            if (b.Kind == BlockKind.Toc) continue;
            if (HouseStyle.IsGuidance(b)) parts.Add("<!-- " + b.Text.TrimWSNL() + " -->");
            else if (DocxMarkdown.Block(b) is string text) parts.Add(text);
        }
        return string.Join("\n\n", parts);
    }

    /// The images inside the template, which its draft refers to by their name in the package.
    private static ImageResolver TemplateImages(DocxPackage pkg) => name =>
    {
        if (name.HasPrefix("word/media/") && pkg.Data(name) is byte[] data) return (data, NSPath.PathExtension(name));
        return null;
    };

    /// Without a template of the project: a plain, tidy document with a title and a table of contents.
    public static DocxPackage PlainTemplate()
    {
        var w = $"xmlns:w=\"{OOXML.WordNamespace}\"";
        const string toc = "<w:p><w:pPr><w:pStyle w:val=\"TOC1\"/></w:pPr><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>"
            + "<w:r><w:instrText xml:space=\"preserve\"> TOC \\o \"1-3\" \\h \\z \\u </w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>";
        static string Heading(int n, int size) =>
            $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{n}\"><w:name w:val=\"heading {n}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>"
                + $"<w:pPr><w:keepNext/><w:spacing w:before=\"{(n == 1 ? 360 : 240)}\" w:after=\"120\"/><w:outlineLvl w:val=\"{n - 1}\"/></w:pPr>"
                + $"<w:rPr><w:b/><w:color w:val=\"1F3864\"/><w:sz w:val=\"{size}\"/></w:rPr></w:style>";
        static string Simple(string id, string name, string inner) =>
            $"<w:style w:type=\"paragraph\" w:styleId=\"{id}\"><w:name w:val=\"{name}\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/>{inner}</w:style>";
        var styles = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles {w}>"
            + "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/></w:rPr></w:rPrDefault>"
            + "<w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>"
            + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>"
            + Heading(1, 32) + Heading(2, 26) + Heading(3, 23)
            + Simple("Title", "Title", "<w:pPr><w:spacing w:after=\"480\"/></w:pPr><w:rPr><w:b/><w:color w:val=\"1F3864\"/><w:sz w:val=\"52\"/></w:rPr>")
            + Simple("Quote", "Quote", "<w:pPr><w:ind w:left=\"567\"/></w:pPr><w:rPr><w:i/></w:rPr>")
            + Simple("Caption", "caption", "<w:rPr><w:i/><w:sz w:val=\"18\"/></w:rPr>")
            + Simple("TOC1", "toc 1", "<w:pPr><w:spacing w:after=\"80\"/></w:pPr>")
            + Simple("TOC2", "toc 2", "<w:pPr><w:spacing w:after=\"80\"/><w:ind w:left=\"220\"/></w:pPr>")
            + Simple("TOC3", "toc 3", "<w:pPr><w:spacing w:after=\"80\"/><w:ind w:left=\"440\"/></w:pPr>")
            + "<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:rPr><w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/></w:rPr></w:style>"
            + "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:tblPr><w:tblBorders>"
            + string.Concat(new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(b => $"<w:{b} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"A6A6A6\"/>"))
            + "</w:tblBorders></w:tblPr></w:style></w:styles>";
        var document = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {w} xmlns:r=\"{OOXML.Drawing["r"]}\"><w:body>"
            + "<w:p><w:pPr><w:pStyle w:val=\"Title\"/></w:pPr><w:r><w:t>{{titel}}</w:t></w:r></w:p>" + toc
            + "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>-</w:t></w:r></w:p>"
            + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>"
            + "</w:body></w:document>";
        const string main = "application/vnd.openxmlformats-officedocument.wordprocessingml";
        return new DocxPackage(new (string, string)[]
        {
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + $"<Override PartName=\"/word/document.xml\" ContentType=\"{main}.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"{main}.styles+xml\"/>"
                + $"<Override PartName=\"/word/settings.xml\" ContentType=\"{main}.settings+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + $"<Relationship Id=\"rId1\" Type=\"{OOXML.RelOfficeDocument}\" Target=\"word/document.xml\"/>"
                + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>"),
            ("word/_rels/document.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + $"<Relationship Id=\"rId1\" Type=\"{OOXML.RelStyles}\" Target=\"styles.xml\"/><Relationship Id=\"rId2\" Type=\"{OOXML.RelSettings}\" Target=\"settings.xml\"/></Relationships>"),
            ("word/document.xml", document),
            ("word/styles.xml", styles),
            ("word/settings.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:settings {w}><w:defaultTabStop w:val=\"708\"/></w:settings>"),
            ("docProps/core.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" "
                + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><dc:title></dc:title></cp:coreProperties>"),
        });
    }
}

/// Checks a document before the user gets it: what would make Word refuse or repair the file.
public static class DocxValidator
{
    public static List<string> Validate(byte[] data)
    {
        List<ZipEntry> entries;
        try { entries = Zip.Read(data); }
        catch (Zip.Failure) { return [L("Het bestand is geen geldig zip-pakket.")]; }
        var problems = new List<string>();
        var xml = new Dictionary<string, XNode>();
        var names = entries.Select(e => e.Name).ToHashSet();
        foreach (var entry in entries)
        {
            if (!entry.Name.HasSuffix(".xml") && !entry.Name.HasSuffix(".rels")) continue;
            try { xml[entry.Name] = XNode.Parse(entry.Data); }
            catch (Zip.Failure) { problems.Add(L("%@: XML ongeldig", entry.Name)); }
        }
        if (!xml.TryGetValue("[Content_Types].xml", out var types))
        {
            problems.Add(L("[Content_Types].xml ontbreekt"));
            return problems;
        }
        var defaults = types.FindAll("Default").Select(e => e["Extension"]?.Lower()).Where(e => e != null).ToHashSet();
        var overrides = types.FindAll("Override").Select(e => e["PartName"]).Where(e => e != null).ToHashSet();
        foreach (var n in names)
        {
            if (n != "[Content_Types].xml" && !overrides.Contains("/" + n) && !defaults.Contains(n.Split('.')[^1].Lower()))
                problems.Add(L("Geen content type voor %@", n));
        }
        foreach (var (n, root) in xml)
        {
            if (!n.HasSuffix(".rels")) continue;
            var source = NSPath.DeletingLastPathComponent(NSPath.DeletingLastPathComponent(n));
            foreach (var r in root.Elements)
            {
                if (r["TargetMode"] == "External") continue;
                var target = r["Target"] ?? "";
                var path = target.HasPrefix("/") ? target[1..] : OOXML.Normalized(NSPath.AppendingPathComponent(source, target));
                if (!names.Contains(path)) problems.Add(L("%@: relatie %@ wijst naar ontbrekend %@", n, r["Id"] ?? "", path));
            }
        }
        if (xml.TryGetValue("word/document.xml", out var doc)) problems.AddRange(Check(doc));
        return problems;
    }

    private static List<string> Check(XNode doc)
    {
        var output = new List<string>();
        static List<string> Duplicates(IEnumerable<string> values)
        {
            var seen = new HashSet<string>();
            var dup = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var v in values)
            {
                if (!seen.Add(v)) dup.Add(v);
            }
            return dup.ToList();
        }
        var bookmarks = doc.Descendants("w:bookmarkStart");
        foreach (var (what, list) in new (string, IEnumerable<string>)[]
        {
            (L("bladwijzer-ID's"), bookmarks.Select(b => b["w:id"]).OfType<string>()),
            (L("bladwijzernamen"), bookmarks.Select(b => b["w:name"]).OfType<string>()),
            (L("tekening-ID's"), doc.Descendants("wp:docPr").Select(d => d["id"]).OfType<string>()),
        })
        {
            var d = Duplicates(list);
            if (d.Count > 0) output.Add(L("Dubbele %@: %@", what, string.Join(", ", d.Take(5))));
        }
        foreach (var kind in new[] { "w:pPr", "w:rPr", "w:tcPr", "w:tblPr" })
        {
            if (!OOXML.OrderIndex.TryGetValue(kind, out var index)) continue;
            foreach (var e in doc.Descendants(kind))
            {
                if (kind == "w:rPr" && e.Parent?.Name is string parent && parent != "w:r" && parent != "w:pPr") continue;
                var seq = e.Elements.Where(c => index.ContainsKey(c.Name)).Select(c => index[c.Name]).ToList();
                if (!seq.SequenceEqual(seq.Order()))
                {
                    output.Add(L("Verkeerde volgorde in %@: %@", kind, string.Join(", ", e.Elements.Select(c => c.Name))));
                    break;
                }
            }
        }
        var chars = doc.Descendants("w:fldChar");
        int begins = chars.Count(c => c["w:fldCharType"] == "begin");
        int ends = chars.Count(c => c["w:fldCharType"] == "end");
        if (begins != ends) output.Add(L("Velden niet in balans: %@ begin / %@ end", begins, ends));
        var bodyElements = doc.Find("w:body")?.Elements;
        if (bodyElements is { Count: > 0 } && bodyElements[^1].Name != "w:sectPr") output.Add(L("Laatste body-element is geen sectPr"));
        // a prefix without a declaration makes the whole file unreadable
        var declared = doc.Attributes.Keys.Where(k => k.HasPrefix("xmlns:")).Select(k => k[6..]).ToHashSet();
        declared.Add("xml");
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(XNode e, HashSet<string> scope)
        {
            var inner = new HashSet<string>(scope);
            foreach (var k in e.Attributes.Keys)
            {
                if (k.HasPrefix("xmlns:")) inner.Add(k[6..]);
            }
            foreach (var name in e.Attributes.Keys.Prepend(e.Name))
            {
                if (name.HasPrefix("xmlns")) continue;
                int colon = name.IndexOf(':');
                if (colon >= 0 && !inner.Contains(name[..colon])) missing.Add(name[..colon]);
            }
            foreach (var c in e.Elements) Walk(c, inner);
        }
        Walk(doc, declared);
        if (missing.Count > 0) output.Add(L("Niet gedeclareerde XML-voorvoegsels: %@", string.Join(", ", missing)));
        return output;
    }
}
