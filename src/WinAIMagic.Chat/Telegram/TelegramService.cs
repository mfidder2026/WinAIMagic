using System.Security.Cryptography;
using System.Text.Json;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat.Telegram;

/// <summary>A Telegram account that may talk to this PC: it sent the pairing code once.</summary>
public sealed record TelegramAccount(long Id, string Name);

/// <summary>Who may talk to the bot. Only accounts that sent the pairing code, in a private chat; everybody else is ignored
/// without an answer, so a stranger cannot even tell that the bot is there.</summary>
public sealed class TelegramAccess
{
    public const double CodeMinutes = 10;
    public List<TelegramAccount> Accounts { get; set; } = [];
    public string? Code { get; private set; }
    public DateTimeOffset? CodeUntil { get; private set; }

    public enum Kind { Allowed, Paired, Ignored }
    public readonly record struct Decision(Kind Kind, TelegramAccount? Account = null);

    public string NewCode(DateTimeOffset? now = null)
    {
        Code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        CodeUntil = (now ?? DateTimeOffset.Now).AddMinutes(CodeMinutes);
        return Code;
    }

    public void CancelCode() { Code = null; CodeUntil = null; }

    public bool IsAllowed(long id) => Accounts.Exists(a => a.Id == id);

    public Decision Decide(TelegramBot.Message message, DateTimeOffset? now = null)
    {
        if (message.Chat.Type != "private" || message.From is not { } from || from.IsBot == true || message.Chat.Id != from.Id) return new(Kind.Ignored);
        if (Accounts.Find(a => a.Id == from.Id) is { } known) return new(Kind.Allowed, known);
        if (Code is not { } code || CodeUntil is not { } until || (now ?? DateTimeOffset.Now) >= until) return new(Kind.Ignored);
        var words = (message.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words[^1] != code || !(words.Length == 1 || (words.Length == 2 && words[0].StartsWith("/start", StringComparison.Ordinal))))
            return new(Kind.Ignored);
        var account = new TelegramAccount(from.Id, from.Name);
        Accounts.Add(account);
        CancelCode();
        return new(Kind.Paired, account);
    }
}

/// <summary>Keeps the connection with the bot: polls Telegram, lets in only the paired accounts, and hands their messages and
/// button presses to the bridge, on the owner's thread (the UI thread in the app).</summary>
public sealed class TelegramService
{
    public enum State { Off, NoToken, Connecting, Online, Problem }
    public readonly record struct Status(State State, string Text = "");

    /// <summary>The app's one service; tests make their own.</summary>
    public static TelegramService? Shared { get; set; }

    private readonly object sync = new();
    private Status status = new(State.Off);
    private readonly List<string> log = [];
    private readonly TelegramAccess access = new();
    private readonly string file;
    private int offset;
    private CancellationTokenSource? poll;
    private string? testToken;
    private int pollTimeout = 50;

    /// <summary>The thread messages and presses are handed over on (the UI thread); null: the thread pool.</summary>
    public SynchronizationContext? Owner { get; set; }
    /// <summary>A message or a button press from a paired account.</summary>
    public Action<TelegramBot.Message, TelegramAccount>? OnMessage { get; set; }
    public Action<TelegramBot.CallbackQuery, TelegramAccount>? OnCallback { get; set; }
    /// <summary>The status, the log or the accounts changed. On any thread.</summary>
    public event Action? Changed;
    /// <summary>Tests point at a stand-in.</summary>
    public Uri Base { get; private set; } = TelegramBot.Api;
    public HttpClient? Http { get; set; }

    private sealed class Saved
    {
        public List<TelegramAccount> Accounts { get; set; } = [];
        public int Offset { get; set; }
    }

