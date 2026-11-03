using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace WinAIMagic.Documents;

public enum XKind { Element, Text, Comment, Instruction }

/// A small XML tree for the parts of a .docx. Names stay as written ("w:p"), so what is not touched is written back
/// the same; `DocxPackage` checks that the prefix w: means what the code assumes.
public sealed class XNode
{
    private List<XNode> _children = [];

    public XKind Kind { get; }
    public string Name { get; set; }
    public string Value { get; set; }
    public Dictionary<string, string> Attributes { get; set; }
    public IReadOnlyList<XNode> Children => _children;
    public XNode? Parent { get; private set; }
    /// A root element: the comments and instructions before it.
    public List<XNode> Prolog { get; set; } = [];

    public XNode(XKind kind, string name = "", string value = "", Dictionary<string, string>? attributes = null)
    {
        Kind = kind;
        Name = name;
        Value = value;
        Attributes = attributes == null ? [] : new(attributes);
    }

    public XNode(string name, Dictionary<string, string>? attributes = null, IEnumerable<XNode>? children = null)
        : this(XKind.Element, name, attributes: attributes)
    {
        if (children != null) foreach (var c in children) Append(c);
    }

    /// A w:t element with this text, keeping spaces at its ends.
    public static XNode TextElement(string name, string text)
    {
        var e = new XNode(name);
        e.Text = text;
        return e;
    }

    // MARK: - reading

    public List<XNode> Elements => _children.Where(c => c.Kind == XKind.Element).ToList();

    public XNode? Find(string name) => _children.FirstOrDefault(c => c.Kind == XKind.Element && c.Name == name);
    public List<XNode> FindAll(string name) => _children.Where(c => c.Kind == XKind.Element && c.Name == name).ToList();
    public bool Has(string name) => Find(name) != null;

    public string? this[string attribute]
    {
        get => Attributes.GetValueOrDefault(attribute);
        set { if (value == null) Attributes.Remove(attribute); else Attributes[attribute] = value; }
    }

    /// The w:val of this element.
    public string? Val => this["w:val"];

    /// This element and everything below it, in document order.
    public void Walk(Action<XNode> visit)
    {
        visit(this);
        for (int i = 0; i < _children.Count; i++) _children[i].Walk(visit);
    }

    /// The elements with one of these names below this one, in document order.
    public List<XNode> Descendants(params string[] names) => DescendantsIn([.. names]);

    public List<XNode> DescendantsIn(HashSet<string> names)
    {
        var output = new List<XNode>();
        foreach (var c in _children)
        {
            if (c.Kind == XKind.Element) c.Walk(e => { if (e.Kind == XKind.Element && names.Contains(e.Name)) output.Add(e); });
        }
        return output;
    }

    /// The directly contained text; setting it replaces all content.
    public string Text
    {
        get => string.Concat(_children.Where(c => c.Kind == XKind.Text).Select(c => c.Value));
        set
        {
            foreach (var c in _children) c.Parent = null;
            _children = value.Length == 0 ? [] : [new XNode(XKind.Text, value: value)];
            if (_children.Count > 0) _children[0].Parent = this;
            if (Name is "w:t" or "w:instrText")
            {
                if (value != value.TrimWSNL()) Attributes["xml:space"] = "preserve";
            }
        }
    }

    /// All text below this element.
    public string AllText
    {
        get
        {
            var output = new StringBuilder();
            Walk(e => { if (e.Kind == XKind.Text) output.Append(e.Value); });
            return output.ToString();
        }
    }

    public bool HasAncestor(string name, XNode? below = null)
    {
        var a = Parent;
        while (a != null && !ReferenceEquals(a, below))
        {
            if (a.Name == name) return true;
            a = a.Parent;
        }
        return false;
    }

    // MARK: - changing

    public XNode Append(XNode child)
    {
        child.RemoveFromParent();
        child.Parent = this;
        _children.Add(child);
        return child;
    }

    /// A new element at the end of this one.
    public XNode Add(string name, Dictionary<string, string>? attributes = null) => Append(new XNode(name, attributes));

    public void Insert(XNode child, int at)
    {
        child.RemoveFromParent();
        child.Parent = this;
        _children.Insert(Math.Min(at, _children.Count), child);
    }

