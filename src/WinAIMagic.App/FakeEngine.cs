#if DEBUG
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace WinAIMagic.App;

/// <summary>Hidden, debug builds only (--fake-engine): a scripted stand-in for the engine on a local port, so the chat can be tried
/// without a model. The last question picks the script: stappen, schrijf, systeemwijziging, beheerder, plan, taak, surf, tabel, commando, 4000 code, 4000 tekst, fout, nudge, amber, rood, traag.</summary>
internal static class FakeEngine
{
    public static Uri Start()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var ctx = await listener.GetContextAsync();
                _ = Task.Run(() => Serve(ctx));
            }
        });
        return new Uri($"http://127.0.0.1:{port}");
    }

    private static async Task Serve(HttpListenerContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = JsonNode.Parse(await reader.ReadToEndAsync())!;
            var messages = body["messages"]!.AsArray();
            int lastUser = -1;
            for (int i = messages.Count - 1; i >= 0 && lastUser < 0; i--)
                if (messages[i]!["role"]!.GetValue<string>() == "user" && !IsNudge(messages[i]!)) lastUser = i;
            var ask = lastUser < 0 ? "" : Text(messages[lastUser]!);
            var system = messages.Count > 0 && messages[0]!["role"]!.GetValue<string>() == "system" ? Text(messages[0]!) : "";
            if (DocumentAnswer(system, ask) is { } answer)
            {
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.SendChunked = true;
                var output = ctx.Response.OutputStream;
                if (answer.Length > 0) await Words(output, "content", answer, ask.Contains("traag") ? 40 : answer.Length > 20_000 ? 0 : 8);
                await Send(output, new JsonObject(), "stop", new JsonObject { ["timings"] = new JsonObject { ["predicted_per_second"] = 38.4 } });
                await output.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
                ctx.Response.Close();
                return;
            }
            int tools = 0, nudges = 0;
            for (int i = lastUser + 1; i < messages.Count; i++)
            {
                var role = messages[i]!["role"]!.GetValue<string>();
                if (role == "tool") tools++;
                if (role == "user") nudges++;
            }
            if (body["stream"]?.GetValue<bool>() != true)
            {
                await Json(ctx, new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = "## Doel\nEen voorbeeld van een samenvatting.\n\n- Beslissing: **doorgaan**\n- Bestand: `notes.txt`" } }) }.ToJsonString());
                return;
            }
            if (ask.Contains("fout"))
            {
                ctx.Response.StatusCode = 500;
                await Json(ctx, "{\"error\":\"De fake engine geeft hier een fout.\"}");
                return;
            }
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.SendChunked = true;
            await Script(ctx.Response.OutputStream, ask, tools, nudges);
            ctx.Response.Close();
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>The documents module talks to the same address; its system prompts say which piece of work this is.</summary>
    private static string? DocumentAnswer(string system, string ask)
    {
        if (system.Contains("hoe een document eruit moet zien")) return ask.Contains("stille secties") ? SilentPlan : Plan;
        if (ask.Contains("Schrijf nu de inhoud van sectie '"))
        {
            if (ask.Contains("stille secties")) return "";
            int from = ask.LastIndexOf("Schrijf nu de inhoud van sectie '", StringComparison.Ordinal) + 33;
            var title = ask[from..ask.IndexOf('\'', from)];
            return ask.Contains("4000 regels") && title == "Aanleiding"
                ? string.Concat(Enumerable.Range(1, 4000).Select(i => $"Regel {i} van een lang document met tekst die doorloopt op de volgende regel van het scherm.\n\n"))
                : Section(title);
        }
        if (system.Contains("Je vult velden"))
        {
            int at = ask.IndexOf("Velden: ", StringComparison.Ordinal) + 8;
            return string.Join("\n", ask[at..ask.IndexOf('\n', at)].Split(", ").Select(f => $"{f}: Testdam"));
        }
        if (system.Contains("Je bewerkt een stuk"))
        {
            int open = ask.LastIndexOf("<<<\n", StringComparison.Ordinal) + 4, close = ask.LastIndexOf("\n>>>", StringComparison.Ordinal);
            var old = open > 3 && close > open ? ask[open..close].TrimEnd() : "";
            return old + "\n\nDeze alinea komt van de fake engine en bevestigt de wijziging.";
        }
        if (system.Contains("Je haalt uit een webpagina")) return "De BIO vraagt om multi-factor authenticatie bij toegang op afstand. Dit geldt sinds 2020.";
        if (system.Contains("Je maakt volledige")) return "Notities: het zaaksysteem wordt vervangen; besluit van de stuurgroep.";
        return null;
    }

    private const string Plan = "TITEL: Vervanging zaaksysteem\nOPBOUW: eigen keuze\n# Samenvatting\n> De kern in vijf regels.\n# Aanleiding\n> Waarom nu.\n## Huidige situatie\n> Wat er staat.\n## Knelpunten\n> Wat er misgaat.\n# Aanpak\n> Hoe het verloopt.\n# Planning en kosten\n> Een tabel per fase.\nZOEK: BIO eisen multi-factor authenticatie\n";

    private static string SilentPlan
    {
        get
        {
            var title = string.Concat(Enumerable.Repeat("Heel lange titel van een sectie ", 6));
            return "TITEL: Rapport\nOPBOUW: x\n" + string.Concat(Enumerable.Range(1, 25).Select(i => $"# {title}{i}\n")) + "ZOEK: " + string.Concat(Enumerable.Repeat("zoekvraag met heel veel woorden ", 12)) + "\n";
        }
    }

    private static string Section(string title) => title switch
    {
        "Samenvatting" => "De gemeente vervangt het zaaksysteem in drie fasen. De kosten blijven binnen het budget van de stuurgroep en de toegang voldoet aan de BIO [1].\n\n- **Besluit:** doorgaan met fase 1\n- **Eerste oplevering:** tweede kwartaal",
        "Planning en kosten" => "De planning volgt de drie fasen uit het besluit.\n\n| Fase | Periode | Kosten |\n|---|---|---|\n| Voorbereiding | Q1 | 40.000 |\n| Migratie | Q2 | 120.000 |\n| Nazorg | Q3 | 25.000 |\n\nDe bedragen zijn exclusief btw.",
        "Knelpunten" => "Drie zaken vragen nu de meeste aandacht:\n\n1. De leverancier stopt met onderhoud aan de huidige versie.\n2. Koppelingen met het documentsysteem vallen regelmatig uit.\n3. Toegang op afstand gebeurt nog zonder tweede factor [1].",
        _ => $"Deze tekst gaat over {title.ToLowerInvariant()}. De afdeling heeft de uitgangspunten met de stuurgroep afgestemd en legt elk besluit vast in het projectdossier. Waar gegevens ontbreken, staat dat er bij.\n\nNog te bevestigen: de precieze startdatum van de eerste fase.",
    };

    private static bool IsNudge(JsonNode m) => Text(m).StartsWith("Je stopte", StringComparison.Ordinal) || Text(m).StartsWith("You stopped", StringComparison.Ordinal);

    private static string Text(JsonNode m) => m["content"] is JsonValue v ? v.GetValue<string>() : m["content"]?.ToJsonString() ?? "";

    private static async Task Json(HttpListenerContext ctx, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static Task Send(Stream s, JsonObject delta, string? finish = null, JsonObject? extra = null)
    {
        var chunk = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = delta, ["finish_reason"] = finish }) };
        if (extra != null) foreach (var (k, v) in extra) chunk[k] = v?.DeepClone();
        return s.WriteAsync(Encoding.UTF8.GetBytes("data: " + chunk.ToJsonString() + "\n\n")).AsTask();
    }

    private static async Task Words(Stream s, string key, string text, int delay)
    {
        foreach (var w in text.Split(' '))
        {
            await Send(s, new JsonObject { [key] = w + " " });
            await s.FlushAsync();
            await Task.Delay(delay);
        }
    }

    private static async Task ToolCall(Stream s, string name, string args, string id)
    {
        await Send(s, new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = id, ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } }) });
    }

    private static async Task Script(Stream s, string ask, int tools, int nudges)
    {
        int delay = ask.Contains("traag") ? 120 : 25;
        int tokens = ask.Contains("rood") ? 62_000 : ask.Contains("amber") ? 50_000 : 9_100 + tools * 700;
        string finish = "stop";
        if (ask.Contains("stappen") && tools < 6)
        {
            await Words(s, "reasoning_content", $"Stap {tools + 1}: ik bekijk wat er in de map staat.", delay);
            await ToolCall(s, "list_dir", "{\"path\":\".\"}", $"call_{tools}");
            finish = "tool_calls";
        }
        else if (ask.Contains("schrijf") && tools == 0)
        {
            await Words(s, "reasoning_content", "Ik maak het bestand aan.", delay);
            await ToolCall(s, "write_file", "{\"path\":\"demo.txt\",\"content\":\"Regel een\\nRegel twee\\nRegel drie\\n\"}", "call_w");
            finish = "tool_calls";
        }
        else if ((ask.Contains("systeemwijziging") || ask.Contains("beheerder")) && tools == 0)
        {
            // a plan on the app's own test key; "beheerder" marks it as an administrator step (the card only: answer Weigeren)
            bool admin = ask.Contains("beheerder");
            var plan = new JsonObject
            {
                ["summary"] = "Kleur op groen",
                ["steps"] = new JsonArray(new JsonObject
                {
                    ["title"] = "kleur op groen zetten",
                    ["check"] = "Get-ItemPropertyValue -Path 'HKCU:\\Software\\WinAIMagicTest' -Name color 2>$null",
                    ["apply"] = "New-Item -Path 'HKCU:\\Software\\WinAIMagicTest' -Force | Out-Null; Set-ItemProperty -Path 'HKCU:\\Software\\WinAIMagicTest' -Name color -Value groen",
                    ["undo"] = "Set-ItemProperty -Path 'HKCU:\\Software\\WinAIMagicTest' -Name color -Value blauw",
                    ["admin"] = admin,
                }),
            };
            await ToolCall(s, "system_change", plan.ToJsonString(), "call_s");
            finish = "tool_calls";
        }
        else if (ask.Contains("plan") && tools == 0)
        {
            // the step list above the composer
            await ToolCall(s, "task_list", "{\"steps\":[{\"title\":\"Eerst kijken\",\"status\":\"done\"},{\"title\":\"Dan bouwen\",\"status\":\"doing\"},{\"title\":\"Testen\",\"status\":\"todo\"}]}", "call_p");
            finish = "tool_calls";
        }
        else if (ask.Contains("taak") && tools == 0)
        {
            await ToolCall(s, "schedule_task", "{\"title\":\"Schijfoverzicht\",\"prompt\":\"Hoeveel ruimte is er vrij op C:?\",\"when\":\"weekdays\",\"time\":\"08:00\"}", "call_t");
            finish = "tool_calls";
        }
        else if (ask.Contains("surf") && tools == 0)
        {
            // the browser agent: its own window, the page as text (Instellingen → Agent → Browser on)
            await ToolCall(s, "browser_open", "{\"url\":\"https://example.com\"}", "call_b");
            finish = "tool_calls";
        }
        else if (ask.Contains("bestand") && tools == 0)
        {
            // a long write_file, its arguments in pieces: the reply says what is being prepared
            await ToolCall(s, "write_file", "{\"path\":\"demo.bas\",\"content\":\"", "call_f");
            for (int i = 1; i <= 120; i++)
            {
                await Send(s, new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["function"] = new JsonObject { ["arguments"] = $"{i * 10} PRINT \\\"REGEL {i}\\\"\\n" } }) });
                await s.FlushAsync();
                await Task.Delay(delay * 2);
            }
            await Send(s, new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["function"] = new JsonObject { ["arguments"] = "\"}" } }) });
            finish = "tool_calls";
        }
        else if (ask.Contains("meekijken"))
        {
            // three rounds of code, each with a screenshot made by a command: earlier texts shorten while it works, the pictures
            // show with their steps, and stay in a row under the folded work when it is done
            if (tools < 3)
            {
                var code = new StringBuilder($"Ronde {tools + 1}, hier is de code:\n\n```asm\n");
                for (int i = 1; i <= 60; i++) code.Append($"        lda #${i:x2}          ; regel {i} van ronde {tools + 1}\n");
                code.Append("```");
                await Words(s, "content", code.ToString(), delay / 4);
                string[] colors = ["Red", "SeaGreen", "RoyalBlue"];
                var shot = "Add-Type -AssemblyName System.Drawing; New-Item -ItemType Directory -Force shots | Out-Null; "
                    + "$b = New-Object System.Drawing.Bitmap 384,272; $g = [System.Drawing.Graphics]::FromImage($b); "
                    + $"$g.Clear([System.Drawing.Color]::{colors[tools]}); $g.DrawString('RONDE {tools + 1}', (New-Object System.Drawing.Font 'Consolas',28), [System.Drawing.Brushes]::White, 90, 110); "
                    + $"$b.Save(\"$PWD\\shots\\scherm{tools + 1}.png\"); 'screenshot: shots\\scherm{tools + 1}.png'";
                await ToolCall(s, "run_command", new JsonObject { ["command"] = shot }.ToJsonString(), $"call_m{tools}");
                finish = "tool_calls";
            }
            else await Words(s, "content", "KLAAR: het intro werkt, de rand wisselt van kleur.", delay);
        }
        else if (ask.Contains("tabel") && tools == 0)
        {
            await Words(s, "content", "Je pc heeft **150 GB** vrij.\n\n| Schijf | Totaal | Vrij |\n|---|--:|--:|\n| C: | 1,8 TB | 150 GB |\n| **D:** | 4 TB | 2,1 TB |\n\nDe rest staat elders.", delay);
        }
        else if (ask.Contains("commando") && tools == 0)
        {
            await ToolCall(s, "run_command", "{\"command\":\"Get-ChildItem -Recurse | Measure-Object\"}", "call_r");
            finish = "tool_calls";
        }
        else if (ask.Contains("denkregels"))
        {
            // thinking over several lines, slowly: the closed thought shows its last lines one below the other
            await Words(s, "reasoning_content", "Eerst de vorm van de sprite.\nHij is 24 tekens breed en 21 regels hoog.\n"
                + "Regel een: ......XX..XX......\nRegel twee: .....XXXX.XXXX.....\nDat is nog niet herkenbaar, ik probeer het opnieuw.\n"
                + "Nu de kleuren: geel lichaam en rode wangen.", 150);
            await Send(s, new JsonObject { ["content"] = "KLAAR." });
        }
        else if (ask.Contains("nudge") && nudges == 0)
        {
            await Words(s, "reasoning_content", "Ik denk hier alleen over na en stop dan.", delay);
        }
        else if (ask.Contains("4000 code"))
        {
            await Words(s, "reasoning_content", "Dit wordt een lang bestand van vierduizend regels.", delay);
            await Send(s, new JsonObject { ["content"] = "Hier is het hele bestand:\n\n```csharp\n" });
            var o = new StringBuilder();
            for (int i = 1; i <= 4000; i++)
            {
                o.Append("    public static int Line").Append(i).Append("(int x) => x * ").Append(i).Append(" + Compute(x, \"regel ").Append(i).Append("\");\n");
                if (i % 8 != 0) continue;
                await Send(s, new JsonObject { ["content"] = o.ToString() });
                await s.FlushAsync();
                o.Clear();
                await Task.Delay(4);
            }
            await Send(s, new JsonObject { ["content"] = "```\n\nDat waren **4000 regels**." });
        }
        else if (ask.Contains("4000 tekst"))
        {
            var o = new StringBuilder();
            for (int i = 1; i <= 4000; i++)
            {
                o.Append(i % 200 == 1 ? $"\n## Deel {i / 200 + 1}\n\n" : "").Append("- Regel ").Append(i).Append(" met **vet** en `code` en nog wat gewone woorden die erbij horen.\n");
                if (i % 8 != 0) continue;
                await Send(s, new JsonObject { ["content"] = o.ToString() });
                await s.FlushAsync();
                o.Clear();
                await Task.Delay(4);
            }
        }
        else
        {
            await Words(s, "reasoning_content", "De gebruiker vraagt iets eenvoudigs. Ik laat zien wat de opmaak kan: kopjes, lijsten, code en een koppeling. Daarna noem ik kort het resultaat.", delay);
            string prefix = ask.Contains("stappen") || ask.Contains("schrijf") || ask.Contains("commando") ? "KLAAR. " : "";
            await Words(s, "content", prefix + "# Een kop\n\nDit is een **vet** woord, een *schuin* woord, ~~doorgehaald~~, `inline code` en een [koppeling](https://www.example.com).\n\n## Een lijst\n\n- Eerste punt\n- Tweede punt met `code`\n  - Ingesprongen punt\n\n### Code\n\n```python\ndef priem(n):\n    return all(n % i for i in range(2, n))\n\nprint([x for x in range(2, 30) if priem(x)])\n```\n\nEn dan nog een afsluitende alinea die lang genoeg is om over meerdere regels te lopen, zodat je ziet hoe de kaart breekt en hoe de regelafstand eruitziet bij gewone lopende tekst.", delay);
        }
        await Send(s, new JsonObject(), finish, new JsonObject
        {
            ["usage"] = new JsonObject { ["total_tokens"] = tokens },
            ["timings"] = new JsonObject { ["predicted_per_second"] = 38.4, ["prompt_per_second"] = 512.0 },
        });
        await s.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
    }
}

