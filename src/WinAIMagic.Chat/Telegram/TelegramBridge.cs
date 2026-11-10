using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat.Telegram;

/// <summary>Telegram as a way into the chat: each paired account has one conversation that goes on ("✈︎ Telegram · name"),
/// the answer grows in Telegram while the model writes, an approval comes as a card with buttons (never one that needs
/// administrator rights: that stays on the PC), and tasks and the heartbeat report there too.</summary>
public sealed class TelegramBridge
{
    private readonly TelegramService service;
    private readonly ChatClient chat;
    private readonly ConversationStore store;

    /// <summary>The engine's address, whether the model runs, loading it (false: it cannot), a short state for /status.</summary>
    public Func<Uri> BaseUrl { get; set; }
    public Func<bool> ModelRunning { get; set; }
    public Func<Task<bool>> EnsureModel { get; set; }
    public Func<string> StatusText { get; set; }
    /// <summary>How often the answer in Telegram is brought up to date while the model writes.</summary>
    public TimeSpan EditInterval { get; set; } = TimeSpan.FromMilliseconds(1500);
    /// <summary>Speech to text for voice messages (a file in, the text or why not); null: not available.</summary>
    public Func<string, Task<(string? Text, string? Error)>>? Transcribe { get; set; }
    /// <summary>An approval that needs administrator rights: the PC says so (a notification), title, text, conversation.</summary>
    public Action<string, string, Guid>? AdminNotice { get; set; }

    private sealed record Card(long Chat, int Message, Guid Conversation, string Text);
    private readonly object sync = new();
    /// <summary>The approval cards in Telegram that still have their buttons, by approval id.</summary>
    private readonly Dictionary<Guid, Card> cards = [];
    /// <summary>The runs whose answer goes to Telegram now.</summary>
    private readonly Dictionary<Guid, CancellationTokenSource> streams = [];

    public TelegramBridge(TelegramService service, ChatClient chat, ConversationStore store,
        Func<Uri> baseUrl, Func<bool> modelRunning, Func<Task<bool>> ensureModel, Func<string> statusText)
    {
        this.service = service;
        this.chat = chat;
        this.store = store;
        BaseUrl = baseUrl;
        ModelRunning = modelRunning;
        EnsureModel = ensureModel;
        StatusText = statusText;
        service.OnMessage = (m, a) => _ = Handle(m, a);
        service.OnCallback = (q, a) => _ = Pressed(q, a);
    }

    public static string Title(TelegramAccount account) => "✈︎ Telegram · " + account.Name;

    /// <summary>The account's conversation: the newest one it has, or a new one.</summary>
    public Guid ConversationFor(TelegramAccount account, bool fresh = false)
    {
        if (!fresh && store.Conversations.ToList().Find(c => c.Telegram == account.Id) is { } known) return known.Id;
        var c = new Conversation { Title = Title(account), Telegram = account.Id };
        store.Insert(c);
        store.Save();
        return c.Id;
    }

    private Conversation? Newest(long account) => store.Conversations.ToList().Find(c => c.Telegram == account);

    private static async Task Try(Func<Task> call)
    {
        try { await call(); } catch (TelegramException) { }
    }

    private static async Task<int?> TrySend(Func<Task<int>> call)
    {
        try { return await call(); } catch (TelegramException) { return null; }
    }

    // messages