    public int? IndexOf(XNode child)
    {
        int i = _children.FindIndex(c => ReferenceEquals(c, child));
        return i < 0 ? null : i;
    }

    public void InsertBefore(XNode reference, XNode child)
    {
        if (IndexOf(reference) is not int i) { Append(child); return; }
        Insert(child, i);
    }

    public void InsertAfter(XNode reference, XNode child)
    {
        if (IndexOf(reference) is not int i) { Append(child); return; }
        Insert(child, i + 1);
    }

    public void Remove(XNode child)
    {
        if (IndexOf(child) is not int i) return;
        _children.RemoveAt(i);
        child.Parent = null;
    }

    public void RemoveAll(string name) { foreach (var e in FindAll(name)) Remove(e); }

    public void RemoveFromParent() => Parent?.Remove(this);

    public XNode Copy()
    {
        var c = new XNode(Kind, Name, Value, Attributes);
        foreach (var child in _children) c.Append(child.Copy());
        return c;
    }

    // MARK: - text form

    public byte[] Serialized()
    {
        var output = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        foreach (var p in Prolog) p.WriteTo(output);
        WriteTo(output);
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    /// Only for comparing two pieces of formatting.
    public string Signature
    {
        get
        {
            var output = new StringBuilder();
            WriteTo(output);
            return output.ToString();
        }
    }

    private void WriteTo(StringBuilder output)
    {
        switch (Kind)
        {
            case XKind.Text:
                output.Append(Escape(Value));
                break;
            case XKind.Comment:
                output.Append("<!--").Append(Value).Append("-->");
                break;
            case XKind.Instruction:
                output.Append("<?").Append(Name).Append(Value.Length == 0 ? "" : " " + Value).Append("?>");
                break;
            case XKind.Element:
                output.Append('<').Append(Name);
                // a dictionary gives no order: the namespace declarations first, then by name, so the result does not vary
                var keys = Attributes.Keys.ToList();
                keys.Sort((a, b) =>
                {
                    bool x = a.HasPrefix("xmlns"), y = b.HasPrefix("xmlns");
                    return x == y ? string.CompareOrdinal(a, b) : (x ? -1 : 1);
                });
                foreach (var k in keys) output.Append(' ').Append(k).Append("=\"").Append(EscapeAttribute(Attributes[k])).Append('"');
                if (_children.Count == 0) { output.Append("/>"); return; }
                output.Append('>');
                foreach (var c in _children) c.WriteTo(output);
                output.Append("</").Append(Name).Append('>');
                break;
        }
    }

    private static string Escape(string s)
    {
        if (s.IndexOfAny(['&', '<', '>', '\r']) < 0) return s;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", "&#13;");
    }

    private static string EscapeAttribute(string s) =>
        Escape(s).Replace("\"", "&quot;").Replace("\n", "&#10;").Replace("\t", "&#9;");

    // MARK: - parsing

    public static XNode Parse(byte[] data)
    {
        XNode? root = null;
        var stack = new List<XNode>();
        var prolog = new List<XNode>();

        // the parser may report one text in pieces
        void AddText(string s)
        {
            if (stack.Count == 0) return;
            var top = stack[^1];
            if (top._children.Count > 0 && top._children[^1].Kind == XKind.Text) top._children[^1].Value += s;
            else top.Append(new XNode(XKind.Text, value: s));
        }

        void Add(XNode node)
        {
            if (stack.Count > 0) stack[^1].Append(node);
            else if (root == null) prolog.Add(node);
        }

        try
        {
            // the reader, not XDocument: without namespaces it gives every name and attribute as written, xmlns too
            using var reader = new XmlTextReader(new MemoryStream(data))
            {
                Namespaces = false,
                Normalization = true,
                WhitespaceHandling = WhitespaceHandling.All,
                DtdProcessing = DtdProcessing.Ignore,
                EntityHandling = EntityHandling.ExpandEntities,
                XmlResolver = null,
            };
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var e = new XNode(XKind.Element, reader.Name);
                        bool empty = reader.IsEmptyElement;
                        for (bool more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
                            e.Attributes[reader.Name] = reader.Value;
                        reader.MoveToElement();
                        if (stack.Count > 0) stack[^1].Append(e);
                        else if (root == null) { root = e; e.Prolog = prolog; }
                        else throw new Zip.Failure("XML");
                        if (!empty) stack.Add(e);
                        break;
                    case XmlNodeType.EndElement:
                        stack.RemoveAt(stack.Count - 1);
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                        if (stack.Count == 0) throw new Zip.Failure("XML");
                        AddText(reader.Value);
                        break;
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        AddText(reader.Value);
                        break;
                    case XmlNodeType.Comment:
                        Add(new XNode(XKind.Comment, value: reader.Value));
                        break;
                    case XmlNodeType.ProcessingInstruction:
                        Add(new XNode(XKind.Instruction, reader.Name, reader.Value));
                        break;
                }
            }
        }
        catch (Exception e) when (e is XmlException or ArgumentException or DecoderFallbackException or InvalidOperationException)
        {
            throw new Zip.Failure(e.Message);
        }
        if (root == null) throw new Zip.Failure("XML");
        return root;
    }
}

