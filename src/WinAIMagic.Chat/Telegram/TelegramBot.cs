using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat.Telegram;

/// <summary>What went wrong talking to Telegram, in words for the user.</summary>
public sealed class TelegramException : Exception
{
    public enum Kinds { Unauthorized, Conflict, Api, Network }
    public Kinds Kind { get; }
    public int Code { get; }

    private TelegramException(Kinds kind, int code, string message) : base(message) { Kind = kind; Code = code; }

    /// <summary>401: the token is wrong or was revoked.</summary>
    public static TelegramException Unauthorized() =>
        new(Kinds.Unauthorized, 401, L("Telegram kent deze token niet. Maak een nieuwe bij @BotFather en plak hem opnieuw."));
    /// <summary>409: another program polls this bot.</summary>
    public static TelegramException Conflict() =>
        new(Kinds.Conflict, 409, L("Een ander programma gebruikt deze bot al (getUpdates). Stop dat, of maak een eigen bot voor WinAIMagic."));
    public static TelegramException Api(int code, string text) => new(Kinds.Api, code, $"Telegram {code}: {text}");
    public static TelegramException Network(string text) => new(Kinds.Network, 0, L($"Geen verbinding met Telegram: {text}"));
}

/// <summary>Telegram's Bot API, the parts WinAIMagic uses. The PC only calls out to api.telegram.org (long polling): nothing
/// listens on this PC, and no server is needed. The token is in the Credential Manager (TelegramBot.SecretName).</summary>
public sealed class TelegramBot(string token, Uri? baseUri = null, HttpClient? http = null)
{
    public const string SecretName = "telegram-bot";
    public static readonly Uri Api = new("https://api.telegram.org");
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    public string Token { get; } = token;
    public Uri Base { get; } = baseUri ?? Api;
    private readonly HttpClient http = http ?? SharedHttp;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // what Telegram sends

    public sealed record User(long Id, bool? IsBot = null, string? FirstName = null, string? LastName = null, string? Username = null)
    {
        [JsonIgnore]
        public string Name
        {
            get
            {
                var full = string.Join(" ", new[] { FirstName, LastName }.Where(s => !string.IsNullOrEmpty(s)));
                return full.Length > 0 ? full : Username is { } u ? "@" + u : Id.ToString();
            }
        }
    }
    public sealed record Chat(long Id, string Type);
    public sealed record PhotoSize(string FileId, int Width, int Height, int? FileSize = null);
    public sealed record Document(string FileId, string? FileName = null, string? MimeType = null, int? FileSize = null);
    public sealed record Voice(string FileId);
    public sealed record Message(int MessageId, User? From, Chat Chat, string? Text = null, string? Caption = null,
        List<PhotoSize>? Photo = null, Document? Document = null, Voice? Voice = null);
    public sealed record CallbackQuery(string Id, User From, Message? Message = null, string? Data = null);
    public sealed record Update(int UpdateId, Message? Message = null, CallbackQuery? CallbackQuery = null);
    public sealed record Button(string Text, string Data);

    // calls

    private sealed record Reply<T>(bool Ok, T? Result, string? Description, int? ErrorCode);

    /// <summary>The token has a colon, so the address is put together as text, never as a relative URI.</summary>
    private Uri At(string path) => new(Base.AbsoluteUri.TrimEnd('/') + "/" + path);
    private Uri Url(string method) => At("bot" + Token + "/" + method);

    /// <summary>The token is in the address: it never shows in a message.</summary>
    private string Hide(string text) => text.Replace(Token, "…");

