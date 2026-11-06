using System.Globalization;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>The checks a backend makes on this machine; tests answer them without engine files.</summary>
public sealed class BackendProbe
{
    public Func<string, string, bool> EngineFileExists { get; init; } = (folder, file) => File.Exists(Path.Combine(Paths.EngineRoot, folder, file));
    /// <summary>The first card's compute capability from nvidia-smi in a process of its own, null when it says nothing.</summary>
    public Func<string?> ComputeCapability { get; init; } = GpuDetection.ReadComputeCapability;

    /// <summary>Everything is there (a test of the choice itself).</summary>
    public static BackendProbe AllPresent(string computeCapability = "9.0") =>
        new() { EngineFileExists = (_, _) => true, ComputeCapability = () => computeCapability };
}

/// <summary>CUDA: engine\tiered\ for the main engine and engine\cuda\ for llama.cpp. Pascal runs too, as an experiment.</summary>
public sealed class NvidiaBackend : GpuBackendBase
{
    private const double MinCapability = 6.0, ValidatedCapability = 7.0;
    private readonly double capability;
    private readonly BackendProbe probe;

    public NvidiaBackend(IReadOnlyList<GpuAdapter> adapters, BackendProbe probe) : this(adapters, probe, probe.ComputeCapability()) { }

    private NvidiaBackend(IReadOnlyList<GpuAdapter> adapters, BackendProbe probe, string? cc)
        : base(adapters, GpuProfiles.For("nvidia", Architecture(cc), adapters.Aggregate(0UL, (n, a) => Math.Max(n, a.VramBytes))), cc == null ? "" : "compute capability " + cc)
    {
        this.probe = probe;
        capability = double.TryParse(cc, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string Architecture(string? cc) =>
        double.TryParse(cc, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v < ValidatedCapability ? "pascal" : "cc7";

    public override string Id => "cuda";
    public override string DisplayName => "CUDA";
    public override string RpcDevice => "CUDA0";

    public override string? Unavailable =>
        capability == 0 ? L("De videokaart meldt geen compute capability: de driver is te oud of nvidia-smi ontbreekt.")
        : capability < MinCapability ? L($"Compute capability {capability.ToString(CultureInfo.InvariantCulture)} is te oud (minimaal {MinCapability.ToString("0.0", CultureInfo.InvariantCulture)}).")
        : !probe.EngineFileExists(EngineFolder, "llama-server.exe")
            ? probe.EngineFileExists("", NvidiaPack.ManifestName) ? L("Het NVIDIA-pakket is nog niet geïnstalleerd: zie Systeem.") : L($"engine\\{EngineFolder} ontbreekt in de installatie.")
        : null;

    public override string? TieredUnavailable =>
        Unavailable ?? (probe.EngineFileExists("tiered\\bin", "wam-glm.exe") ? null : L("engine\\tiered ontbreekt in de installatie."));

    public override string Summary => capability is > 0 and < ValidatedCapability
        ? DisplayName + " · " + L("experimenteel op deze kaart") : base.Summary;
}

/// <summary>HIP: designed for, but this version ships no binaries, so the choice moves on to Vulkan and says why.</summary>
public sealed class AmdBackend : GpuBackendBase
{
    public AmdBackend(IReadOnlyList<GpuAdapter> adapters)
        : base(adapters, GpuProfiles.For("amd", Gfx(adapters.FirstOrDefault()), adapters.Aggregate(0UL, (n, a) => Math.Max(n, a.VramBytes))), Gfx(adapters.FirstOrDefault())) { }

    public override string Id => "hip";
    public override string DisplayName => "HIP";
    public override string? Unavailable => L("Niet beschikbaar in deze versie van WinAIMagic.");
    public override string? TieredUnavailable => Unavailable;

    /// <summary>Upstream's architecture of a card, from its name (unknown: "unknown", which has no main engine).</summary>
    public static string Gfx(GpuAdapter? a)
    {
        if (a == null) return "unknown";
        if (a.IsHaloApu) return "gfx1151";
        string n = a.Name;
        bool Has(params string[] names) => names.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase));
        if (Has("RX 7900", "W7900", "W7800")) return "gfx1100";
        if (Has("RX 7800", "RX 7700")) return "gfx1101";
        if (Has("RX 7600")) return "gfx1102";
        if (Has("R9700", "RX 9070", "RX 9060", "W9700")) return "gfx1201";
        if (Has("RX 6800", "RX 6900", "RX 6950", "W6800", "V620")) return "gfx1030";
        return "unknown";
    }

    public override ulong RamMargin(ulong ramTotal) =>
        UnifiedMemory ? Math.Max(8UL << 30, ramTotal / 10) : base.RamMargin(ramTotal);

    /// <summary>On a Windows APU the GPU's share counts against RAM + page file, with the margin both engines keep free.</summary>
    public override ulong RequiredPageFile(ulong ramTotal) =>
        UnifiedMemory ? (ulong)Math.Ceiling((VramTotal + Math.Max(8UL << 30, ramTotal / 10)) / (double)(1UL << 30)) << 30 : base.RequiredPageFile(ramTotal);

    public override IReadOnlyDictionary<string, string> TieredConfig => new Dictionary<string, string> { ["backend"] = "hip" };
}

/// <summary>llama.cpp on any card with a Vulkan driver: the limited mode.</summary>
public sealed class VulkanBackend : GpuBackendBase
{
    private readonly BackendProbe probe;

