using System.Text;

namespace WinAIMagic.Documents;

/// OOXML names the Word engine uses.
public static class OOXML
{
    public const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    public const string RelImage = Relationships + "image";
    public const string RelHyperlink = Relationships + "hyperlink";
    public const string RelNumbering = Relationships + "numbering";
    public const string RelOfficeDocument = Relationships + "officeDocument";
    public const string RelStyles = Relationships + "styles";
    public const string RelSettings = Relationships + "settings";
    public const string CtNumbering = "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml";
    public const string CtDocument = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    public static readonly Dictionary<string, string> Drawing = new()
    {
        ["wp"] = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing",
        ["a"] = "http://schemas.openxmlformats.org/drawingml/2006/main",
        ["pic"] = "http://schemas.openxmlformats.org/drawingml/2006/picture",
        ["r"] = "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
    };

    /// The order of the children of the property containers that are changed: Word refuses a document with another order.
    private static readonly Dictionary<string, string> Order = new()
    {
        ["w:pPr"] = "pStyle keepNext keepLines pageBreakBefore framePr widowControl numPr suppressLineNumbers pBdr shd tabs "
            + "suppressAutoHyphens kinsoku wordWrap overflowPunct topLinePunct autoSpaceDE autoSpaceDN bidi "
            + "adjustRightInd snapToGrid spacing ind contextualSpacing mirrorIndents suppressOverlap jc textDirection "
            + "textAlignment textboxTightWrap outlineLvl divId cnfStyle rPr sectPr pPrChange",
        ["w:rPr"] = "rStyle rFonts b bCs i iCs caps smallCaps strike dstrike outline shadow emboss imprint noProof snapToGrid "
            + "vanish webHidden color spacing w kern position sz szCs highlight u effect bdr shd fitText vertAlign rtl cs "
            + "em lang eastAsianLayout specVanish oMath",
        ["w:numPr"] = "ilvl numId numberingChange ins",
        ["w:tcPr"] = "cnfStyle tcW gridSpan hMerge vMerge tcBorders shd noWrap tcMar textDirection tcFitText vAlign hideMark",
        ["w:tblPr"] = "tblStyle tblpPr tblOverlap bidiVisual tblStyleRowBandSize tblStyleColBandSize tblW jc tblCellSpacing "
            + "tblInd tblBorders shd tblLayout tblCellMar tblLook tblCaption tblDescription",
        ["w:settings"] = "writeProtection view zoom removePersonalInformation removeDateAndTime doNotDisplayPageBoundaries "
            + "displayBackgroundShape printPostScriptOverText printFractionalCharacterWidth printFormsData "
            + "embedTrueTypeFonts embedSystemFonts saveSubsetFonts saveFormsData mirrorMargins alignBordersAndEdges "
            + "bordersDoNotSurroundHeader bordersDoNotSurroundFooter gutterAtTop hideSpellingErrors "
            + "hideGrammaticalErrors activeWritingStyle proofState formsDesign attachedTemplate linkStyles "
            + "stylePaneFormatFilter stylePaneSortMethod documentType mailMerge revisionView trackRevisions "
            + "doNotTrackMoves doNotTrackFormatting documentProtection autoFormatOverride styleLockTheme "
            + "styleLockQFSet defaultTabStop autoHyphenation consecutiveHyphenLimit hyphenationZone "
            + "doNotHyphenateCaps showEnvelope summaryLength clickAndTypeStyle defaultTableStyle evenAndOddHeaders "
            + "bookFoldRevPrinting bookFoldPrinting bookFoldPrintingSheets drawingGridHorizontalSpacing "
            + "drawingGridVerticalSpacing displayHorizontalDrawingGridEvery displayVerticalDrawingGridEvery "
            + "doNotUseMarginsForDrawingGridOrigin drawingGridHorizontalOrigin drawingGridVerticalOrigin "
            + "doNotShadeFormData noPunctuationKerning characterSpacingControl printTwoOnOne "
            + "strictFirstAndLastChars noLineBreaksAfter noLineBreaksBefore savePreviewPicture "
            + "doNotValidateAgainstSchema saveInvalidXml ignoreMixedContent alwaysShowPlaceholderText "
            + "doNotDemarcateInvalidXml saveXmlDataOnly useXSLTWhenSaving saveThroughXslt showXMLTags "
            + "alwaysMergeEmptyNamespace updateFields hdrShapeDefaults footnotePr endnotePr compat docVars rsids "
            + "mathPr attachedSchema themeFontLang clrSchemeMapping doNotIncludeSubdocsInStats "
            + "doNotAutoCompressPictures forceUpgrade captions readModeInkLockDown smartTagType schemaLibrary "
            + "shapeDefaults doNotEmbedSmartTags decimalSymbol listSeparator",
    };

