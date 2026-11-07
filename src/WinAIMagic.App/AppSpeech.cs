using WinAIMagic.Chat.Agent;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;

namespace WinAIMagic.App;

/// <summary>Speech to text on this PC: a spoken question and Telegram voice messages go to the speech engine with the speech model.</summary>
public sealed partial class AppState
{
    public SpeechEngine SpeechEngine { get; } = new();

    public ModelInfo? SpeechModel => Host.Store.Catalog.Speech.FirstOrDefault();
    public bool SpeechModelReady => SpeechEngine.ModelPath(Host.Store) != null;
    /// <summary>How far the speech model's download is, while it runs; null: not downloading.</summary>
    public double? SpeechDownload => SpeechModel is { } m && Host.Store.Downloading.Contains(m.Id) ? Host.Store.StatusOf(m.Id).Fraction : null;

    /// <summary>The language the speech model listens for: the app's.</summary>
    private static string SpeechLanguage => SpeechText.Language(Settings.Shared.Language)[..2];

    /// <summary>16 kHz mono samples to text, or why not. The card is used when it has room now (a loaded model may fill it).</summary>
    public Task<(string? Text, string? Error)> Hear(short[] samples) => Task.Run(async () =>
    {
        if (SpeechEngine.ModelPath(Host.Store) is not { } model) return ((string?)null, (string?)Lang.L("Het spraakmodel is nog niet gedownload (Instellingen → Agent → Spraak)."));
        return await SpeechEngine.Transcribe(samples, model, SpeechLanguage, SystemInfo.Current(Host.Backend), Host.Backend);
    });

    /// <summary>A Telegram voice message (Ogg Opus) to text.</summary>
    public Task<(string? Text, string? Error)> HearFile(string path) => Task.Run(async () =>
    {
        if (SpeechEngine.ModelPath(Host.Store) is not { } model) return ((string?)null, (string?)Lang.L("Het spraakmodel is nog niet gedownload op de pc (Instellingen → Agent → Spraak)."));
        return await SpeechEngine.TranscribeFile(path, model, SpeechLanguage, SystemInfo.Current(Host.Backend), Host.Backend);
    });

    private void SetUpSpeech() => Speech.Shared.Hear = Hear;
}