    public VulkanBackend(IReadOnlyList<GpuAdapter> adapters, BackendProbe probe)
        : base(adapters, GpuProfiles.For("vulkan", "*", adapters.Aggregate(0UL, (n, a) => Math.Max(n, a.VramBytes))), "Vulkan")
    {
        this.probe = probe;
    }

    public override string Id => "vulkan";
    public override string DisplayName => "Vulkan";
    public override string RpcDevice => "Vulkan0";
    public override string? Unavailable =>
        Adapters.Count == 0 ? L("Geen videokaart gevonden.")
        : !probe.EngineFileExists(EngineFolder, "llama-server.exe") ? L($"engine\\{EngineFolder} ontbreekt in de installatie.")
        : null;
    public override string? TieredUnavailable => L("De hoofdengine draait niet op Vulkan: alleen de tweede engine, met kleinere modellen of trager.");
}

/// <summary>No usable card: llama.cpp on the processor. Slow, but it works.</summary>
public sealed class CpuBackend : GpuBackendBase
{
    private readonly BackendProbe probe;

    public CpuBackend(BackendProbe probe) : base(Array.Empty<GpuAdapter>(), GpuProfiles.For("cpu", "*", 0), "")
    {
        this.probe = probe;
    }

    public override string Id => "cpu";
    public override string DisplayName => "CPU";
    public override string? Unavailable => probe.EngineFileExists(EngineFolder, "llama-server.exe") ? null : L($"engine\\{EngineFolder} ontbreekt in de installatie.");
    public override string? TieredUnavailable => L("De hoofdengine heeft een videokaart nodig.");
    public override ulong RequiredPageFile(ulong ramTotal) => 0;
}

/// <summary>A backend with the make's real profile, without the hardware: for the tests of everything above this layer.</summary>
public sealed class FakeGpuBackend : GpuBackendBase
{
    private readonly bool amdLike;
    private readonly string id;
    private readonly bool tiered;

    private FakeGpuBackend(string id, IReadOnlyList<GpuAdapter> adapters, GpuProfile profile, bool tiered, bool amdLike)
        : base(adapters, profile, profile.Key)
    {
        this.id = id;
        this.tiered = tiered;
        this.amdLike = amdLike;
    }

    private static GpuAdapter Card(string name, uint vendor, ulong vramBytes, bool display) =>
        new(name, vendor, 1, vramBytes, 0, "1.0", 1, display);

    public static FakeGpuBackend Nvidia(int vramGb, bool drivesDisplay = true, string architecture = "cc7") =>
        new("cuda", new[] { Card($"Fake {vramGb} GB", GpuAdapter.NvidiaId, (ulong)vramGb << 30, drivesDisplay) },
            GpuProfiles.For("nvidia", architecture, (ulong)vramGb << 30), true, false);

    /// <summary>A card with HIP engines as a later version will ship them (gfx1100 unless told otherwise).</summary>
    public static FakeGpuBackend Amd(int vramGb, string architecture = "gfx1100", bool drivesDisplay = true)
    {
        var profile = GpuProfiles.For("amd", architecture, (ulong)vramGb << 30);
        return new FakeGpuBackend("hip", new[] { Card($"Fake {vramGb} GB", GpuAdapter.AmdId, (ulong)vramGb << 30, drivesDisplay) }, profile, profile.Tiered, true);
    }

    /// <summary>Strix Halo: the part of its memory Windows gives the GPU is the VRAM, the rest the RAM.</summary>
    public static FakeGpuBackend StrixHalo(int gpuShareGb) =>
        new("hip", new[] { Card("Radeon 8060S", GpuAdapter.AmdId, (ulong)gpuShareGb << 30, true) }, GpuProfiles.For("amd", "gfx1151", (ulong)gpuShareGb << 30), true, true);

    public static FakeGpuBackend Vulkan(int vramGb, uint vendor = GpuAdapter.IntelId) =>
        new("vulkan", new[] { Card($"Fake {vramGb} GB", vendor, (ulong)vramGb << 30, true) }, GpuProfiles.For("vulkan", "*", (ulong)vramGb << 30), false, false);

    public static FakeGpuBackend Cpu() => new("cpu", Array.Empty<GpuAdapter>(), GpuProfiles.For("cpu", "*", 0), false, false);

    public override string Id => id;
    public override string DisplayName => id;
    public override string? Unavailable => null;
    public override string? TieredUnavailable => tiered ? null : "fake: no main engine";

    public override ulong RamMargin(ulong ramTotal) =>
        amdLike && UnifiedMemory ? Math.Max(8UL << 30, ramTotal / 10) : base.RamMargin(ramTotal);

    public override ulong RequiredPageFile(ulong ramTotal) =>
        amdLike && UnifiedMemory ? (ulong)Math.Ceiling((VramTotal + Math.Max(8UL << 30, ramTotal / 10)) / (double)(1UL << 30)) << 30
        : id == "cpu" ? 0 : base.RequiredPageFile(ramTotal);
}
