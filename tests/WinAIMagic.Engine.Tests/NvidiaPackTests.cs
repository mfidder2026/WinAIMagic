using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace WinAIMagic.Engine.Tests;

/// <summary>The portable app downloads the NVIDIA engines once, checks them and puts them beside itself.</summary>
public class NvidiaPackTests : EngineTest
{
    private const string Url = "https://example.test/WinAIMagic-NVIDIA-9.9.9.zip";
    private string EngineRoot => Path.Combine(Root, "app", "engine");

    /// <summary>Serves one file and honours a Range header, as GitHub's release downloads do.</summary>
    private sealed class FileServer(byte[] body) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            long from = request.Headers.Range?.Ranges.First().From ?? 0;
            var r = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body, (int)from, body.Length - (int)from),
            };
            return Task.FromResult(r);
        }
    }

    private static byte[] Zip()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var name in new[] { "cuda/llama-server.exe", "cuda/cublasLt64_13.dll", "tiered/bin/wam-glm.exe" })
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write("new " + name + new string('x', 5000));
            }
        }
        return ms.ToArray();
    }

    private void Manifest(byte[] zip, string? sha = null)
    {
        Directory.CreateDirectory(EngineRoot);
        var info = new { version = "9.9.9", url = Url, size = zip.Length, sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(zip)) };
        File.WriteAllText(Path.Combine(EngineRoot, NvidiaPack.ManifestName), JsonSerializer.Serialize(info));
    }

    private NvidiaPack Pack(byte[] zip, out FileServer server)
    {
        server = new FileServer(zip);
        return new NvidiaPack(new HttpClient(server), EngineRoot) { RetryDelay = TimeSpan.Zero };
    }

    [Fact]
    public async Task ThePackGoesBesideTheApp()
    {
        var zip = Zip();
        Manifest(zip);
        // an older pack: its files go, the new folder replaces it whole
        Directory.CreateDirectory(Path.Combine(EngineRoot, "cuda"));
        File.WriteAllText(Path.Combine(EngineRoot, "cuda", "old.dll"), "old");
        Directory.CreateDirectory(Path.Combine(EngineRoot, "tiered", "serve"));
        var pack = Pack(zip, out _);
        Assert.False(pack.Present);

        Assert.True(await pack.Install(), pack.Error);
        Assert.Equal(NvidiaPack.PackState.Installed, pack.State);
        Assert.True(File.Exists(Path.Combine(EngineRoot, "cuda", "llama-server.exe")));
        Assert.True(File.Exists(Path.Combine(EngineRoot, "tiered", "bin", "wam-glm.exe")));
        Assert.False(File.Exists(Path.Combine(EngineRoot, "cuda", "old.dll")));
        Assert.True(Directory.Exists(Path.Combine(EngineRoot, "tiered", "serve")));
        Assert.True(pack.Present);
        Assert.False(File.Exists(Path.Combine(EngineRoot, "nvidia-pack.zip.part")));
        Assert.False(Directory.Exists(Path.Combine(EngineRoot, "nvidia-new")));
    }

    [Fact]
    public async Task AWrongChecksumInstallsNothing()
    {
        var zip = Zip();
        Manifest(zip, sha: new string('0', 64));
        var pack = Pack(zip, out _);
        Assert.False(await pack.Install());
        Assert.Equal(NvidiaPack.PackState.Failed, pack.State);
        Assert.False(Directory.Exists(Path.Combine(EngineRoot, "cuda")));
        Assert.False(File.Exists(Path.Combine(EngineRoot, "nvidia-pack.zip.part")));
    }

    /// <summary>A broken download goes on where it stopped.</summary>
    [Fact]
    public async Task ADownloadGoesOnWhereItStopped()
    {
        var zip = Zip();
        Manifest(zip);
        File.WriteAllBytes(Path.Combine(EngineRoot, "nvidia-pack.zip.part"), zip[..(zip.Length / 2)]);
        var pack = Pack(zip, out var server);
        Assert.True(await pack.Install(), pack.Error);
        Assert.Equal(1, server.Requests);
        Assert.True(pack.Present);
    }

    /// <summary>An update of the portable app leaves the old pack behind: that one is offered again.</summary>
    [Fact]
    public void AnOlderPackIsNotPresent()
    {
        var zip = Zip();
        Manifest(zip);
        Directory.CreateDirectory(Path.Combine(EngineRoot, "cuda"));
        File.WriteAllText(Path.Combine(EngineRoot, "cuda", "llama-server.exe"), "");
        File.WriteAllText(Path.Combine(EngineRoot, "cuda", NvidiaPack.StampName), "9.9.8");
        Assert.False(new NvidiaPack(new HttpClient(), EngineRoot).Present);
    }

    /// <summary>Offered only by the portable app (it has the manifest), and only with an NVIDIA card.</summary>
    [Fact]
    public void OfferedOnlyWithAnNvidiaCard()
    {
        var pack = new NvidiaPack(new HttpClient(), EngineRoot);
        Assert.False(pack.Offered(FakeGpuBackend.Vulkan(8, GpuAdapter.NvidiaId)));   // the setup: no manifest
        Manifest(Zip());
        Assert.True(pack.Offered(FakeGpuBackend.Vulkan(8, GpuAdapter.NvidiaId)));
        Assert.False(pack.Offered(FakeGpuBackend.Vulkan(8, GpuAdapter.AmdId)));
        Assert.False(pack.Offered(FakeGpuBackend.Vulkan(8)));
        Assert.False(pack.Offered(FakeGpuBackend.Cpu()));
    }

    /// <summary>The main engine finds cuBLAS in engine\cuda, where it now lies once.</summary>
    [Fact]
    public void TheMainEngineSeesTheCudaFolder()
    {
        var dirs = FakeGpuBackend.Nvidia(16).TieredLibDirs;
        Assert.Contains(Path.Combine(WinAIMagic.Core.Support.Paths.EngineRoot, "cuda"), dirs);
    }
}
