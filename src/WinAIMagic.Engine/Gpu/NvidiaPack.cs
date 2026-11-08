using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>Where the pack of this app version is published: written by scripts/package.py into the setup's engine folder.</summary>
public sealed record NvidiaPackInfo(string Version, string Url, ulong Size, string Sha256);

/// <summary>The setup comes without the NVIDIA engines (engine\cuda and engine\tiered\bin, with cuBLAS about 1.6 GB), so a PC
/// without such a card downloads far less. On a PC with one the system check offers them: one zip from the release of the same
/// version, checked against its sha256, unpacked beside the app. The engine is chosen at the start, so it counts after a
/// restart. An update whose pack has the same sha256 keeps the engines already there. An installation without a manifest (the
/// old full setup) never offers the pack.</summary>
public sealed class NvidiaPack
{
    public const string ManifestName = "nvidia-pack.json", StampName = "pack-version.txt";
    /// <summary>What the zip holds, relative to the engine folder; each replaces the folder of that name.</summary>
    public static readonly string[] Folders = ["cuda", Path.Combine("tiered", "bin")];
    private const int Attempts = 5;

    public enum PackState { Idle, Downloading, Installing, Installed, Failed }

    private readonly HttpClient http;
    public NvidiaPack(HttpClient? http = null, string? engineRoot = null)
    {
        this.http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        EngineRoot = engineRoot ?? Paths.EngineRoot;
    }

    public string EngineRoot { get; }
    public PackState State { get; private set; }
    public ulong Received { get; private set; }
    public string? Error { get; private set; }
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public event Action? Changed;