    public async Task Handle(TelegramBot.Message m, TelegramAccount account)
    {
        if (service.Bot is not { } bot) return;
        long chatId = m.Chat.Id;
        var text = (m.Text ?? m.Caption ?? "").Trim();
        if (text.StartsWith('/'))
        {
            await Command(text, account, bot, chatId);
            return;
        }
        var prompt = text;
        if (m.Voice is { } voice)
        {
            if (Transcribe is not { } transcribe)
            {
                await Try(() => bot.Send(chatId, L("Spraakberichten kan WinAIMagic hier niet verstaan. Typ je vraag, of stuur een foto.")));
                return;
            }
            if (!Settings.Shared.FeatureVoice)
            {
                await Try(() => bot.Send(chatId, L("Spraakberichten staan uit op de pc (Instellingen → Agent → Spraak). Typ je vraag, of stuur een foto.")));
                return;
            }
            await bot.Typing(chatId);
            (string? Text, string? Error) heard;
            var file = Path.Combine(Path.GetTempPath(), $"telegram-{Guid.NewGuid():N}.ogg");
            try
            {
                await File.WriteAllBytesAsync(file, await bot.Download(voice.FileId));
                heard = await transcribe(file);
            }
            catch (Exception e) when (e is TelegramException or IOException or UnauthorizedAccessException) { heard = (null, e.Message); }
            finally { try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
            if (heard.Text is not { Length: > 0 } words)
            {
                await Try(() => bot.Send(chatId, L($"Ik kon het spraakbericht niet verstaan: {heard.Error ?? ""}")));
                return;
            }
            await Try(() => bot.Send(chatId, "🎙 " + words));
            prompt = words + (prompt.Length == 0 ? "" : "\n\n" + prompt);
        }
        var attachments = new List<Attachment>();
        if (m.Photo is { Count: > 0 } photos)
        {
            if (!Settings.Shared.Vision)
            {
                await Try(() => bot.Send(chatId, L("Foto's bekijken staat uit (Instellingen → Chat → Afbeeldingen).")));
                return;
            }
            var photo = photos.MaxBy(p => (long)p.Width * p.Height)!;
            try
            {
                var data = await bot.Download(photo.FileId);
                attachments.Add(await ImageScaler.MakeAsync(data, "telegram.jpg") ?? new Attachment { Filename = "telegram.jpg", Jpeg = data });
            }
            catch (TelegramException e)
            {
                await Try(() => bot.Send(chatId, L($"De foto kwam niet over: {e.Message}")));
                return;
            }
        }
        if (m.Document is { } doc)
        {
            string? body = null;
            if ((doc.FileSize ?? 0) <= 1_000_000)
            {
                try
                {
                    var data = await bot.Download(doc.FileId);
                    var s = new System.Text.UTF8Encoding(false, true).GetString(data);
                    if (!s.Contains('\0')) body = s;
                }
                catch (Exception e) when (e is TelegramException or System.Text.DecoderFallbackException or ArgumentException) { }
            }
            if (body == null)
            {
                await Try(() => bot.Send(chatId, L("Dit soort bestand kan ik via Telegram nog niet lezen. Stuur tekst (tot 1 MB) of een foto.")));
                return;
            }
            prompt += (prompt.Length == 0 ? "" : "\n\n") + $"[{doc.FileName ?? "bestand"}]\n```\n{body}\n```";
        }
        if (prompt.Length == 0 && attachments.Count == 0) return;
        var id = ConversationFor(account);
        if (chat.IsBusy(id))
        {
            await Try(() => bot.Send(chatId, L("Ik ben nog bezig met je vorige vraag. /stop stopt die.")));
            return;
        }
        if (!ModelRunning())
        {
            await Try(() => bot.Send(chatId, L("Model laden… dat kan een paar minuten duren.")));
            if (!await EnsureModel())
            {
                await Try(() => bot.Send(chatId, L("Het model kon niet starten. Kijk op de pc bij Modellen.")));
                return;
            }
        }
        // the model may run without vision (not downloaded, or no room for it): then the photo is not looked at
        if (attachments.Count > 0 && !ChatClient.SeesImages())
        {
            await Try(() => bot.Send(chatId, L("Het model draait nu zonder afbeeldingen (vision), dus de foto kan ik niet bekijken.")));
            attachments.Clear();
            if (prompt.Length == 0) return;
        }
        // a second message while the model loaded passed the check above minutes ago: say so instead of starting a second run
        if (chat.IsBusy(id))
        {
            await Try(() => bot.Send(chatId, L("Ik ben nog bezig met je vorige vraag. /stop stopt die.")));
            return;
        }
        await bot.Typing(chatId);
        chat.Send(prompt, attachments, id, store, BaseUrl());
        Stream(id, chatId);
    }

    private async Task Command(string text, TelegramAccount account, TelegramBot bot, long chatId)
    {
        var word = text.Split(' ', 2)[0].ToLowerInvariant().Split('@')[0];
        switch (word)
        {
            case "/nieuw":
            case "/new":
                ConversationFor(account, fresh: true);
                await Try(() => bot.Send(chatId, L("Nieuw gesprek. Waar kan ik je mee helpen?")));
                break;
            case "/stop":
                if (Newest(account.Id) is { } c && chat.IsBusy(c.Id))
                {
                    chat.Stop(c.Id);
                    await Try(() => bot.Send(chatId, L("Gestopt.")));
                }
                else await Try(() => bot.Send(chatId, L("Er loopt nu niets.")));
                break;
            case "/status":
                await Try(() => bot.Send(chatId, StatusText()));
                break;
            default:
                await Try(() => bot.Send(chatId, L("Stuur gewoon je vraag; het model op je pc antwoordt. /nieuw begint een nieuw gesprek, /stop stopt het antwoord, /status zegt hoe de pc ervoor staat.")));
                break;
        }
    }

    // the answer

    /// <summary>The visible answer of this run: the newest assistant message after the last question, with its text or error.</summary>
    private ChatMessage? Answer(Guid id)
    {
        if (store.Find(id) is not { } c) return null;
        var messages = c.Messages.ToList();
        int q = messages.FindLastIndex(m => m.Role == ChatRole.User);
        if (q < 0) return null;
        return messages.Skip(q + 1).LastOrDefault(m => m.Role == ChatRole.Assistant && (m.Text.Length > 0 || m.Error != null));
    }

    /// <summary>Sends the answer as it grows (at most once per EditInterval), and in full when the run is done.</summary>
    public void Stream(Guid id, long chatId)
    {
        var cts = new CancellationTokenSource();
        lock (sync)
        {
            if (streams.Remove(id, out var old)) old.Cancel();
            streams[id] = cts;
        }
        _ = Streaming(id, chatId, cts);
    }

    private async Task Streaming(Guid id, long chatId, CancellationTokenSource cts)
    {
        if (service.Bot is not { } bot) return;
        (int Message, Guid For)? sent = null;
        var shown = "";
        var typed = DateTime.UtcNow;
        try
        {
            while (chat.IsBusy(id) && !cts.IsCancellationRequested)
            {
                await Task.Delay(EditInterval, cts.Token);
                if (Answer(id) is not { Error: null } a)
                {
                    if ((DateTime.UtcNow - typed).TotalSeconds > 4.5) { await bot.Typing(chatId); typed = DateTime.UtcNow; }
                    continue;
                }
                var now = a.Text.Length > TelegramFormat.Limit ? TextLimit.Prefix(a.Text, TelegramFormat.Limit) + " …" : a.Text;
                if (now == shown && sent?.For == a.Id) continue;
                if (sent is { } s && s.For == a.Id) await Try(() => bot.Edit(chatId, s.Message, now + " …"));
                else if (await TrySend(() => bot.Send(chatId, now + " …")) is { } m) sent = (m, a.Id);
                shown = now;
            }
        }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        await Finish(id, chatId, bot, sent);
        lock (sync) if (streams.GetValueOrDefault(id) == cts) streams.Remove(id);
    }

    private async Task Finish(Guid id, long chatId, TelegramBot bot, (int Message, Guid For)? sent)
    {
        // the cards of this run that nobody answered in Telegram lose their buttons
        List<KeyValuePair<Guid, Card>> open;
        lock (sync) open = [.. cards.Where(k => k.Value.Conversation == id)];
        foreach (var (key, card) in open)
        {
            await Try(() => bot.Edit(card.Chat, card.Message, card.Text));
            lock (sync) cards.Remove(key);
        }
        if (Answer(id) is not { } a)
        {
            await Try(() => bot.Send(chatId, L("(geen antwoord)")));
            return;
        }
        if (a.Error is { } error)
        {
            await Try(() => bot.Send(chatId, "⚠️ " + error));
            return;
        }
        var parts = TelegramFormat.Chunks(a.Text);
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (i == 0 && sent is { } s && s.For == a.Id) await Try(() => bot.Edit(chatId, s.Message, part, TelegramFormat.Html(part)));
            else await Try(() => bot.Send(chatId, part, TelegramFormat.Html(part)));
        }
    }