    /// <param name="file">The accounts and the offset; the app's is telegram.json in the app data folder.</param>
    public TelegramService(string? file = null)
    {
        this.file = file ?? Paths.Telegram;
        try
        {
            if (File.Exists(this.file) && JsonSerializer.Deserialize<Saved>(File.ReadAllBytes(this.file), ChatJson.Options) is { } saved)
            {
                access.Accounts = saved.Accounts;
                offset = saved.Offset;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
    }

    /// <summary>For tests: a stand-in server, a token that never touches the Credential Manager, short polls.</summary>
    public void UseForTests(Uri baseUri, string token, int pollTimeout = 1)
    {
        Base = baseUri;
        testToken = token;
        this.pollTimeout = pollTimeout;
    }

    public string? Token => testToken ?? McpSecrets.Get(TelegramBot.SecretName);

    public void SetToken(string value)
    {
        var t = value.Trim();
        if (testToken != null) testToken = t;
        else if (t.Length == 0) McpSecrets.Delete(TelegramBot.SecretName);
        else McpSecrets.Set(TelegramBot.SecretName, t);
        lock (sync) offset = 0;
        Save();
        Restart();
    }

    public TelegramBot? Bot => Token is { Length: > 0 } t ? new TelegramBot(t, Base, Http) : null;
    public Status Current { get { lock (sync) return status; } }
    public bool IsOnline => Current.State == State.Online;
    /// <summary>Polling runs (online, or between tries after a problem): cards and notifications are worth sending.</summary>
    public bool IsRunning { get { lock (sync) return poll != null; } }
    public IReadOnlyList<TelegramAccount> Accounts { get { lock (sync) return [.. access.Accounts]; } }
    /// <summary>Where task results and the heartbeat go: the first account paired.</summary>
    public TelegramAccount? NotifyTarget { get { lock (sync) return access.Accounts.FirstOrDefault(); } }
    public IReadOnlyList<string> Log { get { lock (sync) return [.. log]; } }
    public int Offset { get { lock (sync) return offset; } }

    public void Note(string line)
    {
        lock (sync)
        {
            log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (log.Count > 60) log.RemoveRange(0, log.Count - 60);
        }
        Changed?.Invoke();
    }

    private void SetStatus(Status s)
    {
        lock (sync) status = s;
        Changed?.Invoke();
    }

    // pairing

    public string NewCode() { string c; lock (sync) c = access.NewCode(); Changed?.Invoke(); return c; }
    public void CancelCode() { lock (sync) access.CancelCode(); Changed?.Invoke(); }
    public (string Code, DateTimeOffset Until)? Code { get { lock (sync) return access.Code is { } c && access.CodeUntil is { } u ? (c, u) : null; } }

    public void Unpair(long id)
    {
        lock (sync) access.Accounts.RemoveAll(a => a.Id == id);
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        byte[] data;
        lock (sync) data = JsonSerializer.SerializeToUtf8Bytes(new Saved { Accounts = [.. access.Accounts], Offset = offset }, ChatJson.Options);
        try { AtomicFile.Write(file, data); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // polling

    public void Start()
    {
        lock (sync) if (poll != null) return;
        if (!Settings.Shared.FeatureTelegram && testToken == null) { SetStatus(new(State.Off)); return; }
        if (Bot is not { } bot) { SetStatus(new(State.NoToken)); return; }
        var cts = new CancellationTokenSource();
        lock (sync) poll = cts;
        SetStatus(new(State.Connecting));
        _ = Task.Run(() => Loop(bot, cts));
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (sync) { cts = poll; poll = null; }
        cts?.Cancel();
        SetStatus(new(Settings.Shared.FeatureTelegram && Token == null ? State.NoToken : State.Off));
    }

    public void Restart() { Stop(); Start(); }

    private async Task Loop(TelegramBot bot, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        double wait = 5;
        try
        {
            var me = await bot.Me(ct);
            SetStatus(new(State.Online, "@" + (me.Username ?? me.Name)));
            Note(L($"Verbonden als @{me.Username ?? me.Name}"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (TelegramException e)
        {
            Failed(e);
            if (e.Kind == TelegramException.Kinds.Unauthorized) { Ended(cts); return; }
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await bot.Updates(Offset, pollTimeout, ct);
                if (!IsOnline)
                {
                    try { var me = await bot.Me(ct); SetStatus(new(State.Online, "@" + (me.Username ?? me.Name))); }
                    catch (TelegramException) { }
                }
                wait = 5;
                foreach (var u in updates)
                {
                    lock (sync) offset = Math.Max(offset, u.UpdateId + 1);
                    Handle(u, bot);
                }
                if (updates.Count > 0) Save();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (TelegramException e)
            {
                Failed(e);
                if (e.Kind == TelegramException.Kinds.Unauthorized) break;
                try { await Task.Delay(TimeSpan.FromSeconds(e.Kind == TelegramException.Kinds.Conflict ? 30 : wait), ct); }
                catch (OperationCanceledException) { break; }
                wait = Math.Min(wait * 2, 60);
            }
        }
        Ended(cts);
    }

    private void Ended(CancellationTokenSource cts)
    {
        lock (sync) if (poll == cts) poll = null;
    }

    private void Failed(TelegramException e)
    {
        SetStatus(new(State.Problem, e.Message));
        Note(e.Message);
    }

    private void Dispatch(Action work)
    {
        if (Owner is { } owner) owner.Post(_ => work(), null);
        else _ = Task.Run(work);
    }

    /// <summary>One update: pairing, or a message or press of a paired account for the bridge; anything else is ignored.</summary>
    public void Handle(TelegramBot.Update u, TelegramBot bot)
    {
        if (u.Message is { } m)
        {
            TelegramAccess.Decision d;
            lock (sync) d = access.Decide(m);
            switch (d.Kind)
            {
                case TelegramAccess.Kind.Ignored:
                    Note(L($"Bericht van een onbekend account genegeerd (id {m.From?.Id ?? m.Chat.Id})."));
                    break;
                case TelegramAccess.Kind.Paired:
                    Save();
                    Note(L($"Gekoppeld: {d.Account!.Name}"));
                    _ = bot.Send(m.Chat.Id, L("Gekoppeld. Je kunt nu met WinAIMagic op je pc praten. /nieuw begint een nieuw gesprek, /stop stopt het antwoord, /status zegt hoe de pc ervoor staat."))
                        .ContinueWith(_ => { }, TaskScheduler.Default);
                    break;
                default:
                    var account = d.Account!;
                    Dispatch(() => OnMessage?.Invoke(m, account));
                    break;
            }
        }
        else if (u.CallbackQuery is { } q)
        {
            TelegramAccount? a;
            lock (sync) a = access.Accounts.Find(x => x.Id == q.From.Id);
            if (a == null) { _ = bot.AnswerCallback(q.Id); return; }
            Dispatch(() => OnCallback?.Invoke(q, a));
        }
    }
}