    public static readonly Dictionary<string, Dictionary<string, int>> OrderIndex = Order.ToDictionary(
        kv => kv.Key,
        kv => kv.Value.Split(' ').Select((name, i) => (name, i)).ToDictionary(x => "w:" + x.name, x => x.i));

    /// "word/../customXml/a.xml" -> "customXml/a.xml"
    public static string Normalized(string path)
    {
        var output = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (output.Count > 0) output.RemoveAt(output.Count - 1); }
            else output.Add(part);
        }
        return string.Join("/", output);
    }

    /// Puts `child` in a property container at its place in the schema, in place of one with the same name.
    public static XNode Put(XNode parent, XNode child)
    {
        var index = OrderIndex.GetValueOrDefault(parent.Name) ?? [];
        parent.RemoveAll(child.Name);
        int position = index.GetValueOrDefault(child.Name, 10_000);
        var next = parent.Elements.FirstOrDefault(e => index.GetValueOrDefault(e.Name, 10_000) > position);
        if (next != null) parent.InsertBefore(next, child);
        else parent.Append(child);
        return child;
    }

    public static XNode Put(XNode parent, string name, Dictionary<string, string>? attributes = null) =>
        Put(parent, new XNode(name, attributes));
}

/// A .docx kept part by part: only the parts that are read as XML are written anew; the theme, fonts, images and
/// everything else go back byte for byte. That keeps the house style intact.
public sealed class DocxPackage
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, byte[]> _raw = [];
    private readonly Dictionary<string, XNode> _parsed = [];
    public string MainPart { get; private set; } = "word/document.xml";

    public DocxPackage(string path) : this(File.ReadAllBytes(path)) { }

    public DocxPackage(byte[] data)
    {
        foreach (var entry in Zip.Read(data))
        {
            if (_raw.ContainsKey(entry.Name)) continue;
            _order.Add(entry.Name);
            _raw[entry.Name] = entry.Data;
        }
        if (_raw.TryGetValue("_rels/.rels", out var rels) && TryParse(rels) is XNode relsRoot
            && relsRoot.Elements.FirstOrDefault(e => e["Type"] == OOXML.RelOfficeDocument)?["Target"] is string target)
        {
            MainPart = target.HasPrefix("/") ? target[1..] : target;
        }
        if (!_raw.TryGetValue(MainPart, out var main)) throw new Zip.Failure(L("Dit is geen Word-document."));
        var root = TryParse(main) ?? throw new Zip.Failure(L("Dit Word-document is beschadigd."));
        // the code assumes the prefix w: of normal Word files; Strict Open XML has another namespace
        if (root["xmlns:w"] != OOXML.WordNamespace || root.Find("w:body") == null)
            throw new Zip.Failure(L("Dit Word-document gebruikt een indeling die de app niet kent (bijvoorbeeld Strict Open XML). Sla het in Word op als gewoon .docx."));
        _parsed[MainPart] = root;
    }

    /// A new package from its parts, for the built-in house style.
    public DocxPackage(IEnumerable<(string Name, string Text)> parts)
    {
        foreach (var (name, text) in parts)
        {
            _order.Add(name);
            _raw[name] = Encoding.UTF8.GetBytes(text);
        }
        _parsed[MainPart] = XNode.Parse(_raw.GetValueOrDefault(MainPart) ?? []);
    }

    private static XNode? TryParse(byte[] data)
    {
        try { return XNode.Parse(data); }
        catch (Zip.Failure) { return null; }
    }

    public bool Has(string name) => _raw.ContainsKey(name) || _parsed.ContainsKey(name);
    public IReadOnlyList<string> Names => _order;

    public byte[]? Data(string name)
    {
        if (_parsed.TryGetValue(name, out var x)) return x.Serialized();
        return _raw.GetValueOrDefault(name);
    }

    public void SetData(string name, byte[] data)
    {
        _parsed.Remove(name);
        if (!_raw.ContainsKey(name)) _order.Add(name);
        _raw[name] = data;
    }

    /// The part as a tree; null when it is missing or not XML.
    public XNode? Xml(string name)
    {
        if (_parsed.TryGetValue(name, out var x)) return x;
        if (!_raw.TryGetValue(name, out var d) || TryParse(d) is not XNode root) return null;
        _parsed[name] = root;
        return root;
    }

    public void SetXml(string name, XNode root)
    {
        if (!_raw.ContainsKey(name))
        {
            _order.Add(name);
            _raw[name] = [];
        }
        _parsed[name] = root;
    }

    public XNode Document => Xml(MainPart)!;
    public XNode Body => Document.Find("w:body")!;

    public byte[] ToData()
    {
        // a template's main part declares itself as a template, and then Word does not open the .docx
        if (Xml("[Content_Types].xml") is XNode types
            && types.FindAll("Override").FirstOrDefault(e => e["PartName"] == "/" + MainPart) is XNode main
            && main["ContentType"]?.Contains(".template.") == true)
        {
            main["ContentType"] = OOXML.CtDocument;
        }
        // [Content_Types].xml first, as Word expects
        var names = _order.Where(n => n == "[Content_Types].xml").Concat(_order.Where(n => n != "[Content_Types].xml"));
        var entries = new List<ZipEntry>();
        foreach (var n in names)
        {
            if (Data(n) is byte[] d) entries.Add(new ZipEntry(n, d));
        }
        return Zip.Write(entries);
    }

    // MARK: - relationships

    private static string RelsName(string part) =>
        NSPath.AppendingPathComponent(NSPath.DeletingLastPathComponent(part), "_rels/" + NSPath.LastPathComponent(part) + ".rels");

    private XNode Rels(string part)
    {
        var name = RelsName(part);
        if (Xml(name) is XNode x) return x;
        var root = new XNode("Relationships", new() { ["xmlns"] = "http://schemas.openxmlformats.org/package/2006/relationships" });
        SetXml(name, root);
        // the extension needs a content type
        EnsureDefaultType("rels", "application/vnd.openxmlformats-package.relationships+xml");
        return root;
    }

    public (string Type, string Target, bool External)? Relationship(string part, string id)
    {
        var r = Rels(part).Elements.FirstOrDefault(e => e["Id"] == id);
        if (r == null) return null;
        return (r["Type"] ?? "", r["Target"] ?? "", r["TargetMode"] == "External");
    }

    public string? PartForRelationship(string part, string id)
    {
        if (Relationship(part, id) is not { External: false } r) return null;
        return Resolve(part, r.Target);
    }

    private static string Resolve(string part, string target)
    {
        if (target.HasPrefix("/")) return target[1..];
        return OOXML.Normalized(NSPath.AppendingPathComponent(NSPath.DeletingLastPathComponent(part), target));
    }

    public string? RelatedPart(string type)
    {
        var r = Rels(MainPart).Elements.FirstOrDefault(e => e["Type"] == type && e["TargetMode"] != "External");
        return r?["Target"] is string target ? Resolve(MainPart, target) : null;
    }

    public string AddRelationship(string part, string type, string target, bool external = false)
    {
        var root = Rels(part);
        if (root.Elements.FirstOrDefault(e => e["Type"] == type && e["Target"] == target)?["Id"] is string id) return id;
        var taken = root.Elements.Select(e => e["Id"]).Where(i => i != null).ToHashSet();
        int n = 1;
        while (taken.Contains($"rId{n}")) n++;
        var r = root.Add("Relationship", new() { ["Id"] = $"rId{n}", ["Type"] = type, ["Target"] = target });
        if (external) r["TargetMode"] = "External";
        return $"rId{n}";
    }

    // MARK: - content types

    public void EnsureDefaultType(string ext, string type)
    {
        if (Xml("[Content_Types].xml") is not XNode types
            || types.FindAll("Default").Any(e => e["Extension"]?.Lower() == ext.Lower())) return;
        // defaults come before overrides for strict readers
        types.Insert(new XNode("Default", new() { ["Extension"] = ext, ["ContentType"] = type }), 0);
    }

    public void EnsureOverrideType(string part, string type)
    {
        if (Xml("[Content_Types].xml") is not XNode types
            || types.FindAll("Override").Any(e => e["PartName"] == "/" + part)) return;
        types.Add("Override", new() { ["PartName"] = "/" + part, ["ContentType"] = type });
    }

    public string UniqueMediaName(string ext)
    {
        int n = 1;
        while (Has($"word/media/mm_image{n}.{ext}")) n++;
        return $"word/media/mm_image{n}.{ext}";
    }
}
