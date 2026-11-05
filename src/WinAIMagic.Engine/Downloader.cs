using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

public sealed record DownloadItem(string Dir, string Name, ulong Size, IReadOnlyList<string> Sha256, string Url, IReadOnlyList<int>? Stamp);

/// <summary>One file at a time per Fetch; the store runs one Fetch per file, in parallel. Same behaviour as the Mac's script:
/// 10 attempts, resume with Range, exact size, sha256, the stamp patched after hashing, an .ok beside the verified file.
/// The file grows from the start and is never reserved up front, so the model can be chosen once its headers are in.</summary>
public sealed class Downloader
{
    public const int Attempts = 10;
    private const int Chunk = 1 << 20;
    /// <summary>A connection that delivers nothing for this long counts as broken.</summary>
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);

    private readonly HttpClient http;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>The server no longer has a file the catalog names: the rest of that model need not come down either.</summary>
    public Action? Replaced { get; init; }

    public Downloader(HttpClient http) { this.http = http; }

    /// <summary>The server offers a file of another size than the catalog's: the maker replaced it, and no attempt changes that.</summary>
    private sealed class FileReplacedException : Exception { }

    /// <summary>True when the file is verified. `say`: progress; `fail`: what went wrong, for the model card.</summary>
    public async Task<bool> Fetch(DownloadItem item, Action<string> say, Action<string> fail, CancellationToken ct)
    {
        var path = Path.Combine(item.Dir, item.Name);
        Directory.CreateDirectory(item.Dir);
        if (File.Exists(path + ".ok"))
        {
            say(L($"{item.Name}: al gecontroleerd"));
            return true;
        }
        string? lastError = null;
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            if (ModelStore.FileSize(path) >= item.Size) break;
            try
            {
                await GetFile(item, path, ct);
                lastError = null;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
            catch (Exception e) when (e is FileReplacedException or HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone })
            {
                fail(L("De maker heeft dit model op de server vervangen of verwijderd, dus deze versie van WinAIMagic kan het niet meer downloaden. Kijk of er een nieuwere WinAIMagic is."));
                Replaced?.Invoke();
                return false;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                lastError = Describe(e);
                say(L($"{item.Name}: poging {attempt} mislukt: {lastError}"));
                try { await Task.Delay(RetryDelay, ct); }
                catch (OperationCanceledException) { return false; }
            }
        }
        ulong have = ModelStore.FileSize(path);
        if (have != item.Size)
        {
            fail(lastError != null ? L($"{item.Name}: {lastError}") : L($"{item.Name}: grootte {have} in plaats van {item.Size} bytes. Verwijder het model en download het opnieuw."));
            return false;
        }
        say(L($"{item.Name}: sha256 controleren"));
        string got = await HashFile(path, ct);
        if (ct.IsCancellationRequested) return false;
        if (!item.Sha256.Contains(got, StringComparer.OrdinalIgnoreCase))
        {
            fail(L($"{item.Name}: de controlesom klopt niet ({got})"));
            return false;
        }
        // the hash is of the file as published, the stamp comes after it
        if (item.Stamp is { Count: 2 })
        {
            try
            {
                StampSplit(path, item.Stamp[0], item.Stamp[1]);
                say(L($"{item.Name}: gestempeld als deel {item.Stamp[0]} van {item.Stamp[1]}"));
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                fail(L($"{item.Name}: {e.Message}"));
                return false;
            }
        }
        await File.WriteAllTextAsync(path + ".ok", got, ct);
        say(L($"{item.Name}: klaar"));
        return true;
    }

    /// <summary>The URL holds upstream's file name: an error shows the status, never the address.</summary>
    private static string Describe(Exception e) => e switch
    {
        HttpRequestException { StatusCode: HttpStatusCode code } => L($"de server antwoordde {(int)code} ({code})"),
        OperationCanceledException => L("de verbinding viel stil"),
        HttpRequestException => L("geen verbinding met de server"),
        _ => LogFilter.Clean(e.Message),
    };

    private async Task GetFile(DownloadItem item, string path, CancellationToken ct)
    {
        long have = (long)ModelStore.FileSize(path);
        using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        request.Headers.UserAgent.ParseAdd("WinAIMagic");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0) return;   // the size check decides
        response.EnsureSuccessStatusCode();
        bool resume = response.StatusCode == HttpStatusCode.PartialContent;
        // the server names the size of the whole file before a byte is written: finding out after 111 GB helps nobody
        var headers = response.Content.Headers;
        long? whole = resume ? headers.ContentRange?.Length ?? have + headers.ContentLength : headers.ContentLength;
        if (whole is long size && (ulong)size != item.Size) throw new FileReplacedException();
        // FileShare.Read: the plan reads the headers of a shard while it comes in
        using var file = new FileStream(path, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete, Chunk);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[Chunk];
        while (true)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(Stall);
            int n = await body.ReadAsync(buffer, stall.Token);
            if (n == 0) break;
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            // the status every 2 s reads the size on disk
            await file.FlushAsync(ct);
        }
    }

    private static async Task<string> HashFile(string path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Chunk, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buffer = new byte[Chunk];
        try
        {
            int n;
            while ((n = await file.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer, 0, n);
        }
        catch (OperationCanceledException) { return ""; }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>A shard published on its own (as "1 of 1") gets its place in the set written into its header: split.no / split.count.</summary>
    public static void StampSplit(string path, int no, int count)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var head = new byte[Math.Min(1 << 24, f.Length)];
        f.ReadExactly(head);
        foreach (var (key, value) in new[] { ("split.no", no - 1), ("split.count", count) })
        {
            // the key as GGUF writes it: uint64 length, the name, uint32 type 2 (uint16), then the value
            var pattern = new List<byte>(BitConverter.GetBytes((ulong)key.Length));
            pattern.AddRange(Encoding.ASCII.GetBytes(key));
            pattern.AddRange(BitConverter.GetBytes(2u));
            int at = head.AsSpan().IndexOf(pattern.ToArray());
            if (at < 0) throw new InvalidDataException(L($"{key} niet gevonden in de header"));
            f.Position = at + pattern.Count;
            f.Write(BitConverter.GetBytes((ushort)value));
        }
    }
}
