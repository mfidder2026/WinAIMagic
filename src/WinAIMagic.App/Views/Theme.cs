using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace WinAIMagic.App.Views;

/// <summary>Theme.swift and brief 10, literally.</summary>
public static class Theme
{
    public static readonly Color Accent = Rgb(0x5C, 0x7A, 0xFF), Violet = Rgb(0x9E, 0x5C, 0xFF), Cyan = Rgb(0x33, 0xDB, 0xF2),
        Pink = Rgb(0xFF, 0x5C, 0xAD), Mint = Rgb(0x4D, 0xF2, 0x9E), Amber = Rgb(0xFF, 0xB8, 0x40), Night = Rgb(0x09, 0x0A, 0x17);

    public const double CardRadius = 18, CardPadding = 18, PagePadding = 28, SectionSpacing = 18;

    public static readonly FontFamily Text = new("Segoe UI Variable Text"), Display = new("Segoe UI Variable Display"),
        Icons = new("Segoe Fluent Icons"), Mono = new("Cascadia Mono, Consolas");

    public static readonly Brush Primary = Solid(Colors.White, 0.95), Secondary = Solid(Colors.White, 0.62),
        Tertiary = Solid(Colors.White, 0.40), Hairline = Solid(Colors.White, 0.08);

    public static readonly Brush Brand = Gradient(new Point(0, 0), new Point(1, 1), Accent, Violet, Pink);
    public static readonly Brush Cool = Gradient(new Point(0, 0), new Point(1, 0), Cyan, Accent);
    public static readonly Brush UserBubble = Gradient(new Point(0, 0), new Point(1, 1), Alpha(Accent, 0.55), Alpha(Violet, 0.45));

    public static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
    public static Color Alpha(Color c, double a) => Color.FromArgb((byte)(c.A * a), c.R, c.G, c.B);
    public static SolidColorBrush Solid(Color c, double a = 1) => new(Alpha(c, a));

    public static LinearGradientBrush Gradient(Point from, Point to, params Color[] colors)
    {
        var g = new LinearGradientBrush { StartPoint = from, EndPoint = to };
        for (int i = 0; i < colors.Length; i++)
            g.GradientStops.Add(new GradientStop { Color = colors[i], Offset = colors.Length == 1 ? 0 : (double)i / (colors.Length - 1) });
        return g;
    }

    public static Color Family(string f) => f == "glm" ? Mint : f == "qwen" ? Amber : Accent;

    public static LinearGradientBrush FamilyGradient(string f) => f switch
    {
        "glm" => Gradient(new Point(0, 0), new Point(1, 1), Mint, Cyan),
        "qwen" => Gradient(new Point(0, 0), new Point(1, 1), Amber, Pink),
        _ => Gradient(new Point(0, 0), new Point(1, 1), Accent, Violet),
    };

    /// <summary>The colour at `f` (0..1) along a row of colours.</summary>
    public static Color Along(double f, params Color[] c)
    {
        double at = Math.Clamp(f, 0, 1) * (c.Length - 1);
        int i = Math.Min((int)at, c.Length - 2);
        double t = at - i;
        byte Mix(byte a, byte b) => (byte)(a + (b - a) * t);
        return Color.FromArgb(Mix(c[i].A, c[i + 1].A), Mix(c[i].R, c[i + 1].R), Mix(c[i].G, c[i + 1].G), Mix(c[i].B, c[i + 1].B));
    }

    /// <summary>Text on a filled button in this colour.</summary>
    public static Color OnColor(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 150 ? Night : Colors.White;
}

/// <summary>Segoe Fluent Icons.</summary>
public static class Glyph
{
    public const string Live = "", Models = "", Document = "", Compare = "", Cluster = "", System = "", Settings = "",
        NewChat = "", Play = "", Stop = "", Pause = "", Download = "", CloudDown = "", Check = "", Trash = "",
        Copy = "", Folder = "", Warning = "", Error = "", Done = "", Fixed = "", Add = "", Drive = "",
        Memory = "", Cpu = "", Bolt = "", Network = "", Refresh = "", Eye = "", Fast = "", Speed = "",
        Heat = "", Star = "", Chat = "", ChevronDown = "", ChevronUp = "", Key = "";
}