/// Regular expressions on the UTF-16 offsets Foundation uses, so positions can be compared across runs of text.
/// The patterns are those of NSRegularExpression: Singleline stands for .dotMatchesLineSeparators and Multiline for
/// .anchorsMatchLines, and . ^ $ treat \r and the other line terminators as ICU does, also on \r\n files.
public sealed class Rx
{
    public sealed record Match(NSRange Range, IReadOnlyList<string?> Groups)
    {
        public string? First => Groups.FirstOrDefault(g => g != null);
    }

    private const string Terminators = @"\n\r\x85\p{Zl}\p{Zp}";
    private readonly Regex _re;

    public Rx(string pattern, RegexOptions options = RegexOptions.None)
    {
        bool lines = options.HasFlag(RegexOptions.Multiline);
        bool dotAll = options.HasFlag(RegexOptions.Singleline);
        _re = new Regex(Translate(pattern, dotAll, lines), (options & ~RegexOptions.Multiline) | RegexOptions.CultureInvariant);
    }

    private static string Translate(string pattern, bool dotAll, bool lines)
    {
        var output = new StringBuilder();
        bool inClass = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length) { output.Append(c).Append(pattern[++i]); continue; }
            if (inClass)
            {
                if (c == ']') inClass = false;
                output.Append(c);
                continue;
            }
            switch (c)
            {
                case '[':
                    inClass = true;
                    output.Append(c);
                    break;
                case '.':
                    output.Append(dotAll ? "." : $"[^{Terminators}]");
                    break;
                case '^':
                    output.Append(lines ? $@"(?:\A|(?<=[{Terminators}])(?<!\r(?=\n))(?!\z))" : "^");
                    break;
                case '$':
                    output.Append(lines ? $@"(?=\r\n|[{Terminators}]|\z)(?<!\r(?=\n))" : $@"(?=(?:\r\n|[{Terminators}])?\z)(?<!\r(?=\n\z))");
                    break;
                default:
                    output.Append(c);
                    break;
            }
        }
        return output.ToString();
    }

    public List<Match> Matches(string s)
    {
        var output = new List<Match>();
        for (var m = _re.Match(s); m.Success; m = m.NextMatch())
        {
            var groups = new string?[Math.Max(m.Groups.Count - 1, 0)];
            for (int i = 1; i < m.Groups.Count; i++) groups[i - 1] = m.Groups[i].Success ? m.Groups[i].Value : null;
            output.Add(new Match(new NSRange(m.Index, m.Length), groups));
        }
        return output;
    }

    public Match? First(string s)
    {
        var all = Matches(s);
        return all.Count > 0 ? all[0] : null;
    }

    public bool Test(string s) => _re.IsMatch(s);

    /// Every match replaced by what `with` gives.
    public string Replace(string s, Func<Match, string> with)
    {
        var output = new StringBuilder();
        int at = 0;
        foreach (var m in Matches(s))
        {
            output.Append(s, at, m.Range.Location - at).Append(with(m));
            at = m.Range.Location + m.Range.Length;
        }
        return output.Append(s, at, s.Length - at).ToString();
    }
}