/// <summary>Hidden, debug builds: the documents module without file pickers. --project=folder, --huisstijl=docx, --attach-doc=file;
/// --doc=busy runs 25 silent sections and then shows 4000 lines (the layout rule of DocumentLayoutTests.swift) and logs the sizes.</summary>
internal static class DocumentHooks
{
    public static void Offline(WinAIMagic.Studio.DocumentStudio s)
    {
        s.SearchWeb = (_, _) => Task.FromResult(new List<WinAIMagic.Chat.WebTools.Hit> { new("Baseline Informatiebeveiliging Overheid", "https://bio.example/mfa", "") });
        s.FetchPage = (_, _) => Task.FromResult(("Baseline Informatiebeveiliging Overheid", "Pagina over multi-factor authenticatie."));
    }

    public static void Log(string line) => File.AppendAllText(Path.Combine(WinAIMagic.Core.Support.Paths.AppData, "doc-test.log"), line + "\n");

    /// <summary>Where the editor is in the window, the way the layout tests look at it.</summary>
    public static void LogLayout(string when, Views.DocumentsPage page, Microsoft.UI.Xaml.Window window)
    {
        var e = page.Editor;
        var box = e.TransformToVisual(window.Content).TransformBounds(new Windows.Foundation.Rect(0, 0, e.ActualWidth, e.ActualHeight));
        var root = (Microsoft.UI.Xaml.FrameworkElement)window.Content;
        bool inside = box.Top >= 0 && box.Bottom <= root.ActualHeight + 1 && box.Right <= root.ActualWidth + 1;
        Log($"{when}: editor {box.Width:0}x{box.Height:0} at {box.Top:0}..{box.Bottom:0}, window {root.ActualWidth:0}x{root.ActualHeight:0}, inside={inside}, height>80={box.Height > 80}");
    }

    public static async Task Busy(AppState app, Views.DocumentsPage page, Microsoft.UI.Xaml.Window window)
    {
        var s = app.Studio;
        var query = string.Concat(Enumerable.Repeat("zoekvraag met heel veel woorden ", 12));
        s.SearchWeb = (_, _) => Task.FromResult(new List<WinAIMagic.Chat.WebTools.Hit> { new(query, "https://a.example/1", "") });
        s.FetchPage = (_, _) => throw new HttpRequestException("geen verbinding");
        var file = Path.Combine(s.Folder, "b.txt");
        Directory.CreateDirectory(s.Folder);
        await File.WriteAllTextAsync(file, "Iets om te lezen.");
        s.AddAttachment(file);
        s.Make("Maak iets: stille secties", true);
        await s.Completion;
        Log($"notes {s.Notes.Count}, steps {s.Steps.Count}, problem [{s.Problem}]");
        await Task.Delay(800);
        LogLayout("25 notes", page, window);
        s.Text = string.Join("\n\n", Enumerable.Range(1, 4000).Select(i => $"Regel {i} van een lang document met tekst die doorloopt op de volgende regel van het scherm."));
        await Task.Delay(1500);
        LogLayout("25 notes, 4000 lines", page, window);
    }
}
#endif
