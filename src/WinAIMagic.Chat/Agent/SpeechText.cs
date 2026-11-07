using System.Text.RegularExpressions;

namespace WinAIMagic.Chat.Agent;

/// <summary>What is read aloud of an answer.</summary>
public static class SpeechText
{
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex TableLine = new(@"(?m)^\s*\|?[-: |]+\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex Symbols = new(@"[*_`#>|]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t]+", RegexOptions.Compiled);

    /// <summary>Markdown to plain sentences: no code blocks, links as their text, no symbols read out.</summary>
    public static string Speakable(string markdown)
    {
        var parts = markdown.Split("```").Where((_, i) => i % 2 == 0);
        var s = string.Join(" ", parts);
        s = Link.Replace(s, "$1");
        s = Url.Replace(s, "");
        s = TableLine.Replace(s, "");
        s = Symbols.Replace(s, " ");
        s = Spaces.Replace(s, " ");
        return s.Trim();
    }

    /// <summary>The app's language as a speech language.</summary>
    public static string Language(string app) => app switch
    {
        "nl" => "nl-NL",
        "de" => "de-DE",
        "fr" => "fr-FR",
        _ => "en-US",
    };
}
