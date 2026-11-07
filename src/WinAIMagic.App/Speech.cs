using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using WinAIMagic.Chat.Agent;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App;

/// <summary>Speech (Instellingen → Agent): reading answers aloud with Windows' own voices, and speaking a question, which the speech
/// engine turns into text. Both stay on this PC.</summary>
public sealed class Speech
{
    public static Speech Shared { get; } = new();

    private readonly MediaPlayer player = new();
    private SpeechSynthesizer? synth;

    /// <summary>The answer being read aloud now.</summary>
    public Guid? Speaking { get; private set; }
    public bool Listening { get; private set; }
    /// <summary>Why listening did not work, in words for the user.</summary>
    public string? Problem { get; set; }
    /// <summary>Speaking, listening, what was heard or a problem changed. On any thread.</summary>
    public event Action? Changed;

    private Speech()
    {
        player.MediaEnded += (_, _) => { Speaking = null; Changed?.Invoke(); };
        player.MediaFailed += (_, _) => { Speaking = null; Changed?.Invoke(); };
    }

    private static string Lang => SpeechText.Language(Settings.Shared.Language);

    // reading aloud

    /// <summary>The voices of the app's language (Windows' own, installed with the language), for the settings.</summary>
    public static IReadOnlyList<VoiceInformation> Voices
    {
        get
        {
            var prefix = Lang[..2];
            try { return [.. SpeechSynthesizer.AllVoices.Where(v => v.Language.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(v => v.DisplayName)]; }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { return []; }
        }
    }

    /// <summary>Reads an answer aloud, or stops when it is the one being read.</summary>
    public async void Speak(string text, Guid id)
    {
        if (Speaking == id) { StopSpeaking(); return; }
        player.Pause();
        try
        {
            synth ??= new SpeechSynthesizer();
            var chosen = Settings.Shared.VoiceId;
            synth.Voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == chosen) ?? Voices.FirstOrDefault() ?? SpeechSynthesizer.DefaultVoice;
            var stream = await synth.SynthesizeTextToStreamAsync(SpeechText.Speakable(text));
            Speaking = id;
            player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            player.Play();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Speaking = null;
            App.Log(e);
        }
        Changed?.Invoke();
    }

    public void StopSpeaking()
    {
        player.Pause();
        player.Source = null;
        Speaking = null;
        Changed?.Invoke();
    }

    // speaking a question

    /// <summary>Speech to text with the speech engine (AppState.Hear): the samples in, the text or why not.</summary>
    public Func<short[], Task<(string? Text, string? Error)>>? Hear { get; set; }
    /// <summary>The recording is with the speech engine now.</summary>
    public bool Understanding { get; private set; }
    /// <summary>The loudness of the last piece of sound, 0 to 1, while listening.</summary>
    public double Level { get; private set; }

    private Microphone? mic;
    private readonly List<short> recorded = new();
    private SpeechPause pause = new();
    private Action<string, bool>? onHeard;
    /// <summary>The longest question: two minutes.</summary>
    private const int MostSamples = Audio.Rate * 120;

    /// <summary>Starts listening. When a question is done (two seconds of quiet after speaking) or stopped, the speech engine makes text of
    /// it and onHeard gets it, with true when it was the pause: then the question goes by itself. onHeard comes on any thread.</summary>
    public Task StartListening(Action<string, bool> onHeard)
    {
        Problem = null;
        if (Listening || Understanding) return Task.CompletedTask;
        lock (recorded) recorded.Clear();
        pause = new SpeechPause();
        this.onHeard = onHeard;
        var m = new Microphone();
        m.OnPiece = Piece;
        if (m.Start() is int code)
        {
            m.Dispose();
            Problem = code == 2 || code == 6
                ? L("Er is geen microfoon gevonden. Sluit er een aan, of kies hem in Windows Instellingen → Systeem → Geluid.")
                : L($"De microfoon kon niet worden geopend (fout {code}). Zet in Windows Instellingen → Privacy en beveiliging → Microfoon de toegang voor bureaublad-apps aan.");
            Changed?.Invoke();
            return Task.CompletedTask;
        }
        mic = m;
        Listening = true;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    private void Piece(short[] piece)
    {
        bool done, full;
        lock (recorded)
        {
            recorded.AddRange(piece);
            full = recorded.Count >= MostSamples;
        }
        Level = Audio.Level(piece);
        done = pause.Add(Level, TimeSpan.FromSeconds(piece.Length / (double)Audio.Rate));
        Changed?.Invoke();
        if (done || full) _ = Finish(send: done);
    }

    /// <summary>Stops listening; what was said goes to the speech engine and into the field, without sending.</summary>
    public void StopListening() => _ = Finish(send: false);

    private async Task Finish(bool send)
    {
        short[] samples;
        lock (recorded)
        {
            if (!Listening) return;
            Listening = false;
            samples = recorded.ToArray();
            recorded.Clear();
        }
        var m = mic;
        mic = null;
        // the microphone's own thread may be the one asking
        _ = Task.Run(() => m?.Dispose());
        Level = 0;
        bool silent = !pause.HeardSpeech && samples.Length > 0 && Audio.Level(samples) < 0.0005;
        if (silent)
        {
            Problem = L("De microfoon gaf alleen stilte. Staat hij uit, of mogen bureaublad-apps hem niet gebruiken (Windows Instellingen → Privacy en beveiliging → Microfoon)?");
            Changed?.Invoke();
            return;
        }
        if (Hear is not { } hear) return;
        Understanding = true;
        Changed?.Invoke();
        try
        {
            var (text, error) = await hear(samples);
            if (text is { Length: > 0 }) onHeard?.Invoke(text, send);
            else Problem = error ?? L("Er werd niets verstaan.");
        }
        finally
        {
            Understanding = false;
            Changed?.Invoke();
        }
    }
}