    // approvals

    /// <summary>A run waits for a yes: a card in Telegram for a Telegram conversation, and for a task when its results go to
    /// Telegram. Administrator rights are only approved on the PC.</summary>
    public void Approval(Approval a, bool isTask)
    {
        // also between tries after a passing network problem: a card must not get lost
        if (service.Bot is not { } bot || !service.IsRunning) return;
        var c = store.Find(a.Conversation);
        long? target = c?.Telegram ?? (isTask && Settings.Shared.TelegramNotify ? service.NotifyTarget?.Id : null);
        if (target is not { } to) return;
        var name = c?.DisplayTitle ?? "";
        var lines = new List<string> { isTask ? L($"Taak '{name}' vraagt toestemming:") : L("Toestemming nodig:"), a.Title };
        if (a.Detail.Length > 0) lines.Add(TextLimit.Prefix(a.Detail, 1500));
        lines.AddRange(a.Warnings.Select(w => "⚠️ " + w));
        foreach (var step in a.Steps.Take(10)) lines.Add("• " + step.Title + (step.Admin ? " (" + L("beheerder") + ")" : ""));
        var text = string.Join("\n", lines);
        _ = SendCard(bot, to, a, name, text);
        if (!isTask)
        {
            // as with a task: no answer within a day is a no
            _ = Task.Delay(TimeSpan.FromHours(24)).ContinueWith(_ =>
            {
                if (chat.ApprovalFor(a.Conversation) is { } still && still.Id == a.Id) chat.Answer(still, false);
            }, TaskScheduler.Default);
        }
    }

