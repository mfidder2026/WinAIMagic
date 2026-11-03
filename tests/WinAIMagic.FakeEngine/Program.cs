using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

// A stand-in for llama-server, the serve layer and ggml-rpc-server. Understands:
//   --port P            serves /health, /slots, /metrics and /v1/chat/completions on 127.0.0.1:P
//   --parent-pipe       stops (exit 0) when stdin ends, as the serve layer does
//   --marker FILE       appends how it was stopped ("break" or "pipe") and its environment key to FILE
//   --spawn-child       starts a copy of itself with --sleep, and prints "child <pid>"
//   --sleep             does nothing until killed
//   --exit N            prints an error and exits with code N at once
//   --ignore-break      a break does not stop it
//   --delay-health MS   /health answers 503 for this long
//   --print-name        prints lines with the upstream project's name in them, as a real engine does

string Arg(string name, string fallback = "")
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
bool Has(string name) => args.Contains(name);

if (Has("--version"))
{
    Console.WriteLine("version: 1234 (fake)");
    return 0;
}
if (Has("--exit"))
{
    Console.WriteLine("error: loading the model failed");
    return int.Parse(Arg("--exit"));
}
if (Has("--sleep"))
{
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

string marker = Arg("--marker");
void Mark(string how)
{
    if (marker.Length == 0) return;
    File.AppendAllText(marker, how + " key=" + Environment.GetEnvironmentVariable("LLAMA_API_KEY") + "\n");
}

var stop = new TaskCompletionSource<int>();
if (!Has("--ignore-break"))
{
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Mark("break");
        stop.TrySetResult(0);
    };
}
if (Has("--parent-pipe"))
{
    new Thread(() =>
    {
        var s = Console.OpenStandardInput();
        var b = new byte[4096];
        try { while (s.Read(b, 0, b.Length) > 0) { } } catch (IOException) { }
        Mark("pipe");
        stop.TrySetResult(0);
    }) { IsBackground = true }.Start();
}
if (Has("--spawn-child"))
{
    var self = Environment.ProcessPath!;
    var child = Process.Start(new ProcessStartInfo(self, "--sleep") { UseShellExecute = false, CreateNoWindow = true })!;
    Console.WriteLine("child " + child.Id);
}
if (Has("--print-name"))
{
    Console.WriteLine("[Maya] loading general.name = GLM-5.3-Flash-MAYA-S24");
    Console.WriteLine("llama_model_loader: - kv general.name str = maya");
}
Console.WriteLine("load_tensors: loading");
Console.Out.Flush();

int port = int.Parse(Arg("--port", "0"));
int delay = int.Parse(Arg("--delay-health", "0"));
var clock = Stopwatch.StartNew();
if (port > 0)
{
    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    _ = Task.Run(async () =>
    {
        int decoded = 0;
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[65536];
                    int n = stream.Read(buffer, 0, buffer.Length);
                    var head = Encoding.UTF8.GetString(buffer, 0, Math.Max(n, 0));
                    var path = head.Split(' ').ElementAtOrDefault(1) ?? "/";
                    int status = 200;
                    string body = "{}";
                    if (path == "/health") { if (clock.ElapsedMilliseconds < delay) status = 503; else body = "{\"status\":\"ok\"}"; }
                    else if (path == "/slots") body = "[{\"id\":0,\"is_processing\":true,\"next_token\":[{\"n_decoded\":" + Interlocked.Add(ref decoded, 10) + "}]}]";
                    else if (path == "/metrics") body = "{\"live\":{\"state\":\"generating\",\"tok_s\":12.5},\"tiers\":{\"now\":{\"vram_hit\":0.8}},\"hardware\":{\"gpu\":{\"temp\":61}}}";
                    else if (path.StartsWith("/v1/chat/completions"))
                    {
                        // the rest of a body that did not arrive with the headers
                        int expected = int.TryParse(head.Split('\n').FirstOrDefault(l => l.StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase))?.Split(':')[1].Trim(), out var cl) ? cl : 0;
                        int have = n - head.IndexOf("\r\n\r\n", StringComparison.Ordinal) - 4;
                        while (have < expected) { int m = stream.Read(buffer, 0, buffer.Length); if (m <= 0) break; have += m; }
                        body = "{\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"timings\":{\"prompt_per_second\":100.5,\"prompt_n\":2500,\"predicted_per_second\":20.25,\"predicted_n\":300}}";
                    }
                    var response = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                    stream.Write(Encoding.UTF8.GetBytes(response));
                }
            });
        }
    });
}
return await stop.Task;
