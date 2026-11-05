using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>The model that writes the better description for an image, and on which engine. Taken over from the Mac (fad00e7).</summary>
public sealed record ImageDescriber(ModelInfo Model, bool OnFast)
{
    /// <summary>On the second engine, the fast model's (Snel); else the app's own, the heavy model's (Zwaar).</summary>
    public string Role => OnFast ? L("Snel") : L("Zwaar");

    /// <summary>The fast model writes it: small, it starts in seconds. Here it runs on the processor, so it can stay beside the image on the
    /// card. The heavy model only when there is no other, or when it runs already and the fast one does not fit beside it.
    /// `downloaded`: the chat models that are here and run on the second engine; `fits`: the fast one fits beside a running heavy one.</summary>
    public static ImageDescriber? Choose(ModelInfo? heavy, IReadOnlyList<ModelInfo> downloaded, string chosenFast, bool heavyRunning,
                                         ModelInfo? fastRunning, bool fits)
    {
        if (fastRunning != null) return new ImageDescriber(fastRunning, true);
        static ulong Size(ModelInfo m) => m.Memory ?? m.TotalSize;
        var others = downloaded.Where(m => m.Id != heavy?.Id).ToList();
        var chosen = others.FirstOrDefault(m => m.Id == chosenFast);
        var smallest = others.MinBy(Size) is { } s && (heavy == null || Size(s) < Size(heavy)) ? s : null;
        if ((chosen ?? smallest) is { } fast && (!(heavyRunning && !fits) || heavy == null)) return new ImageDescriber(fast, true);
        return heavy == null ? null : new ImageDescriber(heavy, false);
    }
}
