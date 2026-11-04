using System.Text.Json;
using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;

namespace WinAIMagic.Engine;

/// <summary>One card as DXGI reports it.</summary>
public sealed record GpuAdapter(string Name, uint VendorId, uint DeviceId, ulong DedicatedBytes, ulong SharedBytes,
                                string Driver = "", long Luid = 0, bool DrivesDisplay = false)
{
    public const uint NvidiaId = 0x10DE, AmdId = 0x1002, IntelId = 0x8086;

    /// <summary>A Strix or Gorgon Halo APU: its integrated GPU is a real target (brief 3a).</summary>
    public bool IsHaloApu => VendorId == AmdId && new[] { "8060S", "8065S", "8050S", "8040S", "AI Max", "Strix Halo", "Gorgon Halo" }
        .Any(n => Name.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>A GPU in the processor, which does not count as a target unless it is a Halo APU.</summary>
    public bool IsIntegrated => !IsHaloApu && (DedicatedBytes < 1UL << 30
        || (VendorId == AmdId && (Name.Contains("Radeon(TM) Graphics") || Name.EndsWith("Radeon Graphics") || Name.Contains("M Graphics"))));

    /// <summary>The card's memory; on an APU the part Windows gives the GPU (Variable Graphics Memory).</summary>
    public ulong VramBytes => DedicatedBytes;
}

/// <summary>Everything that differs per make of card: only this interface and gpu-profiles.json know about makes (brief 3a).</summary>
public interface IGpuBackend
{
    /// <summary>The value of Settings.GpuBackendOverride.</summary>
    string Id { get; }
    string DisplayName { get; }
    /// <summary>The folder under the engine root with this backend's llama-server and ggml-rpc-server.</summary>
    string EngineFolder { get; }
    /// <summary>The device name a helper serves (ggml-rpc-server -d); empty: the server's default.</summary>
    string RpcDevice { get; }
    IReadOnlyList<GpuAdapter> Adapters { get; }
    /// <summary>Compute capability or gfx architecture, for the Systeem page.</summary>
    string Capability { get; }
    /// <summary>The gpu-profiles.json key that applies: make/architecture/VRAM class.</summary>
    string ProfileKey { get; }
    GpuProfile Profile { get; }
    bool HasGpu { get; }
    /// <summary>Why this backend cannot run here, or null when it can.</summary>
    string? Unavailable { get; }
    /// <summary>Why the main engine (VRAM + RAM + SSD) cannot run on this backend, or null when it can.</summary>
    string? TieredUnavailable { get; }
    bool Experimental { get; }
    /// <summary>VRAM and RAM are the same chips.</summary>
    bool UnifiedMemory { get; }
    bool DrivesDisplay { get; }
    ulong VramTotal { get; }
    /// <summary>VRAM kept free beside the engine: the setting when given, else the profile's.</summary>
    ulong VramReserve(int settingMib);
    /// <summary>RAM left to Windows and other programs.</summary>
    ulong RamMargin(ulong ramTotal);
    /// <summary>The page file that keeps the commit limit out of the way of VRAM and the pinned RAM.</summary>
    ulong RequiredPageFile(ulong ramTotal);
    /// <summary>The numbers of the cards to run on, as the engine counts them.</summary>
    IReadOnlyList<int> GpuIndices { get; }
    /// <summary>Environment of every engine process on this backend.</summary>
    IReadOnlyDictionary<string, string> Environment { get; }
    /// <summary>Extra keys for the main engine's configuration.</summary>
    IReadOnlyDictionary<string, string> TieredConfig { get; }
    /// <summary>Folders with the runtime libraries of the main engine.</summary>
    IReadOnlyList<string> TieredLibDirs { get; }
    /// <summary>The Systeem page's text about this backend's speed-up settings.</summary>
    string Summary { get; }
}

/// <summary>A profile from gpu-profiles.json, merged from general to specific.</summary>
public sealed record GpuProfile(string Key, string Backend, string EngineFolder, bool Tiered, bool Experimental, bool Measured, bool Unified,
                                int VramReserveMib, int VramReserveDisplayMib, double RamMarginGib, double PageFileExtraGib, string Prefill,
                                int MaxGpus, IReadOnlyDictionary<string, string> Env);

/// <summary>Reads gpu-profiles.json (embedded in Core).</summary>
public static class GpuProfiles
{
    /// <summary>The backends a user can pick under Settings: label, id.</summary>
    public static readonly (string Label, string Id)[] Choices = [("CUDA", "cuda"), ("HIP", "hip"), ("Vulkan", "vulkan"), ("CPU", "cpu")];

    /// <summary>Test flag: a PC with a card that runs the main engine.</summary>
    public static IGpuBackend SimulatedMainEngineCard(int vramGb) => FakeGpuBackend.Nvidia(vramGb);

    private static readonly Lazy<JsonObject> Table = new(() =>
    {
        using var s = typeof(Paths).Assembly.GetManifestResourceStream("Resources.gpu-profiles.json")
            ?? throw new InvalidOperationException("gpu-profiles.json is not in the app");
        return (JsonObject)JsonNode.Parse(s)!["profiles"]!;
    });

    public static IEnumerable<string> Keys => Table.Value.Select(k => k.Key);

    /// <summary>The class of a card: 8, 12, 16, 24 or 32+ GB.</summary>
    public static string VramClass(ulong vram)
    {
        double gib = vram / (double)(1UL << 30);
        return gib <= 8.5 ? "8" : gib <= 12.5 ? "12" : gib <= 16.5 ? "16" : gib <= 24.5 ? "24" : "32+";
    }

    public static GpuProfile For(string make, string architecture, ulong vram)
    {
        string cls = VramClass(vram);
        var merged = new JsonObject();
        var env = new Dictionary<string, string>();
        foreach (var key in new[] { $"{make}/*/*", $"{make}/*/{cls}", $"{make}/{architecture}/*", $"{make}/{architecture}/{cls}" })
        {
            if (Table.Value[key] is not JsonObject o) continue;
            foreach (var kv in o)
            {
                if (kv.Key == "env")
                    foreach (var e in (JsonObject)kv.Value!) env[e.Key] = e.Value!.GetValue<string>();
                else
                    merged[kv.Key] = kv.Value?.DeepClone();
            }
        }
        T Get<T>(string name, T fallback) => merged[name] is JsonNode n ? n.GetValue<T>() : fallback;
        return new GpuProfile($"{make}/{architecture}/{cls}", Get("backend", "cpu"), Get("engineFolder", "cpu"), Get("tiered", false),
                              Get("experimental", false), Get("measured", false), Get("unified", false),
                              Get("vramReserveMib", 0), Get("vramReserveDisplayMib", 0), Get("ramMarginGib", 6.0), Get("pageFileExtraGib", 9.0),
                              Get("prefill", "auto"), Get("maxGpus", 0), env);
    }
}

/// <summary>What the backends share; each make only says what differs.</summary>
public abstract class GpuBackendBase : IGpuBackend
{
    protected GpuBackendBase(IReadOnlyList<GpuAdapter> adapters, GpuProfile profile, string capability)
    {
        Profile = profile;
        Capability = capability;
        Adapters = profile.MaxGpus > 0 ? adapters.Take(profile.MaxGpus).ToList() : adapters;
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public virtual string RpcDevice => "";
    public string EngineFolder => Profile.EngineFolder;
    public IReadOnlyList<GpuAdapter> Adapters { get; }
    public string Capability { get; }
    public GpuProfile Profile { get; }
    public string ProfileKey => Profile.Key;
    public bool HasGpu => Adapters.Count > 0;
    public abstract string? Unavailable { get; }
    public abstract string? TieredUnavailable { get; }
    public bool Experimental => Profile.Experimental;
    public bool UnifiedMemory => Profile.Unified;
    public bool DrivesDisplay => Adapters.Any(a => a.DrivesDisplay);
    public ulong VramTotal => Adapters.Aggregate(0UL, (n, a) => n + a.VramBytes);
    public IReadOnlyList<int> GpuIndices => Enumerable.Range(0, Adapters.Count).ToList();
    public virtual IReadOnlyDictionary<string, string> Environment => Profile.Env;
    public virtual IReadOnlyDictionary<string, string> TieredConfig => new Dictionary<string, string>();
    public virtual IReadOnlyList<string> TieredLibDirs => new[] { Path.Combine(Paths.EngineRoot, "tiered", "bin") };

    public ulong VramReserve(int settingMib)
    {
        if (settingMib >= 0) return (ulong)settingMib << 20;
        ulong total = 0;
        foreach (var a in Adapters) total += (ulong)(a.DrivesDisplay ? Profile.VramReserveDisplayMib : Profile.VramReserveMib) << 20;
        return total;
    }

    public virtual ulong RamMargin(ulong ramTotal) => (ulong)(Profile.RamMarginGib * (1UL << 30));

    public virtual ulong RequiredPageFile(ulong ramTotal) =>
        Profile.PageFileExtraGib <= 0 ? 0 : ((ulong)Math.Ceiling(VramTotal / (double)(1UL << 30)) + (ulong)Profile.PageFileExtraGib) << 30;

    public virtual string Summary => Profile.Measured ? DisplayName : DisplayName + " · " + Lang.L("startwaarden, nog niet gemeten");

    protected static bool EngineFileExists(string folder, string file) => File.Exists(Path.Combine(Paths.EngineRoot, folder, file));
}
