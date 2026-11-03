using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using Xunit;
using static WinAIMagic.Chat.Tests.FakeEngine;

namespace WinAIMagic.Chat.Tests;

/// <summary>The model must not fall silent while its work is not done (the sentences come from a real conversation).</summary>
public class UnfinishedTurnTests
{
    private static bool Check(string text, string reasoning = "", bool cutOff = false, bool working = true) =>
        ChatClient.LooksUnfinished(text, reasoning, cutOff, working);

    [Fact]
    public void StopsThatAreNotTheEnd()
    {
        Assert.True(Check("Sorry, ik ben gewoon doorgegaan! Ik maak nu een werkend .d64 disk image met het spel en start de emulator."));
        Assert.True(Check("Mijn script is mislukt door een error. Laat me het debuggen en een alternatieve aanpak proberen:"));
        Assert.True(Check("", reasoning: "Python3 werkt niet.\nLaten we kijken of c1541 beschikbaar is.\n</parameter>\n</function>\n</tool_call>"));
        Assert.True(Check("Ik kijk even.", reasoning: "c1541 zoeken\n</parameter>\n</function>\n</tool_call>"));
        Assert.True(Check("Let me try another way."));
        Assert.True(Check("half antwoord", cutOff: true, working: false));
    }

    /// <summary>After steps only the model's own word ends a task; the remark below ended a real conversation.</summary>
    [Fact]
    public void ARemarkIsNoEnding()
    {
        Assert.True(ChatClient.LooksUnfinished("dtruss levert geen uitvoer, exit code 0. Dit werkt niet lekker.", "", false, true, true));
        Assert.False(ChatClient.LooksUnfinished("KLAAR: het spel draait in VICE.", "", false, true, true));
        Assert.False(ChatClient.LooksUnfinished("**Klaar!** Het staat in uit.txt.", "", false, true, true));
        Assert.False(ChatClient.LooksUnfinished("DONE. The file is written.", "", false, true, true));
        Assert.False(ChatClient.LooksUnfinished("Welke map bedoel je?", "", false, true, true));
        Assert.False(ChatClient.LooksUnfinished("2 + 2 is 4.", "", false, true, false));
        Assert.False(ChatClient.LooksUnfinished("Fertig: alles läuft.", "", false, true, true));
        Assert.False(ChatClient.LooksUnfinished("Terminé. Tout marche.", "", false, true, true));
        Assert.True(ChatClient.LooksUnfinished("KLAAR is het nog niet:", "", false, true, true), "a colon comes before the word");
    }

    [Fact]
    public void RealEndings()
    {
        Assert.False(Check("Het spel staat in avontuur.prg en draait in VICE."));
        Assert.False(Check("Klaar! Wil je dat ik nog een kamer toevoeg?"));
        Assert.False(Check("Dat is alles. Laat me weten als je nog iets wilt veranderen."));
        Assert.False(Check("Let me know if you want more rooms."));
        Assert.False(Check("Ik zal het doen.", working: false));
    }

    /// <summary>The order of the rules: each one wins over the rules below it.</summary>
    [Fact]
    public void TheOrderOfTheRules()
    {
        Assert.True(Check("  \n ", working: false), "no text beats not working");
        Assert.False(Check("<tool_call>", working: false), "not working beats tool-call remains");
        Assert.True(Check("Klopt dat? <function=x>"), "remains beat a question");
        Assert.False(Check("Laat me weten: klopt dat?"), "a question beats a colon");
        Assert.True(Check("Dit ga ik doen:"), "a colon beats the announcement lists");
        Assert.True(ChatClient.LooksUnfinished("Klaar:", "", false, true, true), "a colon also beats the first word");
        Assert.True(Check("Klaar.", reasoning: new string('x', 400) + "</tool_call>" + new string('y', 100)), "the last 300 characters of thinking");
        Assert.False(Check("Klaar.", reasoning: "</tool_call>" + new string('y', 300)), "older thinking does not count");
        Assert.True(Check("Je vais continuer."));
        Assert.True(Check("Ich werde jetzt suchen."));
    }
}

public class ChatRequestTests : ChatTest
{
    private readonly ConversationStore store;
    private Guid Id => store.Conversations[0].Id;

    public ChatRequestTests() { store = new ConversationStore(); }

    private async Task<FakeEngine> Ask(string text, FakeEngine engine, ChatClient? client = null)
    {
        var chat = client ?? new ChatClient(engine.Client);
        chat.Send(text, [], Id, store, Base);
        await chat.Completion;
        return engine;
    }