    private async Task SendCard(TelegramBot bot, long to, Approval a, string name, string text)
    {
        if (a.Admin)
        {
            await Try(() => bot.Send(to, text + "\n\n" + L("Dit vraagt beheerdersrechten; keur het goed op de pc.")));
            AdminNotice?.Invoke(L($"Goedkeuring nodig: {name}"), a.Title, a.Conversation);
            return;
        }
        var key = a.Id.ToString();
        var buttons = new List<IReadOnlyList<TelegramBot.Button>> { new[] { new TelegramBot.Button(L("Toestaan"), "a:" + key), new TelegramBot.Button(L("Weigeren"), "n:" + key) } };
        if (!a.EveryTime) buttons.Add(new[] { new TelegramBot.Button(L("Altijd in dit gesprek"), "t:" + key) });
        if (await TrySend(() => bot.Send(to, text, buttons: buttons)) is not { } m) return;
        // the approval may have been answered on the PC meanwhile
        if (chat.ApprovalFor(a.Conversation)?.Id == a.Id)
        {
            lock (sync) cards[a.Id] = new Card(to, m, a.Conversation, text);
        }
        else await Try(() => bot.Edit(to, m, text));
    }

    public async Task Pressed(TelegramBot.CallbackQuery q, TelegramAccount account)
    {
        if (service.Bot is not { } bot) return;
        var parts = (q.Data ?? "").Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var id)) { await bot.AnswerCallback(q.Id); return; }
        Card? card;
        lock (sync) card = cards.GetValueOrDefault(id);
        if (card == null || card.Chat != account.Id || chat.ApprovalFor(card.Conversation) is not { } approval || approval.Id != id)
        {
            await bot.AnswerCallback(q.Id, L("Deze vraag is al beantwoord."));
            if (q.Message is { } msg) await Try(() => bot.Edit(msg.Chat.Id, msg.MessageId, msg.Text ?? ""));
            lock (sync) cards.Remove(id);
            return;
        }
        lock (sync) cards.Remove(id);
        bool allow = parts[0] != "n";
        bool always = parts[0] == "t" && !approval.EveryTime;
        chat.Answer(approval, allow, always);
        var result = always ? L("Altijd toegestaan in dit gesprek") : allow ? L("Toegestaan") : L("Geweigerd");
        await bot.AnswerCallback(q.Id, result);
        await Try(() => bot.Edit(card.Chat, card.Message, card.Text + "\n\n" + (allow ? "✅ " : "❌ ") + result));
    }

    // notifications

    /// <summary>A task or the heartbeat has something to say: also to Telegram, when that is on.</summary>
    public void Notify(string title, string body)
    {
        if (!Settings.Shared.TelegramNotify || service.Bot is not { } bot || !service.IsRunning || service.NotifyTarget is not { } to) return;
        var text = title + (body.Length == 0 ? "" : "\n" + body);
        _ = Try(() => bot.Send(to.Id, TextLimit.Prefix(text, TelegramFormat.Limit)));
    }
}
