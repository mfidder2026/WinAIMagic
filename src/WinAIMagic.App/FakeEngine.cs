#if DEBUG
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace WinAIMagic.App;

/// <summary>Hidden, debug builds only (--fake-engine): a scripted stand-in for the engine on a local port, so the chat can be tried
/// without a model. The last question picks the script: stappen, schrijf, commando, 4000 code, 4000 tekst, fout, nudge, amber, rood, traag.</summary>
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
        else if (ask.Contains("commando") && tools == 0)
        {
            await ToolCall(s, "run_command", "{\"command\":\"Get-ChildItem -Recurse | Measure-Object\"}", "call_r");
            finish = "tool_calls";
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
#endif