    [Fact]
    public async Task TheRequestExactly()
    {
        Settings.Shared.ApiKey = "geheim";
        var e = await Ask("Hallo", new FakeEngine(() => Say("Hoi")));
        var body = e.Requests[0];
        Assert.Equal("http://127.0.0.1:8080/v1/chat/completions", e.Urls[0].ToString());
        Assert.Equal("Bearer geheim", e.Authorization[0]);
        Assert.Equal("DeepSeek-V4.1-Flash", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
        Assert.Equal(1.0, body["temperature"]!.GetValue<double>());
        Assert.Equal(1.0, body["top_p"]!.GetValue<double>());
        Assert.True(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal("high", body["chat_template_kwargs"]!["reasoning_effort"]!.GetValue<string>());
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.Equal(16_000, body["thinking_budget_tokens"]!.GetValue<int>());
        Assert.Equal("\n\nGenoeg nagedacht; nu schrijf ik het antwoord.\n", body["reasoning_budget_message"]!.GetValue<string>());
        Assert.False(body.ContainsKey("tools"), "no web, no folder");
        Assert.Equal(TimeSpan.FromSeconds(3600), ChatClient.RequestTimeout);
    }

    [Fact]
    public async Task ThinkingOffIsBudgetZero()
    {
        Settings.Shared.Thinking = false;
        var body = (await Ask("Hallo", new FakeEngine(() => Say("Hoi")))).Requests[0];
        Assert.Equal(0, body["thinking_budget_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("reasoning_budget_message"));
        Assert.False(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UnlimitedThinkingIsSaidOutLoud()
    {
        Settings.Shared.ThinkingBudget = 0;
        var body = (await Ask("Hallo", new FakeEngine(() => Say("Hoi")))).Requests[0];
        Assert.Equal(-1, body["thinking_budget_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("reasoning_budget_message"));
    }

    [Theory]
    [InlineData(4000)]
    [InlineData(32000)]
    public async Task ABudgetTravelsWithItsMessage(int budget)
    {
        Settings.Shared.ThinkingBudget = budget;
        var body = (await Ask("Hallo", new FakeEngine(() => Say("Hoi")))).Requests[0];
        Assert.Equal(budget, body["thinking_budget_tokens"]!.GetValue<int>());
        Assert.True(body.ContainsKey("reasoning_budget_message"));
    }

    [Fact]
    public async Task ToolSelectionFollowsTheSettings()
    {
        Assert.Empty(ChatClient.CurrentTools());
        Settings.Shared.WebSearch = true;
        Assert.Equal(new[] { "web_search", "fetch_url" }, ToolNames());
        WorkspaceTools.Current = NewFolder("work");
        Assert.Equal(new[] { "web_search", "fetch_url", "list_dir", "read_file", "write_file", "edit_file", "run_command" }, ToolNames());
        Settings.Shared.WorkspaceTools = false;
        Assert.Equal(new[] { "web_search", "fetch_url" }, ToolNames());

        static string[] ToolNames() => [.. ChatClient.CurrentTools().Select(t => t!["function"]!["name"]!.GetValue<string>())];
    }

    [Fact]
    public async Task HistoryForTheModel()
    {
        var jpeg = new byte[] { 1, 2, 3 };
        store.Update(Id, c =>
        {
            c.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = "Wat zie je?", Attachments = [new Attachment { Filename = "a.jpg", Jpeg = jpeg }, new Attachment { Filename = "b.jpg", Jpeg = jpeg }] });
            c.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = "Een kat.", Reasoning = "GEHEIME GEDACHTE" });
            c.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = "Lees" });
            c.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = "", Reasoning = "alleen gedacht" });
            c.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = "", ToolCalls = [new ToolCall("c1", "list_dir", "{}")] });
            c.Messages.Add(new ChatMessage { Role = ChatRole.Tool, Text = "uitvoer", ToolCallId = "c1" });
            c.Messages.Add(new ChatMessage { Role = ChatRole.System, Text = "NIET MEESTUREN" });
        });
        var messages = (await Ask("Verder", new FakeEngine(() => Say("Goed")))).Requests[0]["messages"]!.AsArray();
        var json = messages.ToJsonString();
        Assert.DoesNotContain("GEHEIME", json);
        Assert.DoesNotContain("alleen gedacht", json);
        Assert.DoesNotContain("reasoning", json);
        Assert.DoesNotContain("NIET MEESTUREN", json);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        // images as JPEG data content before the text
        var parts = messages[1]!["content"]!.AsArray();
        Assert.Equal(3, parts.Count);
        Assert.Equal("image_url", parts[0]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64,AQID", parts[0]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("image_url", parts[1]!["type"]!.GetValue<string>());
        Assert.Equal("Wat zie je?", parts[2]!["text"]!.GetValue<string>());
        Assert.Equal("Een kat.", messages[2]!["content"]!.GetValue<string>());
        // the empty turn is gone, the tool call and its result are there
        Assert.Equal("c1", messages[4]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("function", messages[4]!["tool_calls"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("list_dir", messages[4]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("tool", messages[5]!["role"]!.GetValue<string>());
        Assert.Equal("c1", messages[5]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("Verder", messages[6]!["content"]!.GetValue<string>());
    }

    /// <summary>Keep-alives during a long prompt, and whatever else a server sends between the data.</summary>
    [Fact]
    public async Task TheStreamSurvivesWhatIsNotData()
    {
        var e = await Ask("Hallo", new FakeEngine(() => Sse(
            ": keep-alive",
            "",
            ":",
            "event: ping",
            "data: {\"prompt_progress\":{\"total\":100,\"processed\":50}}",
            "data: {\"choices\":[]}",
            "data: {niet: json",
            "data: 12",
            "data:{\"choices\":[{\"delta\":{\"reasoning_content\":\"denk\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Ho\"},\"finish_reason\":null}]}",
            "\r",
            ": keep-alive\r",
            "data: {\"choices\":[{\"delta\":{\"content\":\"i\"}}]}\r",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4,\"total_tokens\":7},\"timings\":{\"predicted_per_second\":12,\"prompt_per_second\":99.5}}",
            "data: [DONE]",
            "data: {\"choices\":[{\"delta\":{\"content\":\"NA HET EINDE\"}}]}")));
        var reply = store.Conversations[0].Messages[^1];
        Assert.Equal("Hoi", reply.Text);
        Assert.Equal("denk", reply.Reasoning);
        Assert.Null(reply.Error);
        Assert.Equal(12.0, reply.TokensPerSecond);
        Assert.Equal(99.5, reply.PromptTokensPerSecond);
        Assert.Equal(7, store.Conversations[0].ContextTokens);
    }

    [Fact]
    public void WhatIsDataInAStream()
    {
        Assert.Equal("{}", ChatClient.DataOf("data: {}"));
        Assert.Equal("{}", ChatClient.DataOf("data:{}"));
        Assert.Equal("[DONE]", ChatClient.DataOf("data: [DONE]"));
        Assert.Null(ChatClient.DataOf(": keep-alive"));
        Assert.Null(ChatClient.DataOf(""));
        Assert.Null(ChatClient.DataOf("event: x"));
        Assert.Null(ChatClient.DataOf("data: "));
    }

    [Fact]
    public async Task ToolCallsArriveInPieces()
    {
        Settings.Shared.RightRead = "allow";
        WorkspaceTools.Current = NewFolder("work");
        store.Conversations[0].Workspace = WorkspaceTools.Current;
        var engine = new FakeEngine(
            () => Sse(
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"b\",\"function\":{\"name\":\"read_\",\"arguments\":\"\"}}]}}]}",
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"list_dir\",\"arguments\":\"{\\\"pa\"}}]}}]}",
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"function\":{\"name\":\"file\",\"arguments\":\"{\\\"path\\\":\\\"x\\\"}\"}}]}}]}",
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"th\\\":\\\".\\\"}\"}}]}}]}",
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}",
                "data: [DONE]"),
            () => Say("KLAAR"));
        await Ask("Kijk", engine);
        var calls = store.Conversations[0].Messages.First(m => m.ToolCalls.Count > 0).ToolCalls;
        Assert.Equal("list_dir", calls[0].Name);
        Assert.Equal("{\"path\":\".\"}", calls[0].Arguments);
        Assert.StartsWith("call_0_", calls[0].Id);
        Assert.Equal("b", calls[1].Id);
        Assert.Equal("read_file", calls[1].Name);
        Assert.Equal(2, store.Conversations[0].Messages.Count(m => m.Role == ChatRole.Tool));
    }

    [Fact]
    public async Task TitleFromTheFirstQuestion()
    {
        await Ask(new string('x', 60) + "\ny", new FakeEngine(() => Say("Hoi")));
        Assert.Equal(new string('x', 48), store.Conversations[0].Title);
        var other = new Conversation();
        store.Conversations.Add(other);
        var chat = new ChatClient(new FakeEngine(() => Say("Hoi")).Client);
        chat.Send("", [new Attachment { Filename = "foto.jpg" }], other.Id, store, Base);
        await chat.Completion;
        Assert.Equal("foto.jpg", other.Title);
    }

    [Fact]
    public async Task AnEngineErrorIsShownWithItsText()
    {
        var e = await Ask("Hallo", new FakeEngine(() => Error(500, "kapot\n" + new string('x', 600))));
        var reply = store.Conversations[0].Messages[^1];
        Assert.StartsWith("Engine gaf 500: kapot", reply.Error);
        Assert.Equal("Engine gaf 500: ".Length + 400, reply.Error!.Length);
        Assert.Single(e.Requests);
    }

    [Fact]
    public async Task AConnectionFailureIsShown()
    {
        var engine = new FakeEngine();
        engine.Then(_ => throw new HttpRequestException("geen verbinding"));
        await Ask("Hallo", engine);
        Assert.Equal("geen verbinding", store.Conversations[0].Messages[^1].Error);
    }

    /// <summary>The same words on both engines: llama-server, and the tiered server's OpenAI wording.</summary>
    [Theory]
    [InlineData("{\"error\":{\"code\":400,\"message\":\"the request exceeds the available context size, try increasing it\",\"type\":\"exceed_context_size_error\"}}")]
    [InlineData("{\"error\":{\"message\":\"This model's maximum context length is 65536 tokens (66000 tokens) + max tokens (1) exceeds the context\",\"type\":\"invalid_request_error\",\"code\":\"context_length_exceeded\"}}")]
    public async Task ATooLongConversationIsSummarizedAndTriedAgain(string answer)
    {
        Fill(5);
        var engine = new FakeEngine(() => Error(400, answer), () => Say("Hoi"));
        engine.Complete = _ => "Samenvatting van het begin";
        await Ask("Nog een", engine);
        var c = store.Conversations[0];
        Assert.Equal("Samenvatting van het begin", c.Summary);
        Assert.Null(c.Messages[^1].Error);
        Assert.Equal("Hoi", c.Messages[^1].Text);
        Assert.Equal(2, engine.Requests.Count);
        Assert.Contains("Samenvatting van het begin", engine.Requests[1]["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task OnlyOneRetry()
    {
        Fill(5);
        var engine = new FakeEngine(() => Error(400, "context too long"), () => Error(400, "context too long"));
        engine.Complete = _ => "S";
        await Ask("Nog een", engine);
        Assert.StartsWith("Engine gaf 400: context", store.Conversations[0].Messages[^1].Error);
        Assert.Equal(2, engine.Requests.Count);
    }

    [Fact]
    public async Task AnotherBadRequestIsNoContextProblem()
    {
        Fill(5);
        var engine = new FakeEngine(() => Error(400, "bad temperature"));
        await Ask("Nog een", engine);
        Assert.StartsWith("Engine gaf 400", store.Conversations[0].Messages[^1].Error);
        Assert.Empty(engine.CompleteRequests);
    }

    private void Fill(int questions)
    {
        store.Update(Id, c =>
        {
            for (int i = 0; i < questions; i++)
            {
                c.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = $"Vraag {i}" });
                c.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = $"Antwoord {i}" });
            }
        });
    }

    [Fact]
    public async Task SummarizesAtEightyPercent()
    {
        Fill(4);
        store.Conversations[0].ContextTokens = 801;
        var engine = new FakeEngine(() => Say("Hoi", total: 100));
        engine.Complete = _ => "S";
        var chat = new ChatClient(engine.Client) { ContextLimit = 1000 };
        await Ask("Nog een", engine, chat);
        Assert.Equal("S", store.Conversations[0].Summary);
        Assert.Equal(100, store.Conversations[0].ContextTokens);
    }

    [Fact]
    public async Task NotBelowEightyPercent()
    {
        Fill(4);
        store.Conversations[0].ContextTokens = 800;
        var engine = new FakeEngine(() => Say("Hoi"));
        var chat = new ChatClient(engine.Client) { ContextLimit = 1000 };
        await Ask("Nog een", engine, chat);
        Assert.Empty(engine.CompleteRequests);
    }

    [Fact]
    public async Task TheSummaryRequestExactly()
    {
        Fill(4);
        store.Update(Id, c =>
        {
            c.Messages[0].Attachments = [new Attachment { Filename = "x", Jpeg = [1] }];
            c.Messages.Insert(1, new ChatMessage { Role = ChatRole.User, Text = "AUTOMATISCH", Automatic = true });
            c.Messages.Insert(2, new ChatMessage { Role = ChatRole.Assistant, Text = "", ToolCalls = [new ToolCall("c", "read_file", new string('a', 700))] });
            c.Messages.Insert(3, new ChatMessage { Role = ChatRole.Tool, Text = new string('r', 2000), ToolCallId = "c" });
        });
        var engine = new FakeEngine();
        engine.Complete = _ => "S";
        var chat = new ChatClient(engine.Client);
        await chat.CompactAsync(Id, store, Base);
        var body = engine.CompleteRequests[0];
        Assert.False(body["stream"]!.GetValue<bool>());
        Assert.Equal(0.3, body["temperature"]!.GetValue<double>());
        Assert.Equal(2000, body["max_tokens"]!.GetValue<int>());
        Assert.False(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(0, body["thinking_budget_tokens"]!.GetValue<int>());
        Assert.Contains("Je vat een gesprek samen", body["messages"]![0]!["content"]!.GetValue<string>());
        var transcript = body["messages"]![1]!["content"]!.GetValue<string>();
        Assert.StartsWith("Vat dit gesprek samen:\n\nGebruiker: Vraag 0 [afbeelding]", transcript);
        Assert.DoesNotContain("AUTOMATISCH", transcript);
        Assert.Contains("Assistent gebruikt read_file: " + new string('a', 600) + "\n", transcript);
        Assert.Contains("Resultaat: " + new string('r', 1500) + "\n", transcript);
        Assert.Contains("Assistent: Antwoord 0", transcript);
        // the last two questions stay
        var after = store.Conversations[0];
        Assert.Equal(2, after.Unsummarized.Count(m => m.Role == ChatRole.User));
        Assert.Equal("Vraag 2", after.Unsummarized[0].Text);
    }

    [Fact]
    public async Task ALongTranscriptLosesItsStart()
    {
        Fill(4);
        store.Update(Id, c => c.Messages[0].Text = new string('q', 5000));
        var engine = new FakeEngine();
        engine.Complete = _ => "S";
        await new ChatClient(engine.Client) { ContextLimit = 1000 }.CompactAsync(Id, store, Base);
        var transcript = engine.CompleteRequests[0]["messages"]![1]!["content"]!.GetValue<string>();
        Assert.StartsWith("Vat dit gesprek samen:\n\n[begin weggelaten]\n", transcript);
        Assert.True(transcript.Length < 2600);
    }

    /// <summary>One long task with one question: the latest steps stay, a tool result stays with its call.</summary>
    [Fact]
    public async Task TheCutRuleInOneLongTask()
    {
        store.Update(Id, c =>
        {
            c.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = "Grote taak" });
            for (int i = 0; i < 5; i++)
            {
                c.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [new ToolCall($"c{i}", "list_dir", "{}")] });
                c.Messages.Add(new ChatMessage { Role = ChatRole.Tool, Text = "r", ToolCallId = $"c{i}" });
            }
        });
        var engine = new FakeEngine();
        engine.Complete = _ => "S";
        await new ChatClient(engine.Client).CompactAsync(Id, store, Base);
        var open = store.Conversations[0].Unsummarized;
        Assert.Equal(ChatRole.Assistant, open[0].Role);
        Assert.Equal(6, open.Count);
        // every template wants a question first
        var next = new ChatClient().RequestMessages(open, "S");
        Assert.Equal("user", next[1]!["role"]!.GetValue<string>());
        Assert.Equal("Ga verder met de taak uit de samenvatting.", next[1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task AShortConversationIsNotSummarized()
    {
        Fill(2);
        var engine = new FakeEngine();
        await new ChatClient(engine.Client).CompactAsync(Id, store, Base);
        Assert.Empty(engine.CompleteRequests);
        Assert.Null(store.Conversations[0].Summary);
    }
}

public class SystemPromptTests : ChatTest
{
    private string Prompt(string? summary = null) => new ChatClient().RequestMessages([], summary)[0]!["content"]!.GetValue<string>();

    [Fact]
    public void TheOrderOfThePrompt()
    {
        var work = NewFolder("work");
        WorkspaceTools.Current = work;
        File.WriteAllText(Path.Combine(work, "WINAIMAGIC.md"), "MAPNOTITIE");
        File.WriteAllText(Paths.GeneralNotes, "ALGEMEEN");
        Settings.Shared.WebSearch = true;
        Settings.Shared.SystemPrompt = "EIGEN PROMPT";
        var p = Prompt("SAMENVATTING");
        string[] order =
        [
            "Je bent DeepSeek-V4.1-Flash, lokaal draaiend op de pc van de gebruiker via WinAIMagic. Vandaag is het ",
            "Je kunt het web doorzoeken",
            "Algemene notities van de gebruiker (WINAIMAGIC.md), volg ze op:\nALGEMEEN",
            "Je werkt in de map " + WorkspaceTools.Workspace,
            "Buiten deze map werken list_dir",
            "Notities van de gebruiker voor deze werkmap (WINAIMAGIC.md), volg ze op:\nMAPNOTITIE",
            "Werk door tot de taak echt af is",
            "Is de taak af, begin je laatste bericht dan met het woord KLAAR.",
            "EIGEN PROMPT",
            "Samenvatting van het begin van dit gesprek",
            "SAMENVATTING",
        ];
        int at = -1;
        foreach (var part in order)
        {
            int next = p.IndexOf(part, at + 1, StringComparison.Ordinal);
            Assert.True(next > at, "out of order: " + part + "\n" + p);
            at = next;
        }
    }

    [Theory]
    [InlineData("nl", "KLAAR", "Vandaag is het")]
    [InlineData("en", "DONE", "Today is")]
    [InlineData("de", "FERTIG", "Heute ist")]
    [InlineData("fr", "TERMINÉ", "Nous sommes le")]
    public void WindowsInEveryLanguage(string language, string word, string today)
    {
        Settings.Shared.Language = language;
        WorkspaceTools.Current = NewFolder("work");
        Settings.Shared.WebSearch = true;
        DeveloperTools.MissingOverride = ["python", "git"];
        var p = Prompt();
        foreach (var gone in new[] { "Mac", "MacAIMagic", "MACAIMAGIC", "zsh", "sudo", "dtruss", "mdfind", "osascript", "/Applications", "Finder", "Apple", "met open", "with open", "mit open", "avec open", "& aan", "& à", "& at", "& am" })
            Assert.DoesNotContain(gone, p);
        foreach (var part in new[] { "WinAIMagic", "PowerShell", "Start-Process", "Get-ChildItem -Recurse", "Get-Content", "where.exe", "Program Files", "UAC", "python, git", word, today })
            Assert.Contains(part, p);
    }

    [Theory]
    [InlineData("nl", "ontbreekt python")]
    [InlineData("en", "python is not on this PC")]
    [InlineData("de", "fehlt python")]
    [InlineData("fr", "python n'est pas présent")]
    public void SaysWhenPythonOrGitIsMissing(string language, string text)
    {
        Settings.Shared.Language = language;
        WorkspaceTools.Current = NewFolder("work");
        Assert.DoesNotContain("python", Prompt());
        DeveloperTools.MissingOverride = ["python"];
        Assert.Contains(text, Prompt());
    }

    /// <summary>Asked of the real machine; without a throw, whatever is installed here.</summary>
    [Fact]
    public void DeveloperToolsAreFoundWithWhere()
    {
        DeveloperTools.MissingOverride = null;
        Assert.True(DeveloperTools.Missing.All(n => n is "python" or "git"));
        Assert.Equal(DeveloperTools.Missing.Count == 0, DeveloperTools.HasDeveloperTools);
    }

    [Fact]
    public void WithoutAFolderNoFolderRules()
    {
        var p = Prompt();
        Assert.DoesNotContain("run_command", p);
        Assert.DoesNotContain("KLAAR", p);
    }

    [Fact]
    public void GeneralNotesAreCutAt8000()
    {
        File.WriteAllText(Paths.GeneralNotes, new string('n', 9000));
        Assert.DoesNotContain(new string('n', 8001), Prompt());
        Assert.Contains(new string('n', 8000), Prompt());
    }
}

/// <summary>The loop: going on, stopping, rights, learning. A scripted model plays the engine; no real model.</summary>
public class AgentLoopTests : ChatTest
{
    private readonly ConversationStore store;
    private readonly string work;
    private Conversation C => store.Conversations[0];

    public AgentLoopTests()
    {
        store = new ConversationStore();
        work = NewFolder("work");
        C.Workspace = work;
        Settings.Shared.RightRead = "allow";
        Settings.Shared.RightWrite = "allow";
        Settings.Shared.RightRun = "allow";
    }

    private async Task<ChatClient> Ask(string text, FakeEngine engine)
    {
        var chat = new ChatClient(engine.Client);
        chat.Send(text, [], C.Id, store, Base);
        await chat.Completion;
        return chat;
    }

    private static Func<HttpResponseMessage> ListDir(string id = "c") => () => Call("list_dir", new JsonObject { ["path"] = "." }, id);
    private static Func<HttpResponseMessage> Write(string name, string id = "w") => () => Call("write_file", new JsonObject { ["path"] = name, ["content"] = "x" }, id);
    private static string Content(JsonObject body) => body["messages"]![0]!["content"]!.GetValue<string>();

    [Fact]
    public async Task ModelCreatesAndRunsFiles()
    {
        byte[] prg = [0x01, 0x08, 0x99, 0x89, 0x00, 0x00];
        var engine = new FakeEngine(
            () => Call("write_file", new JsonObject { ["path"] = "hallo.prg", ["content"] = Convert.ToBase64String(prg), ["encoding"] = "base64" }, "a"),
            () => Call("run_command", new JsonObject { ["command"] = "(Get-Content -AsByteStream -Path hallo.prg -TotalCount 2) -join ' '" }, "b"),
            () => Say("KLAAR: het staat er."));
        await Ask("Maak hallo.prg", engine);
        Assert.Equal(prg, File.ReadAllBytes(Path.Combine(work, "hallo.prg")));
        Assert.Contains(C.Messages, m => m.ToolCalls.Exists(t => t.Name == "run_command"));
        Assert.Contains("exit code", C.Messages.Last(m => m.Role == ChatRole.Tool).Text);
        Assert.Null(C.Messages[^1].Error);
        // the last visible message carries the steps
        Assert.Equal(2, C.Messages[^1].Activities.Count);
        Assert.True(C.Messages[^1].Activities.TrueForAll(a => a.Done));
        // the tool turns fold away
        Assert.True(C.Messages.Where(m => m.Role == ChatRole.Assistant && m.ToolCalls.Count > 0).All(m => m.IsHidden));
    }

    [Fact]
    public async Task WebSearchLoop()
    {
        Settings.Shared.BraveKey = "k";
        Settings.Shared.WebSearch = true;
        C.Workspace = null;
        var web = new FakeWeb { Answer = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"web\":{\"results\":[{\"title\":\"llama.cpp\",\"url\":\"https://example.org/l\",\"description\":\"<b>nieuw</b>\"}]}}") } };
        WebTools.Http = new HttpClient(web);
        var engine = new FakeEngine(
            () => Call("web_search", new JsonObject { ["query"] = "llama.cpp versie" }),
            () => Say("KLAAR: de nieuwste versie staat op example.org."));
        await Ask("Zoek op", engine);
        Assert.Contains(C.Messages, m => m.Role == ChatRole.Tool);
        Assert.NotEmpty(C.Messages[^1].Text);
        Assert.Equal("https://example.org/l", C.Messages[^1].Sources.Single().Url);
        Assert.Contains("[1] llama.cpp\nhttps://example.org/l\nnieuw", C.Messages.First(m => m.Role == ChatRole.Tool).Text);
        Assert.Equal("Zoeken: llama.cpp versie", C.Messages[^1].Activities[0].Label);
        Assert.Equal(21.5, C.Messages[^1].TokensPerSecond);
    }

    /// <summary>Commands without output and one that fails must not end a task: quiet steps are followed by a nudge.</summary>
    [Fact]
    public async Task QuietStepsDoNotEndTheTask()
    {
        var engine = new FakeEngine(ListDir("1"), () => Say("Dit werkt niet lekker."), ListDir("2"), () => Say("KLAAR"));
        await Ask("Werk", engine);
        var nudge = C.Messages.Single(m => m.Automatic == true);
        Assert.StartsWith("Je stopte zonder gereedschap te gebruiken.", nudge.Text);
        Assert.Equal(ChatRole.User, nudge.Role);
        Assert.Null(C.Messages[^1].Error);
        Assert.Equal(4, engine.Requests.Count);
    }

    [Fact]
    public async Task AfterThreeNudgesItSaysSo()
    {
        var engine = new FakeEngine(ListDir(), () => Say("Hm."), () => Say("Hm."), () => Say("Hm."), () => Say("Hm."));
        await Ask("Werk", engine);
        Assert.Equal(3, C.Messages.Count(m => m.Automatic == true));
        Assert.Equal("Het model stopte 3 keer zonder verder te werken of te zeggen dat de taak af is. Zeg \"ga door\", of geef het een aanwijzing.", C.Messages[^1].Error);
        Assert.Equal(5, engine.Requests.Count);
    }

    [Fact]
    public async Task AToolCallResetsTheNudges()
    {
        var engine = new FakeEngine(ListDir("1"), () => Say("Hm."), () => Say("Hm."), () => Say("Hm."), ListDir("2"), () => Say("Hm."), () => Say("Hm."), () => Say("Hm."), () => Say("KLAAR"));
        await Ask("Werk", engine);
        Assert.Null(C.Messages[^1].Error);
        Assert.Equal(6, C.Messages.Count(m => m.Automatic == true));
    }

    /// <summary>An announced step with a tool call cut off by a full context: the broken call is not run.</summary>
    [Fact]
    public async Task LengthRejectsToolCalls()
    {
        var engine = new FakeEngine(
            () => Call("write_file", new JsonObject { ["path"] = "half.txt", ["content"] = "x" }, finish: "length"),
            () => Say("KLAAR"));
        await Ask("Werk", engine);
        Assert.False(File.Exists(Path.Combine(work, "half.txt")));
        Assert.DoesNotContain(C.Messages, m => m.ToolCalls.Count > 0);
        Assert.Equal("Je antwoord werd afgekapt omdat je context vol was. Ga verder waar je gebleven was.", C.Messages.Single(m => m.Automatic == true).Text);
    }

    [Fact]
    public async Task APlainChatJustEnds()
    {
        C.Workspace = null;
        var engine = new FakeEngine(() => Say("Ik ga even denken."));
        await Ask("Hoi", engine);
        Assert.Single(engine.Requests);
        Assert.Null(C.Messages[^1].Error);
    }

    [Fact]
    public async Task AnAnswerOfOnlyThinkingIsNudged()
    {
        C.Workspace = null;
        var engine = new FakeEngine(() => Say("", reasoning: "denk denk"), () => Say("Antwoord"));
        await Ask("Hoi", engine);
        Assert.Equal(2, engine.Requests.Count);
    }

    /// <summary>200 rounds in a folder, and the pause says so where the user sees it: not on a folded tool result.</summary>
    [Fact]
    public async Task TwoHundredRoundsWithAFolderAndThePauseIsVisible()
    {
        var engine = new FakeEngine { Always = _ => Call("list_dir", new JsonObject { ["path"] = "." }) };
        await Ask("Werk", engine);
        Assert.Equal(200, engine.Requests.Count);
        var last = C.Messages[^1];
        Assert.False(last.IsHidden);
        Assert.Equal(ChatRole.Assistant, last.Role);
        Assert.Equal("Het model heeft 200 stappen gezet en pauzeert. Zeg \"ga door\" om verder te gaan.", last.Error);
        Assert.Equal(200, last.Activities.Count);
    }

    [Fact]
    public async Task TwentyRoundsWithoutAFolder()
    {
        C.Workspace = null;
        var engine = new FakeEngine { Always = _ => Call("web_search_oud", new JsonObject()) };
        await Ask("Zoek", engine);
        Assert.Equal(20, engine.Requests.Count);
        Assert.False(C.Messages[^1].IsHidden);
        Assert.Equal("Het model heeft 20 stappen gezet en pauzeert. Zeg \"ga door\" om verder te gaan.", C.Messages[^1].Error);
        // the next request after a pause has a clean history
        Assert.DoesNotContain(new ChatClient().RequestMessages(C.Messages), m => m!["role"]!.GetValue<string>() == "assistant" && m["content"]!.GetValue<string>().Length == 0 && m["tool_calls"] == null);
    }

    [Fact]
    public async Task AToolResultDoesNotFillASmallContext()
    {
        File.WriteAllText(Path.Combine(work, "lang.txt"), new string('a', 30_000));
        var engine = new FakeEngine(() => Call("read_file", new JsonObject { ["path"] = "lang.txt" }), () => Say("KLAAR"));
        var chat = new ChatClient(engine.Client) { ContextLimit = 1000 };
        chat.Send("Lees", [], C.Id, store, Base);
        await chat.Completion;
        var result = C.Messages.Single(m => m.Role == ChatRole.Tool).Text;
        Assert.Contains("… (shortened, the output was 30002 characters) …", result);
        Assert.Equal(8000 + "\n… (shortened, the output was 30002 characters) …\n".Length, result.Length);
        Assert.Equal(4000, C.Messages[^1].Activities[0].Output!.Length);
    }

    /// <summary>A long task with one question goes on to the end, also in a small context.</summary>
    [Fact]
    public async Task LongTaskFinishesInSmallContext()
    {
        var engine = new FakeEngine { Complete = b => b.ContainsKey("tools") ? null : "De taak tot nu toe." };
        for (int i = 0; i < 8; i++)
        {
            var id = $"c{i}";
            engine.Then(_ => Call("list_dir", new JsonObject { ["path"] = "." }, id, total: 900));
        }
        engine.Then(_ => Say("KLAAR", total: 100));
        var chat = new ChatClient(engine.Client) { ContextLimit = 1000 };
        chat.Send("Lees alles", [], C.Id, store, Base);
        await chat.Completion;
        Assert.NotNull(C.Summary);
        Assert.Null(C.Messages[^1].Error);
        Assert.Equal("KLAAR", C.Messages[^1].Text);
        var later = engine.Requests[^1]["messages"]!.AsArray();
        Assert.Contains("De taak tot nu toe.", later[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", later[1]!["role"]!.GetValue<string>());
    }

    /// <summary>A new chat must have the folder chosen last, and then the model knows where it works.</summary>
    [Fact]
    public async Task ModelIsToldToSearchOutsideTheFolder()
    {
        var engine = new FakeEngine(() => Say("Niets gevonden in Program Files."), () => Say("KLAAR"));
        await Ask("Staat VICE op deze pc?", engine);
        var prompt = Content(engine.Requests[0]);
        Assert.Contains("Zeg dus niet dat je niet op deze pc kunt zoeken: doe het met run_command.", prompt);
        Assert.Contains("run_command", engine.Requests[0]["tools"]!.ToJsonString());
    }

    // MARK: - stop

    /// <summary>Stop stops the command, answers its call, and stops the spinner.</summary>
    [Fact]
    public async Task StopFillsInTheToolResult()
    {
        var two = new JsonObject
        {
            ["tool_calls"] = new JsonArray(
                new JsonObject { ["index"] = 0, ["id"] = "a", ["function"] = new JsonObject { ["name"] = "run_command", ["arguments"] = "{\"command\":\"Start-Sleep 60\"}" } },
                new JsonObject { ["index"] = 1, ["id"] = "b", ["function"] = new JsonObject { ["name"] = "list_dir", ["arguments"] = "{\"path\":\".\"}" } }),
        };
        var engine = new FakeEngine(() => Sse(Chunk(two), "data: [DONE]"));
        var chat = new ChatClient(engine.Client);
        chat.Send("Werk", [], C.Id, store, Base);
        await Until(() => C.Messages[^1].Activities.Count == 1);
        await Task.Delay(1500);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        chat.Stop();
        await chat.Completion;
        Assert.True(sw.Elapsed.TotalSeconds < 6, sw.Elapsed.ToString());
        Assert.False(chat.Busy);
        var results = C.Messages.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Equal(new[] { "a", "b" }, results.Select(m => m.ToolCallId));
        Assert.True(results.TrueForAll(m => m.Text == "gestopt door de gebruiker"));
        Assert.True(C.Messages.TrueForAll(m => m.Activities.TrueForAll(a => a.Done)), "the spinner stops");

        // the conversation goes on: every call is answered
        var next = new FakeEngine(() => Say("Goed."));
        var chat2 = new ChatClient(next.Client);
        chat2.Send("Toch niet", [], C.Id, store, Base);
        await chat2.Completion;
        var roles = next.Requests[0]["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "system", "user", "assistant", "tool", "tool", "user" }, roles);
    }

    [Fact]
    public async Task StopDuringTheAnswerKeepsWhatWasWritten()
    {
        var engine = new FakeEngine(() => Slow(Enumerable.Repeat("woord ", 1000), TimeSpan.FromMilliseconds(5)));
        var chat = new ChatClient(engine.Client);
        chat.Send("Schrijf", [], C.Id, store, Base);
        await Until(() => C.Messages[^1].Text.Length > 12);
        chat.Stop();
        await chat.Completion;
        Assert.StartsWith("woord ", C.Messages[^1].Text);
        Assert.Null(C.Messages[^1].Error);
        Assert.False(chat.Busy);
    }

    [Fact]
    public async Task AConversationThatEndedInACrashCanGoOn()
    {
        C.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = "a" });
        C.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [new ToolCall("x", "list_dir", "{}")], Activities = [new ToolActivity { Label = "Map" }] });
        var engine = new FakeEngine(() => Say("KLAAR"));
        await Ask("verder", engine);
        Assert.Equal("gestopt door de gebruiker", C.Messages.Single(m => m.Role == ChatRole.Tool).Text);
        Assert.True(C.Messages[1].Activities[0].Done);
    }

    // MARK: - rights

    private FakeEngine WriteThenDone() => new(Write("a.txt"), () => Say("KLAAR"));

    [Fact]
    public async Task DeniedInTheSettings()
    {
        Settings.Shared.RightWrite = "deny";
        var chat = new ChatClient(WriteThenDone().Client);
        chat.Send("Schrijf", [], C.Id, store, Base);
        await chat.Completion;
        Assert.False(File.Exists(Path.Combine(work, "a.txt")));
        Assert.Equal("The user's settings do not allow this kind of action (Schrijven). Do not try it again; tell the user what you wanted to do, or continue without it.", C.Messages.Single(m => m.Role == ChatRole.Tool).Text);
        Assert.True(C.Messages[^1].Activities[0].Denied);
    }

    [Fact]
    public async Task AskedAndAllowed()
    {
        Settings.Shared.RightWrite = "ask";
        var chat = new ChatClient(WriteThenDone().Client);
        chat.Send("Schrijf", [], C.Id, store, Base);
        await Until(() => chat.PendingApproval != null);
        var a = chat.PendingApproval!;
        Assert.Equal("Bestand schrijven: a.txt", a.Title);
        Assert.Equal("x", a.Detail);
        Assert.Equal(C.Id, a.Conversation);
        chat.Answer(a, true);
        await chat.Completion;
        Assert.True(File.Exists(Path.Combine(work, "a.txt")));
        Assert.Null(chat.PendingApproval);
    }

    [Fact]
    public async Task AskedAndRefused()
    {
        Settings.Shared.RightRun = "ask";
        var engine = new FakeEngine(() => Call("run_command", new JsonObject { ["command"] = "New-Item a.txt" }), () => Say("KLAAR"));
        var chat = new ChatClient(engine.Client);
        chat.Send("Doe", [], C.Id, store, Base);
        await Until(() => chat.PendingApproval != null);
        chat.Answer(chat.PendingApproval!, false);
        await chat.Completion;
        Assert.False(File.Exists(Path.Combine(work, "a.txt")));
        Assert.Equal("The user did not allow this. Ask what they want instead, or continue without it.", C.Messages.Single(m => m.Role == ChatRole.Tool).Text);
        Assert.True(C.Messages[^1].Activities[0].Denied);
    }

    /// <summary>"Altijd" goes for this chat, in this session: not for another chat, not for a new session.</summary>
    [Fact]
    public async Task AlwaysForThisChatAndSession()
    {
        Settings.Shared.RightWrite = "ask";
        var engine = new FakeEngine(Write("a.txt"), () => Say("KLAAR"), Write("b.txt"), () => Say("KLAAR"), Write("c.txt"), () => Say("KLAAR"), Write("d.txt"));
        var chat = new ChatClient(engine.Client);
        chat.Send("Schrijf", [], C.Id, store, Base);
        await Until(() => chat.PendingApproval != null);
        chat.Answer(chat.PendingApproval!, true, always: true);
        await chat.Completion;
        chat.Send("Nog een", [], C.Id, store, Base);
        await chat.Completion;
        Assert.True(File.Exists(Path.Combine(work, "b.txt")), "not asked again");

        var other = new Conversation { Workspace = work };
        store.Conversations.Add(other);
        chat.Send("Schrijf", [], other.Id, store, Base);
        await Until(() => chat.PendingApproval != null);
        Assert.Equal(other.Id, chat.PendingApproval!.Conversation);
        chat.Answer(chat.PendingApproval!, false);
        await chat.Completion;
        Assert.False(File.Exists(Path.Combine(work, "c.txt")));

        // another session (a new client)
        var chat2 = new ChatClient(engine.Client);
        chat2.Send("Schrijf", [], C.Id, store, Base);
        await Until(() => chat2.PendingApproval != null);
        chat2.Stop();
        await chat2.Completion;
    }

    /// <summary>Deny goes first, also when the user said "always" before.</summary>
    [Fact]
    public async Task DenyWinsOverAlways()
    {
        Settings.Shared.RightWrite = "ask";
        var engine = new FakeEngine(Write("a.txt"), () => Say("KLAAR"), Write("b.txt"), () => Say("KLAAR"));
        var chat = new ChatClient(engine.Client);
        chat.Send("Schrijf", [], C.Id, store, Base);
        await Until(() => chat.PendingApproval != null);
        chat.Answer(chat.PendingApproval!, true, always: true);
        await chat.Completion;
        Settings.Shared.RightWrite = "deny";
        chat.Send("Nog een", [], C.Id, store, Base);
        await chat.Completion;
        Assert.False(File.Exists(Path.Combine(work, "b.txt")));
        Assert.Contains("do not allow", C.Messages.Last(m => m.Role == ChatRole.Tool).Text);
    }

    [Fact]
    public async Task StopDeniesAnOpenQuestion()
    {
        Settings.Shared.RightWrite = "ask";
        var chat = new ChatClient(WriteThenDone().Client);
        chat.Send("Schrijf", [], C.Id, store, Base);
        await Until(() => chat.PendingApproval != null);
        chat.Stop();
        await chat.Completion;
        Assert.Null(chat.PendingApproval);
        Assert.False(File.Exists(Path.Combine(work, "a.txt")));
        Assert.Equal("gestopt door de gebruiker", C.Messages.Single(m => m.Role == ChatRole.Tool).Text);
    }

    [Fact]
    public async Task TheAskCallback()
    {
        Settings.Shared.RightWrite = "ask";
        var chat = new ChatClient(WriteThenDone().Client) { AskUser = (a, _) => Task.FromResult(new ApprovalAnswer(a.Title.Contains("a.txt"))) };
        chat.Send("Schrijf", [], C.Id, store, Base);
        await chat.Completion;
        Assert.True(File.Exists(Path.Combine(work, "a.txt")));
    }

    [Fact]
    public async Task StopCancelsTheAskCallback()
    {
        Settings.Shared.RightWrite = "ask";
        bool cancelled = false;
        var chat = new ChatClient(WriteThenDone().Client)
        {
            AskUser = async (_, ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled = true; throw; }
                return new ApprovalAnswer(true);
            },
        };
        chat.Send("Schrijf", [], C.Id, store, Base);
        await Until(() => chat.PendingApproval != null);
        chat.Stop();
        await chat.Completion;
        await Until(() => cancelled);
        Assert.False(File.Exists(Path.Combine(work, "a.txt")));
    }

    [Fact]
    public async Task AutoApproveAllowsWhatWouldBeAsked()
    {
        Settings.Shared.RightWrite = "ask";
        Settings.Shared.AutoApprove = true;
        await Ask("Schrijf", WriteThenDone());
        Assert.True(File.Exists(Path.Combine(work, "a.txt")));
    }

    // MARK: - learning

    private static FakeEngine Task3(Func<JsonObject, string?> complete) =>
        new(ListDir("1"), ListDir("2"), ListDir("3"), () => Say("KLAAR")) { Complete = complete };

    /// <summary>What a task teaches ends up in WINAIMAGIC.md, and the next conversation in that folder knows it.</summary>
    [Fact]
    public async Task LessonReachesNextConversation()
    {
        var engine = Task3(_ => "Dit leerde ik:\n- vicex128 bestaat hier niet\nGEEN andere");
        var chat = await Ask("Doe drie dingen", engine);
        var notes = File.ReadAllText(Path.Combine(work, "WINAIMAGIC.md"));
        Assert.Contains("- vicex128 bestaat hier niet", notes);
        Assert.Contains("<!-- winaimagic:lessons -->", notes);
        Assert.DoesNotContain("GEEN", notes);
        Assert.Equal("WINAIMAGIC.md bijgewerkt: 1 nieuw geleerd", C.Messages[^1].Activities[^1].Label);

        var request = engine.CompleteRequests[0];
        Assert.False(request["stream"]!.GetValue<bool>());
        Assert.Equal(0.2, request["temperature"]!.GetValue<double>());
        Assert.Equal(400, request["max_tokens"]!.GetValue<int>());
        Assert.Equal(0, request["thinking_budget_tokens"]!.GetValue<int>());
        Assert.Equal("none", request["tool_choice"]!.GetValue<string>());
        Assert.NotNull(request["tools"]);
        Assert.Contains("deze pc", request["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());

        var second = new Conversation { Workspace = work };
        store.Conversations.Add(second);
        var next = new FakeEngine(() => Say("Nee"));
        var chat2 = new ChatClient(next.Client);
        chat2.Send("Bestaat vicex128?", [], second.Id, store, Base);
        await chat2.Completion;
        Assert.Contains("vicex128", Content(next.Requests[0]));
    }

    /// <summary>The prompt stays the same in the conversation that learned it, so the engine can keep its cache; an edit counts at once.</summary>
    [Fact]
    public async Task ThePromptStaysFrozenUntilTheFileChanges()
    {
        var engine = Task3(_ => "- een les van minstens acht tekens");
        engine.Then(_ => Say("eerst"));
        engine.Then(_ => Say("daarna"));
        var chat = new ChatClient(engine.Client);
        chat.Send("Doe", [], C.Id, store, Base);
        await chat.Completion;
        Assert.Contains("een les", File.ReadAllText(Path.Combine(work, "WINAIMAGIC.md")));
        chat.Send("Verder", [], C.Id, store, Base);
        await chat.Completion;
        Assert.DoesNotContain("een les", Content(engine.Requests[4]));
        Assert.Equal(Content(engine.Requests[0]), Content(engine.Requests[4]));

        var file = Path.Combine(work, "WINAIMAGIC.md");
        File.AppendAllText(file, "EIGEN REGEL\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        chat.Send("En nu", [], C.Id, store, Base);
        await chat.Completion;
        Assert.Contains("EIGEN REGEL", Content(engine.Requests[5]));
        Assert.Contains("een les", Content(engine.Requests[5]));
    }

    [Fact]
    public async Task NoLessonAfterTwoSteps()
    {
        var engine = new FakeEngine(ListDir("1"), ListDir("2"), () => Say("KLAAR")) { Complete = _ => "- zou er niet moeten komen" };
        await Ask("Doe", engine);
        Assert.Empty(engine.CompleteRequests);
    }

    [Fact]
    public async Task NoLessonAfterTheWeb()
    {
        Settings.Shared.WebSearch = true;
        var engine = new FakeEngine(ListDir("1"), ListDir("2"), ListDir("3"), () => Call("fetch_url", new JsonObject { ["url"] = "nope" }), () => Say("KLAAR")) { Complete = _ => "- een pagina kan hier niets in zetten" };
        await Ask("Doe", engine);
        Assert.Empty(engine.CompleteRequests);
    }

    [Fact]
    public async Task NoLessonAfterAnError()
    {
        var engine = new FakeEngine(ListDir("1"), ListDir("2"), ListDir("3"), () => Error(500, "kapot")) { Complete = _ => "- zou er niet moeten komen" };
        await Ask("Doe", engine);
        Assert.Empty(engine.CompleteRequests);
    }

    [Fact]
    public async Task NoLessonWhenWritingIsDenied()
    {
        Settings.Shared.RightWrite = "deny";
        var engine = Task3(_ => "- zou er niet moeten komen");
        await Ask("Doe", engine);
        Assert.Empty(engine.CompleteRequests);
        Assert.False(File.Exists(Path.Combine(work, "WINAIMAGIC.md")), "no notes file either");
    }

    [Fact]
    public async Task NoLessonsWhenTheSettingIsOff()
    {
        Settings.Shared.LearnFromChats = false;
        var engine = Task3(_ => "- zou er niet moeten komen");
        await Ask("Doe", engine);
        Assert.Empty(engine.CompleteRequests);
    }

    [Fact]
    public async Task NoLessonAfterStop()
    {
        var engine = new FakeEngine(ListDir("1"), ListDir("2"), ListDir("3"), () => Slow(Enumerable.Repeat("x", 2000), TimeSpan.FromMilliseconds(5))) { Complete = _ => "- zou er niet moeten komen" };
        var chat = new ChatClient(engine.Client);
        chat.Send("Doe", [], C.Id, store, Base);
        await Until(() => C.Messages[^1].Text.Length > 3);
        chat.Stop();
        await chat.Completion;
        Assert.Empty(engine.CompleteRequests);
    }

    [Fact]
    public void BusyAndStateEvents()
    {
        var chat = new ChatClient(new FakeEngine(() => Say("x")).Client);
        int changes = 0;
        chat.StateChanged += () => changes++;
        chat.Send("a", [], C.Id, store, Base);
        chat.Completion.GetAwaiter().GetResult();
        Assert.True(changes >= 2);
        Assert.False(chat.Busy);
    }
}

public class StreamThrottleTests : ChatTest
{
    /// <summary>Text never reaches the screen from inside the call that brought it.</summary>
    [Fact]
    public async Task NothingArrivesFromInsideAdd()
    {
        using var owner = new OwnerThread();
        var got = new List<string>();
        var throttle = new StreamThrottle((c, _) => got.Add(c), context: owner);
        var done = new TaskCompletionSource();
        int seen = -1;
        owner.Run(() =>
        {
            throttle.Add("a");
            Thread.Sleep(300);   // the screen is busy for longer than the interval
            seen = got.Count;
            done.SetResult();
        });
        await done.Task;
        Assert.Equal(0, seen);
        await throttle.DrainAsync();
        Assert.Equal(new[] { "a" }, got);
    }

    /// <summary>The hang of 1.9.4: the stream's thread and the screen waited for each other. A screen that does not
    /// answer now holds up nothing, and at most one delivery stands in its queue.</summary>
    [Fact]
    public async Task ABlockedScreenDoesNotHoldUpTheStream()
    {
        using var owner = new OwnerThread();
        using var blocked = new ManualResetEventSlim();
        var got = new System.Text.StringBuilder();
        int deliveries = 0;
        var throttle = new StreamThrottle((c, _) => { got.Append(c); deliveries++; }, context: owner);
        owner.Run(blocked.Wait);

        var expected = new System.Text.StringBuilder();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var producer = Task.Run(() =>
        {
            for (int i = 0; i < 200_000; i++)
            {
                var piece = i + " ";
                expected.Append(piece);
                throttle.Add(piece);
            }
        });
        Assert.True(producer.Wait(TimeSpan.FromSeconds(10)), "the stream waited for the screen");
        await Task.Delay(300);
        Assert.Equal(0, deliveries);

        blocked.Set();
        await throttle.DrainAsync();
        Assert.Equal(expected.ToString(), got.ToString());
        Assert.InRange(deliveries, 1, 2);
    }

    /// <summary>The port of the check that a screen only changes on its own thread.</summary>
    [Fact]
    public async Task TheTextOnlyChangesOnTheOwnersThread()
    {
        using var owner = new OwnerThread();
        var store = new ConversationStore();
        var id = store.Conversations[0].Id;
        var engine = new FakeEngine(() => Slow(Enumerable.Repeat("woord ", 200), TimeSpan.FromMilliseconds(10)));
        var chat = new ChatClient(engine.Client);
        int changes = 0, offOwner = 0, deltas = 0, deltaOffOwner = 0;
        store.Changed += _ =>
        {
            changes++;
            if (Environment.CurrentManagedThreadId != owner.ThreadId) offOwner++;
        };
        chat.Delta += (_, _, _, _) =>
        {
            deltas++;
            if (Environment.CurrentManagedThreadId != owner.ThreadId) deltaOffOwner++;
        };
        var finished = new TaskCompletionSource();
        owner.Run(() =>
        {
            chat.Send("Schrijf", [], id, store, Base);
            chat.Completion.ContinueWith(_ => finished.SetResult());
        });
        await finished.Task;
        Assert.True(changes > 5, changes.ToString());
        Assert.Equal(0, offOwner);
        Assert.Equal(0, deltaOffOwner);
        Assert.InRange(deltas, 2, 40);
        Assert.Equal(string.Concat(Enumerable.Repeat("woord ", 200)), store.Conversations[0].Messages[^1].Text);
    }

    /// <summary>Port of the test that the screen keeps working while the model writes (1.9.4), with long chunks.</summary>
    [Fact]
    public async Task TheScreenKeepsWorkingWhileTheModelWrites()
    {
        using var owner = new OwnerThread();
        var store = new ConversationStore();
        var id = store.Conversations[0].Id;
        var chunk = string.Concat(Enumerable.Repeat("Een zin met woorden. ", 400));
        var engine = new FakeEngine(() => Slow(Enumerable.Repeat(chunk, 60), TimeSpan.FromMilliseconds(5)));
        var chat = new ChatClient(engine.Client);
        int ticks = 0;
        var finished = new TaskCompletionSource();
        owner.Run(() =>
        {
            chat.Send("Maak een AQS", [], id, store, Base);
            chat.Completion.ContinueWith(_ => finished.SetResult());
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!finished.Task.IsCompleted && sw.Elapsed.TotalSeconds < 30)
        {
            // a layout pass of the screen: it gets its turn all the time, and it answers fast
            var tick = new TaskCompletionSource();
            owner.Run(() => { ticks++; tick.SetResult(); });
            Assert.True(await Task.WhenAny(tick.Task, Task.Delay(2000)) == tick.Task, "the screen stopped answering");
            await Task.Delay(10);
        }
        Assert.True(finished.Task.IsCompleted);
        Assert.True(ticks > 1);
        Assert.Contains("Een zin met woorden.", store.Conversations[0].Messages[^1].Text);
        Assert.Equal(chunk.Length * 60, store.Conversations[0].Messages[^1].Text.Length);
    }

    [Fact]
    public async Task AtMostEveryTenthOfASecond()
    {
        var times = new List<TimeSpan>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var got = new System.Text.StringBuilder();
        var throttle = new StreamThrottle((c, _) => { lock (times) { times.Add(sw.Elapsed); got.Append(c); } });
        while (sw.Elapsed.TotalSeconds < 1)
        {
            throttle.Add("x");
            await Task.Delay(1);
        }
        await throttle.DrainAsync();
        Assert.InRange(times.Count, 3, 12);
        for (int i = 1; i < times.Count; i++)
            Assert.True((times[i] - times[i - 1]).TotalMilliseconds >= 90, (times[i] - times[i - 1]).ToString());
    }

    [Fact]
    public async Task ThinkingAndTextStayApart()
    {
        var content = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();
        var throttle = new StreamThrottle((c, r) => { content.Append(c); reasoning.Append(r); }, TimeSpan.FromMilliseconds(5));
        throttle.Add("a", "1");
        throttle.Add("", "2");
        throttle.Add("b");
        await throttle.DrainAsync();
        Assert.Equal("ab", content.ToString());
        Assert.Equal("12", reasoning.ToString());
        await throttle.DrainAsync();
    }

    [Fact]
    public async Task AChatsStreamTextGoesThroughTheThrottle()
    {
        var store = new ConversationStore();
        var engine = new FakeEngine(() => Slow(Enumerable.Repeat("woord ", 100), TimeSpan.FromMilliseconds(5)));
        var chat = new ChatClient(engine.Client);
        int deltas = 0, tokens = 0;
        chat.Delta += (_, _, _, _) => Interlocked.Increment(ref deltas);
        chat.TokensStreamed += n => Interlocked.Add(ref tokens, n);
        chat.Send("a", [], store.Conversations[0].Id, store, Base);
        await chat.Completion;
        Assert.Equal(100, tokens);
        Assert.InRange(deltas, 1, 20);
    }
}

/// <summary>A new chat must have the folder chosen last, or the model gets no tools and says it cannot work on this PC.</summary>
public class ChatFolderTests : ChatTest
{
    public ChatFolderTests() { Settings.Shared.Workspace = @"C:\tmp\laatste-map"; }

    [Fact]
    public void EmptyChatWithoutFolderGetsTheLastFolder()
    {
        var store = new ConversationStore();
        store.Conversations = [new Conversation()];
        store.NewConversation();
        Assert.Single(store.Conversations);
        Assert.Equal(@"C:\tmp\laatste-map", store.Conversations[0].Workspace);
        Assert.Equal(store.Conversations[0].Id, store.Selection);
    }

    [Fact]
    public void NewChatAfterAChatGetsTheLastFolder()
    {
        var store = new ConversationStore();
        var old = new Conversation { Title = "Oud" };
        old.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = "hallo" });
        store.Conversations = [old];
        store.NewConversation();
        Assert.Equal(2, store.Conversations.Count);
        Assert.Equal(@"C:\tmp\laatste-map", store.Conversations[0].Workspace);
        Assert.Null(store.Conversations[1].Workspace);
    }

    [Fact]
    public void ChatKeepsItsOwnFolder()
    {
        var store = new ConversationStore();
        store.Conversations = [new Conversation { Workspace = @"C:\tmp\eigen-map" }];
        store.NewConversation();
        Assert.Equal(@"C:\tmp\eigen-map", store.Conversations[0].Workspace);
    }

    [Fact]
    public void NoFolderChosenYetLeavesTheChatWithout()
    {
        Settings.Shared.Workspace = "";
        var store = new ConversationStore();
        store.Conversations = [new Conversation()];
        store.NewConversation();
        Assert.Null(store.Conversations[0].Workspace);
    }

    [Fact]
    public void OutsideTheFolderPointsToRunCommand() =>
        Assert.Contains("run_command", ToolError.Outside(@"C:\Program Files").Message);

    [Fact]
    public void DeleteLeavesAChat()
    {
        var store = new ConversationStore();
        store.Delete(store.Conversations[0].Id);
        Assert.Single(store.Conversations);
        Assert.Equal(store.Conversations[0].Id, store.Selection);
    }
}

public class ConversationFileTests : ChatTest
{
    [Fact]
    public void SameFieldsAndIso8601()
    {
        var store = new ConversationStore();
        var c = store.Conversations[0];
        var user = new ChatMessage { Role = ChatRole.User, Text = "v", Attachments = [new Attachment { Filename = "a.jpg", Jpeg = [1, 2, 3] }] };
        var reply = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Text = "t",
            Reasoning = "r",
            ToolCalls = [new ToolCall("i", "n", "{}")],
            Activities = [new ToolActivity { Label = "l", Done = true, Output = "o", Denied = true }],
            Sources = [new Source("s", "https://s")],
            TokensPerSecond = 1.5,
            PromptTokensPerSecond = 2.5,
            Error = "e",
            Automatic = true,
        };
        c.Messages.AddRange([user, reply, new ChatMessage { Role = ChatRole.Tool, Text = "x", ToolCallId = "i" }]);
        c.Title = "T";
        c.Workspace = @"C:\w";
        c.ContextTokens = 5;
        c.Summary = "S";
        c.SummaryUpTo = user.Id;
        store.Save();

        var json = JsonNode.Parse(File.ReadAllText(Paths.ConversationsFile))![0]!;
        foreach (var key in new[] { "id", "title", "messages", "workspace", "updated", "contextTokens", "summary", "summaryUpTo" })
            Assert.NotNull(json[key]);
        Assert.Equal(8, json.AsObject().Count);
        var m = json["messages"]![1]!;
        foreach (var key in new[] { "id", "role", "text", "reasoning", "attachments", "toolCalls", "activities", "sources", "tokensPerSecond", "promptTokensPerSecond", "error", "date", "automatic" })
            Assert.NotNull(m[key]);
        Assert.Equal("assistant", m["role"]!.GetValue<string>());
        Assert.Null(m["toolCallId"]);
        Assert.Equal("AQID", json["messages"]![0]!["attachments"]![0]!["jpeg"]!.GetValue<string>());
        Assert.Equal("a.jpg", json["messages"]![0]!["attachments"]![0]!["filename"]!.GetValue<string>());
        Assert.Equal("n", m["toolCalls"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("{}", m["toolCalls"]![0]!["arguments"]!.GetValue<string>());
        Assert.Equal("l", m["activities"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("https://s", m["sources"]![0]!["url"]!.GetValue<string>());
        Assert.Equal("i", json["messages"]![2]!["toolCallId"]!.GetValue<string>());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d", m["date"]!.GetValue<string>());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT", json["updated"]!.GetValue<string>());

        // and back
        var again = new ConversationStore();
        var c2 = again.Conversations[0];
        Assert.Equal(c.Id, c2.Id);
        Assert.Equal("t", c2.Messages[1].Text);
        Assert.Equal(new byte[] { 1, 2, 3 }, c2.Messages[0].Attachments[0].Jpeg);
        Assert.Equal(c.SummaryUpTo, c2.SummaryUpTo);
        Assert.Equal(reply.Date, c2.Messages[1].Date);
        Assert.Equal(2, c2.Unsummarized.Count);
    }

    [Fact]
    public void NewestFirstOnLoad()
    {
        var store = new ConversationStore();
        var older = new Conversation { Title = "oud", Updated = DateTimeOffset.UtcNow.AddDays(-2) };
        var newer = new Conversation { Title = "nieuw", Updated = DateTimeOffset.UtcNow };
        store.Conversations = [older, newer];
        store.Save();
        Assert.Equal("nieuw", new ConversationStore().Conversations[0].Title);
    }

    /// <summary>An unreadable file is kept and reported, and the next save does not overwrite it.</summary>
    [Fact]
    public void UnreadableFileIsKeptAndReported()
    {
        File.WriteAllText(Paths.ConversationsFile, "{ dit is kapot");
        var store = new ConversationStore();
        Assert.Contains(".bad", store.LoadProblem);
        Assert.Single(store.Conversations);
        store.Conversations[0].Title = "nieuw";
        store.Save();
        Assert.Equal("{ dit is kapot", File.ReadAllText(Paths.ConversationsFile + ".bad"));
        Assert.Null(new ConversationStore().LoadProblem);
    }

    [Theory]
    [InlineData("nl", "bewaard als")]
    [InlineData("en", "kept as")]
    [InlineData("de", "aufbewahrt")]
    [InlineData("fr", "conservé")]
    public void ThereportFollowsTheLanguage(string language, string text)
    {
        Settings.Shared.Language = language;
        File.WriteAllText(Paths.ConversationsFile, "[{]");
        Assert.Contains(text, new ConversationStore().LoadProblem);
    }

    [Fact]
    public void NothingWrongNothingReported()
    {
        Assert.Null(new ConversationStore().LoadProblem);
    }

    [Fact]
    public void WhatIsHidden()
    {
        Assert.True(new ChatMessage { Role = ChatRole.Tool }.IsHidden);
        Assert.True(new ChatMessage { Role = ChatRole.System }.IsHidden);
        Assert.True(new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [new ToolCall("", "", "")] }.IsHidden);
        Assert.False(new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [new ToolCall("", "", "")], Activities = [new ToolActivity()] }.IsHidden);
        Assert.False(new ChatMessage { Role = ChatRole.Assistant }.IsHidden);
        Assert.False(new ChatMessage { Role = ChatRole.User }.IsHidden);
    }

    [Theory]
    [InlineData("en", "New conversation")]
    [InlineData("nl", "Nieuw gesprek")]
    public void TheDefaultTitleIsTranslated(string language, string title)
    {
        Settings.Shared.Language = language;
        Assert.Equal(title, new Conversation().DisplayTitle);
        Assert.Equal("Eigen", new Conversation { Title = "Eigen" }.DisplayTitle);
    }
}
