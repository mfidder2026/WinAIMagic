using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechRecognition;
using Windows.Media.SpeechSynthesis;
using WinAIMagic.Chat.Agent;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App;

/// <summary>Speech (Instellingen → Agent): reading answers aloud with Windows' own voices, on this PC, and speaking a question
/// through Windows' dictation. Windows dictates through Microsoft's online speech service, so that needs "Online speech
/// recognition" switched on in Windows; reading aloud stays on the PC.</summary>
public sealed class Speech
{
    public static Speech Shared { get; } = new();

    private readonly MediaPlayer player = new();
    private SpeechSynthesizer? synth;
    private SpeechRecognizer? recognizer;
    private string confirmed = "";

    /// <summary>The answer being read aloud now.</summary>
    public Guid? Speaking { get; private set; }
    public bool Listening { get; private set; }
    /// <summary>What was heard so far (while listening).</summary>
    public string Heard { get; private set; } = "";
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

    /// <summary>Starts listening; every change of what was heard goes to onText. Stop with StopListening.</summary>
    public async Task StartListening(Action<string> onText)
    {
        Problem = null;
        Heard = confirmed = "";
        try
        {
            var language = SpeechRecognizer.SupportedTopicLanguages.FirstOrDefault(l => l.LanguageTag.Equals(Lang, StringComparison.OrdinalIgnoreCase))
                ?? SpeechRecognizer.SupportedTopicLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith(Lang[..2], StringComparison.OrdinalIgnoreCase));
            if (language == null)
            {
                Problem = L("Windows-dictee is er niet voor deze taal op deze pc.");
                Changed?.Invoke();
                return;
            }
            var r = new SpeechRecognizer(language);
            r.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            var compiled = await r.CompileConstraintsAsync();
            if (compiled.Status != SpeechRecognitionResultStatus.Success) throw new InvalidOperationException(compiled.Status.ToString());
            r.HypothesisGenerated += (_, e) =>
            {
                Heard = (confirmed + " " + e.Hypothesis.Text).Trim();
                onText(Heard);
                Changed?.Invoke();
            };
            r.ContinuousRecognitionSession.ResultGenerated += (_, e) =>
            {
                if (e.Result.Status != SpeechRecognitionResultStatus.Success || e.Result.Text.Length == 0) return;
                confirmed = (confirmed + " " + e.Result.Text).Trim();
                Heard = confirmed;
                onText(Heard);
                Changed?.Invoke();
            };
            r.ContinuousRecognitionSession.Completed += (_, e) =>
            {
                if (e.Status != SpeechRecognitionResultStatus.Success && Heard.Length == 0 && Listening)
                    Problem = Explain(e.Status.ToString(), 0);
                Listening = false;
                Changed?.Invoke();
            };
            await r.ContinuousRecognitionSession.StartAsync();
            recognizer = r;
            Listening = true;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Problem = Explain(e.Message, e.HResult);
            App.Log(e);
        }
        Changed?.Invoke();
    }

    /// <summary>The recognizer's error in plain words, with what to do. The user changes Windows' settings, not the app.</summary>
    public static string Explain(string message, int hresult)
    {
        // 0x80045509: the online speech privacy policy is not accepted
        if (hresult == unchecked((int)0x80045509) || message.Contains("privacy", StringComparison.OrdinalIgnoreCase))
            return L("Windows-dictee staat uit. Zet in Windows Instellingen → Privacy en beveiliging → Spraak de online spraakherkenning aan, en probeer het opnieuw. Wat je zegt gaat dan via de spraakdienst van Microsoft.");
        if (hresult == unchecked((int)0x80070005) || message.Contains("denied", StringComparison.OrdinalIgnoreCase))
            return L("De microfoon is niet toegestaan. Zet in Windows Instellingen → Privacy en beveiliging → Microfoon de toegang voor bureaublad-apps aan.");
        if (message.Contains("TimeoutExceeded") || message.Contains("NoMatch"))
            return L("Er werd niets verstaan. Spreek iets dichter bij de microfoon, en druk pas daarna op stoppen.");
        return L($"De spraakherkenning stopte: {message}");
    }

    public async void StopListening()
    {
        if (!Listening) return;
        Listening = false;
        Changed?.Invoke();
        var r = recognizer;
        recognizer = null;
        if (r == null) return;
        try { await r.ContinuousRecognitionSession.StopAsync(); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        r.Dispose();
    }
}