    /// <summary>The manifest beside the engines; null in the setup's installation, which has everything.</summary>
    public NvidiaPackInfo? Info
    {
        get
        {
            try
            {
                var path = Path.Combine(EngineRoot, ManifestName);
                return File.Exists(path) ? JsonSerializer.Deserialize<NvidiaPackInfo>(File.ReadAllText(path), Json) : null;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The engines of this version are there: a pack with other contents left behind by an update does not count. The stamp
    /// is the pack's sha256 (the version for packs installed before 1.14.3).</summary>
    public bool Present
    {
        get
        {
            if (!File.Exists(Path.Combine(EngineRoot, "cuda", "llama-server.exe"))) return false;
            if (Info is not { } info) return true;
            var stamp = Path.Combine(EngineRoot, "cuda", StampName);
            if (!File.Exists(stamp)) return false;
            var was = File.ReadAllText(stamp).Trim();
            return was.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase) || was == info.Version;
        }
    }

    /// <summary>Worth offering: this installation has a manifest, the engines are missing (or other), and there is an NVIDIA card,
    /// which then runs on Vulkan, or on the old engines.</summary>
    public bool Offered(IGpuBackend backend) =>
        Info != null && !Present && (backend.Id == "cuda" || backend.Adapters.Any(a => a.VendorId == GpuAdapter.NvidiaId));

    /// <summary>Downloads, checks and unpacks; true when the engines are in place. Never throws: the error is in `Error`.</summary>
    public async Task<bool> Install(CancellationToken ct = default)
    {
        if (State is PackState.Downloading or PackState.Installing) return false;
        if (Info is not { } info) return Fail(L("Deze installatie heeft geen NVIDIA-pakket nodig."));
        Error = null;
        Received = 0;
        Set(PackState.Downloading);
        var part = Path.Combine(EngineRoot, "nvidia-pack.zip.part");
        var fresh = Path.Combine(EngineRoot, "nvidia-new");
        try
        {
            string? lastError = null;
            for (int attempt = 1; attempt <= Attempts && Size(part) < info.Size; attempt++)
            {
                try
                {
                    await Download(info, part, ct);
                    lastError = null;
                }
                catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    lastError = e.Message;
                    await Task.Delay(RetryDelay, ct);
                }
            }
            if (Size(part) != info.Size)
            {
                // a wrong size means a wrong file: the next try starts from the beginning
                if (Size(part) > info.Size) File.Delete(part);
                return Fail(lastError ?? L($"Het pakket is {Size(part)} in plaats van {info.Size} bytes."));
            }
            Set(PackState.Installing);
            string got;
            await using (var f = File.OpenRead(part)) got = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
            if (!got.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                return Fail(L("De controlesom van het pakket klopt niet. Probeer het opnieuw."));
            }
            if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
            await Task.Run(() => ZipFile.ExtractToDirectory(part, fresh), ct);
            // whole folders at once: an engine is never half old and half new
            foreach (var folder in Folders)
            {
                var from = Path.Combine(fresh, folder);
                if (!Directory.Exists(from)) return Fail(L($"Het pakket mist {folder}."));
            }
            foreach (var folder in Folders)
            {
                var to = Path.Combine(EngineRoot, folder);
                if (Directory.Exists(to)) Directory.Delete(to, true);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(Path.Combine(fresh, folder), to);
            }
            File.WriteAllText(Path.Combine(EngineRoot, "cuda", StampName), info.Sha256);
            File.Delete(part);
            Directory.Delete(fresh, true);
            Set(PackState.Installed);
            return true;
        }
        catch (OperationCanceledException) { return Fail(L("Gestopt.")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException)
        {
            return Fail(e.Message);
        }
    }

    private async Task Download(NvidiaPackInfo info, string part, CancellationToken ct)
    {
        long have = (long)Size(part);
        using var request = new HttpRequestMessage(HttpMethod.Get, info.Url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        // a server that ignores the range sends it all again
        bool resumed = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        Received = resumed ? (ulong)have : 0;
        var buffer = new byte[1 << 20];
        var shown = DateTime.UtcNow;
        int n;
        while ((n = await body.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            Received += (ulong)n;
            if (DateTime.UtcNow - shown > TimeSpan.FromSeconds(1)) { shown = DateTime.UtcNow; Changed?.Invoke(); }
        }
    }

    public const string ItemId = "nvidia";

    /// <summary>Its line in the system check: the offer on a PC with such a card, then the download's progress.</summary>
    public SystemCheck.Item? CheckItem(IGpuBackend backend)
    {
        string title = L("NVIDIA-pakket");
        var todo = SystemCheck.CheckState.Todo;
        switch (State)
        {
            case PackState.Downloading:
                return new(ItemId, title, L($"Downloaden: {Received.Gb()} van {(Info?.Size ?? 0).Gb()}"), todo);
            case PackState.Installing:
                return new(ItemId, title, L("Controleren en uitpakken…"), todo);
            case PackState.Installed:
                return new(ItemId, title, L("Geïnstalleerd. Herstart WinAIMagic om de NVIDIA-kaart te gebruiken."), todo, L("Herstart nu"), SystemCheck.CheckAction.RestartApp);
            case PackState.Failed:
                return new(ItemId, title, L($"Mislukt: {Error}"), todo, L("Probeer opnieuw"), SystemCheck.CheckAction.InstallNvidiaPack);
        }
        if (!Offered(backend)) return null;
        var size = (Info?.Size ?? 0).Gb();
        return new(ItemId, title,
            L($"Deze pc heeft een NVIDIA-kaart. Met het NVIDIA-pakket ({size}) rekenen de modellen daarop sneller dan nu met Vulkan, en draaien ook de grote modellen. Het komt in de map van de app."),
            todo, L("Download NVIDIA-pakket"), SystemCheck.CheckAction.InstallNvidiaPack);
    }

    private static ulong Size(string path) => File.Exists(path) ? (ulong)new FileInfo(path).Length : 0;

    private bool Fail(string why)
    {
        Error = why;
        Set(PackState.Failed);
        return false;
    }

    private void Set(PackState state)
    {
        State = state;
        Changed?.Invoke();
    }
}