    public async Task<T> Call<T>(string method, JsonObject? args = null, TimeSpan? wait = null, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait ?? TimeSpan.FromSeconds(30));
        HttpResponseMessage response;
        string body;
        try
        {
            async Task<HttpResponseMessage> Post() =>
                await http.PostAsync(Url(method), new StringContent((args ?? []).ToJsonString(), Encoding.UTF8, "application/json"), timeout.Token).ConfigureAwait(false);
            // a connection from the pool that was closed meanwhile fails at once: one more try on a fresh one
            try { response = await Post(); }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested) { response = await Post(); }
            body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            throw TelegramException.Network(Hide(e is OperationCanceledException ? L("geen antwoord binnen de tijd") : e.Message));
        }
        Reply<T>? reply = null;
        try { reply = JsonSerializer.Deserialize<Reply<T>>(body, Json); } catch (JsonException) { }
        int status = (int)response.StatusCode;
        if (status == 401 || reply?.ErrorCode == 401) throw TelegramException.Unauthorized();
        if (status == 409 || reply?.ErrorCode == 409) throw TelegramException.Conflict();
        if (reply is not { Ok: true, Result: { } result })
            throw TelegramException.Api(reply?.ErrorCode ?? status, reply?.Description ?? Hide(body.Length > 200 ? body[..200] : body));
        return result;
    }

    public Task<User> Me(CancellationToken ct = default) => Call<User>("getMe", ct: ct);

    /// <summary>Waits up to timeout seconds for something new (long polling).</summary>
    public Task<List<Update>> Updates(int offset, int timeout = 50, CancellationToken ct = default) =>
        Call<List<Update>>("getUpdates", new JsonObject
        {
            ["offset"] = offset,
            ["timeout"] = timeout,
            ["allowed_updates"] = new JsonArray("message", "callback_query"),
        }, TimeSpan.FromSeconds(timeout + 20), ct);

    private sealed record Sent(int MessageId);

    /// <summary>Sends one message (at most 4096 characters, see TelegramFormat.Chunks) as HTML, or as plain text when Telegram
    /// does not take the HTML. Returns its id.</summary>
    public async Task<int> Send(long chat, string text, string? html = null, IReadOnlyList<IReadOnlyList<Button>>? buttons = null, CancellationToken ct = default)
    {
        var p = new JsonObject { ["chat_id"] = chat, ["text"] = html ?? text, ["link_preview_options"] = new JsonObject { ["is_disabled"] = true } };
        if (html != null) p["parse_mode"] = "HTML";
        if (buttons is { Count: > 0 }) p["reply_markup"] = Keyboard(buttons);
        try
        {
            return (await Call<Sent>("sendMessage", p, ct: ct).ConfigureAwait(false)).MessageId;
        }
        catch (TelegramException e) when (e.Kind == TelegramException.Kinds.Api && e.Code == 400 && html != null)
        {
            p["text"] = text;
            p.Remove("parse_mode");
            return (await Call<Sent>("sendMessage", p, ct: ct).ConfigureAwait(false)).MessageId;
        }
    }

    /// <summary>Replaces a message's text (an answer that grows, a card that was answered). "Not modified" is no error.</summary>
    public async Task Edit(long chat, int message, string text, string? html = null, IReadOnlyList<IReadOnlyList<Button>>? buttons = null, CancellationToken ct = default)
    {
        var p = new JsonObject
        {
            ["chat_id"] = chat, ["message_id"] = message, ["text"] = html ?? text,
            ["link_preview_options"] = new JsonObject { ["is_disabled"] = true },
            ["reply_markup"] = Keyboard(buttons ?? []),
        };
        if (html != null) p["parse_mode"] = "HTML";
        try
        {
            await Call<JsonElement>("editMessageText", p, ct: ct).ConfigureAwait(false);
        }
        catch (TelegramException e) when (e.Kind == TelegramException.Kinds.Api && e.Code == 400 && e.Message.Contains("not modified")) { }
        catch (TelegramException e) when (e.Kind == TelegramException.Kinds.Api && e.Code == 400 && html != null)
        {
            p["text"] = text;
            p.Remove("parse_mode");
            await Call<JsonElement>("editMessageText", p, ct: ct).ConfigureAwait(false);
        }
    }

    public async Task Typing(long chat)
    {
        try { await Call<JsonElement>("sendChatAction", new JsonObject { ["chat_id"] = chat, ["action"] = "typing" }).ConfigureAwait(false); }
        catch (TelegramException) { }
    }

    /// <summary>The little note after a button press (and it stops the button's spinner).</summary>
    public async Task AnswerCallback(string id, string? text = null)
    {
        var p = new JsonObject { ["callback_query_id"] = id };
        if (text != null) p["text"] = text;
        try { await Call<JsonElement>("answerCallbackQuery", p).ConfigureAwait(false); }
        catch (TelegramException) { }
    }

    private sealed record FileInfo(string? FilePath);

    /// <summary>A file someone sent (a photo, a document, a voice message), at most 20 MB (the Bot API's own limit).</summary>
    public async Task<byte[]> Download(string fileId, CancellationToken ct = default)
    {
        var file = await Call<FileInfo>("getFile", new JsonObject { ["file_id"] = fileId }, ct: ct).ConfigureAwait(false);
        if (file.FilePath is not { } path) throw TelegramException.Api(404, "no file_path");
        try
        {
            using var response = await http.GetAsync(At("file/bot" + Token + "/" + path), ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw TelegramException.Api(404, "file");
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e) { throw TelegramException.Network(Hide(e.Message)); }
    }

    public static JsonObject Keyboard(IReadOnlyList<IReadOnlyList<Button>> rows) => new()
    {
        ["inline_keyboard"] = new JsonArray([.. rows.Select(r => (JsonNode)new JsonArray([.. r.Select(b => (JsonNode)new JsonObject { ["text"] = b.Text, ["callback_data"] = b.Data })]))]),
    };
}
