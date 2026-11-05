using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>What WinAIMagic needs on this PC, checked at every start. What the app can put right itself it does; what needs the
/// user (a permission, a download of 100+ GB, a restart) it offers with one button.</summary>
public sealed class SystemCheck
{
    public enum CheckState { Ok, Fixed, Todo, Optional, Failed }
    public enum CheckAction { OpenModels, OpenCluster, SetPageFile, OpenDefender, InstallDeveloperTools }

    public sealed record Item(string Id, string Title, string Detail, CheckState State, string? ActionLabel = null, CheckAction? Action = null);

    public IReadOnlyList<Item> Items { get; private set; } = Array.Empty<Item>();
    public bool Running { get; private set; }
    public event Action? Changed;

    /// <summary>Something keeps the app from working: the start opens the check.</summary>
    public bool NeedsAttention => Items.Any(i => i.State == CheckState.Failed);

    public async Task Run(EngineHost host)
    {
        Running = true;
        Changed?.Invoke();
        host.RefreshSystem();
        host.Store.Refresh();
        var engine = await Task.Run(() => EngineItem(host.Backend));
        var list = new List<Item> { AppItem(), HardwareItem(host), engine, DownloadItem() };
        list.AddRange(ModelItems(host));
        list.Add(PageFileItem(host.System.PageFile, host.Backend.RequiredPageFile(host.System.RamTotal)));
        list.Add(NvmeItem(DriveReport.For(Paths.ModelsRoot)));
        list.Add(DefenderItem(engine));
        list.Add(await Task.Run(DeveloperToolsItem));
        Items = list;
        Running = false;
        Changed?.Invoke();
    }

    // MARK: - the checks

    /// <summary>Which copy runs: an older one elsewhere on the PC is easily started by accident.</summary>
    internal static Item AppItem()
    {
        var version = Paths.AppVersion;
        return new Item("app", "WinAIMagic", L($"Versie {version}") + " · " + Paths.AppRoot, CheckState.Ok);
    }

    internal static Item HardwareItem(EngineHost host)
    {
        var s = host.System;
        var b = host.Backend;
        string title = L("Deze pc");
        string memory = $"{s.RamTotal.Gib()} GB RAM · {s.WindowsVersion}";
        if (!b.HasGpu) return new Item("hardware", title, L("Geen bruikbare videokaart: de engine rekent op de processor.") + " " + s.Cpu + " · " + memory, CheckState.Ok);
        var cards = string.Join(" + ", b.Adapters.Select(a => $"{a.Name} ({a.VramBytes.Gib()} GB VRAM, driver {a.Driver})"));
        return new Item("hardware", title, $"{cards} · {memory} · {host.Choice.Reason}", b.Experimental ? CheckState.Optional : CheckState.Ok);
    }

    /// <summary>The binaries of the chosen backend: do they start? Files with the Mark of the Web lose it and get another try.</summary>
    internal static Item EngineItem(IGpuBackend backend)
    {
        string title = L("Engine");
        var bin = LlamaEngine.Binary(backend);
        if (!File.Exists(bin))
            return new Item("engine", title, L("De engine ontbreekt in de app. Installeer WinAIMagic opnieuw."), CheckState.Failed);
        var first = Version(bin);
        if (first.Text != null)
        {
            // the main engine lives in the bundled Python: does that start too?
            if (backend.TieredUnavailable == null && !PythonStarts())
                return new Item("engine", title, L("De Python van de engine start niet.") + " " + L("Installeer WinAIMagic opnieuw."), CheckState.Failed);
            return new Item("engine", title, first.Text, CheckState.Ok);
        }
        if (first.Blocked)
            return new Item("engine", title, L("Windows Defender houdt de engine tegen."), CheckState.Failed, L("Open Windows-beveiliging"), CheckAction.OpenDefender);
        // a downloaded copy keeps the Mark of the Web, and Windows may hold the engine back: the user opened the app, so the mark
        // comes off the engine folder, and the engine gets another try
        if (RemoveMarkOfTheWeb(Paths.EngineRoot) > 0 && Version(bin).Text is { } text)
            return new Item("engine", title, L("Windows hield de engine tegen; dat is opgeheven.") + " " + text, CheckState.Fixed);
        return new Item("engine", title, L("De engine start niet op deze pc.") + " " + L("Installeer WinAIMagic opnieuw."), CheckState.Failed);
    }

    /// <summary>"llama.cpp &lt;version&gt;" when the engine runs, null when it does not start; `Blocked`: Defender stopped it.</summary>
    private static (string? Text, bool Blocked) Version(string bin)
    {
        try
        {
            var r = ChildProcess.RunToEnd(bin, new[] { "--version" }, TimeSpan.FromSeconds(20));
            if (r is not { Status: 0 }) return (null, false);
            var line = r.Value.Output.Split('\n').FirstOrDefault(l => l.StartsWith("version")) ?? "";
            return ("llama.cpp " + line.Replace("version: ", "").Trim(), false);
        }
        catch (Win32Exception e) { return (null, e.NativeErrorCode is 225 or 226); }
    }

    private static bool PythonStarts()
    {
        try { return ChildProcess.RunToEnd(PackBuilder.PythonExe, new[] { "--version" }, TimeSpan.FromSeconds(20)) is { Status: 0 }; }
        catch (Win32Exception) { return false; }
    }

    /// <summary>Removes the zone stream from the executables and libraries under `folder`; how many had one.</summary>
    internal static int RemoveMarkOfTheWeb(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        int removed = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            var stream = file + ":Zone.Identifier";
            try
            {
                if (File.Exists(stream)) { File.Delete(stream); removed++; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return removed;
    }

    /// <summary>The bundled Python and the pack tools: everything for downloading and packing is in the app itself.</summary>
    internal static Item DownloadItem()
    {
        string title = L("Downloaden");
        var missing = new List<string>();
        if (!File.Exists(PackBuilder.PythonExe)) missing.Add("python.exe");
        if (!File.Exists(Path.Combine(PackBuilder.TieredRoot, "tools", "iq_pack.py"))) missing.Add("iq_pack.py");
        if (missing.Count > 0)
            return new Item("download", title, L($"Ontbreekt: {string.Join(", ", missing)}. Installeer WinAIMagic opnieuw."), CheckState.Failed);
        return new Item("download", title, L("Alles aanwezig om modellen te downloaden en te controleren."), CheckState.Ok);
    }

    internal static List<Item> ModelItems(EngineHost host)
    {
        var models = host.Store;
        var s = host.System;
        if (ModelStore.FolderMissing)
            return new List<Item> { new("model", L("Model"), ModelStore.FolderMissingMessage, CheckState.Failed, L("Naar Modellen"), CheckAction.OpenModels) };
        var ready = models.Catalog.Models.Where(m => models.StatusOf(m.Id).IsComplete).ToList();
        ulong free = s.FreeDisk;
        if (ready.Count > 0)
            return new List<Item>
            {
                new("model", L("Model"), string.Join(", ", ready.Select(m => m.Name)), CheckState.Ok),
                new("disk", L("Schijfruimte"), L($"{free.GbShort()} vrij"), CheckState.Ok),
            };
        // nothing fits in VRAM + RAM: the smallest model still streams, and this PC can also be a helper in a cluster
        var best = host.Recommended;
        if (best == null)
            return new List<Item>
            {
                new("model", L("Model"), L($"Op deze pc met {s.RamTotal.Gib()} GB RAM streamen alle modellen deels van de SSD. Dat werkt gewoon. De pc kan ook helper zijn in een cluster; daarvoor is geen model nodig."),
                    CheckState.Optional, L("Naar Cluster"), CheckAction.OpenCluster),
                new("disk", L("Schijfruimte"), L($"{free.GbShort()} vrij"), CheckState.Ok),
            };
        bool downloading = models.Downloading.Contains(best.Id);
        string text = downloading ? L($"{best.Name} wordt gedownload.")
            : host.FitOf(best) == MemoryPlanner.Fit.Streams
                ? s.HasGpu
                    ? L($"Nog geen model. Aanbevolen voor deze pc met {s.VramTotal.Gib()} GB VRAM en {s.RamTotal.Gib()} GB RAM: {best.Name} ({best.TotalSize.GbShort()}). Het streamt van de SSD; op zo weinig geheugen is dat nog niet gemeten en het kan traag zijn.")
                    : L($"Nog geen model. Aanbevolen voor deze pc met {s.RamTotal.Gib()} GB RAM: {best.Name} ({best.TotalSize.GbShort()}). Het streamt van de SSD; op zo weinig geheugen is dat nog niet gemeten en het kan traag zijn.")
            : L($"Nog geen model. Aanbevolen voor deze pc met {s.VramTotal.Gib()} GB VRAM en {s.RamTotal.Gib()} GB RAM: {best.Name} ({best.TotalSize.GbShort()}), dat helemaal in VRAM en RAM past.")
              + (models.Catalog.Models.Any(m => host.FitOf(m) == MemoryPlanner.Fit.Streams) ? " " + L("De grotere modellen kun je ook gebruiken: die streamen van de SSD.") : "");
        var model = new Item("model", L("Model"), text, CheckState.Todo, downloading ? null : L("Naar Modellen"), downloading ? null : CheckAction.OpenModels);
        bool enough = free > best.TotalSize + (5UL << 30);
        var disk = new Item("disk", L("Schijfruimte"),
            enough ? L($"{free.GbShort()} vrij, genoeg voor {best.ShortName}.")
                   : L($"{free.GbShort()} vrij; {best.ShortName} heeft {best.TotalSize.GbShort()} nodig. Maak ruimte vrij of kies onder Modellen een map op een andere schijf."),
            enough ? CheckState.Ok : CheckState.Failed);
        return new List<Item> { model, disk };
    }

    /// <summary>VRAM and the pinned tier both count for commit; "system managed" can stay at 8 GB and then the engine pins too little.</summary>
    internal static Item PageFileItem(ulong have, ulong required)
    {
        string title = L("Wisselbestand");
        if (required == 0 || have >= required)
            return new Item("pagefile", title, L($"{have.Gib()} GB, genoeg voor het vastzetten van het model."), CheckState.Ok);
        return new Item("pagefile", title,
            L($"{have.Gib()} GB; de engine heeft minstens {required.Gib()} GB nodig (VRAM + 9 GB), anders zet hij te weinig vast en wordt hij veel trager. Vraagt om toestemming en werkt na een herstart."),
            CheckState.Todo, L("Stel in"), CheckAction.SetPageFile);
    }

    /// <summary>A slow drive gets a warning, no block.</summary>
    internal static Item NvmeItem(DriveReport d)
    {
        string title = L("Modelschijf");
        if (!d.Ready) return new Item("nvme", title, L($"{Paths.ModelsRoot} is niet beschikbaar."), CheckState.Failed);
        if (d.IsNvme) return new Item("nvme", title, L($"{d.Root} is een NVMe-schijf, {d.FreeBytes.GbShort()} vrij."), CheckState.Ok);
        return new Item("nvme", title, L($"{d.Root} is geen NVMe-schijf. Modellen die van de SSD streamen worden hierop trager. Kies onder Modellen een map op een NVMe-schijf als je die hebt."), CheckState.Optional);
    }

    /// <summary>Defender can scan every read of a model file; whether it does is only visible to an administrator, so the row offers
    /// the settings. A blocked engine is reported by the engine row and repeated here.</summary>
    internal static Item DefenderItem(Item engine)
    {
        string title = "Windows Defender";
        if (engine.Action == CheckAction.OpenDefender)
            return new Item("defender", title, L("Defender blokkeert de engine. Sta hem toe in Windows-beveiliging."), CheckState.Failed, L("Open Windows-beveiliging"), CheckAction.OpenDefender);
        return new Item("defender", title, L("Blokkeert de engine niet. Je kunt de modelmap uitsluiten van scannen, dat scheelt schijfwerk bij modellen die streamen."),
                        CheckState.Optional, L("Open Windows-beveiliging"), CheckAction.OpenDefender);
    }

    /// <summary>Python and git, for what the model does in the work folder; the app itself brings its own.</summary>
    internal static Item DeveloperToolsItem()
    {
        string title = L("Ontwikkeltools");
        if (HasDeveloperTools) return new Item("devtools", title, L("Aanwezig: het model kan in de werkmap Python en git gebruiken."), CheckState.Ok);
        return new Item("devtools", title, L("Niet nodig voor de app zelf. Wel als het model in de werkmap Python of git wil gebruiken."),
                        CheckState.Optional, L("Installeer"), CheckAction.InstallDeveloperTools);
    }

    /// <summary>Are Python and git here? The chat's system prompt says so when they are not.</summary>
    public static bool HasDeveloperTools => OnPath("git.exe") && OnPath("python.exe") && !IsStoreStub("python.exe");

    private static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Any(d => d.Length > 0 && File.Exists(Path.Combine(d, exe)));

    /// <summary>Windows' python.exe under WindowsApps only offers the Store.</summary>
    private static bool IsStoreStub(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(d => d.Length > 0 && File.Exists(Path.Combine(d, exe)))
            .All(d => d.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase));

    // MARK: - actions

    public void Perform(CheckAction action, EngineHost host, Action<CheckAction> open)
    {
        switch (action)
        {
            case CheckAction.OpenModels:
                if (host.Recommended is { } best) host.SelectedModelId = best.Id;
                open(action);
                break;
            case CheckAction.OpenCluster:
                open(action);
                break;
            case CheckAction.SetPageFile:
                PageFileProblem = null;
                switch (SetPageFile(host.Backend.RequiredPageFile(host.System.RamTotal) >> 20, Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\"))
                {
                    case PageFileResult.Done: RestartNeeded = true; break;
                    case PageFileResult.Failed: PageFileProblem = L("Het wisselbestand instellen is mislukt. Stel het in via Systeem > Info > Geavanceerde systeeminstellingen > Prestaties > Virtueel geheugen."); break;
                }
                _ = Run(host);
                break;
            case CheckAction.OpenDefender:
                Process.Start(new ProcessStartInfo("windowsdefender://threatsettings") { UseShellExecute = true });
                break;
            case CheckAction.InstallDeveloperTools:
                // winget's own window; the check notices the tools at its next run
                Process.Start(new ProcessStartInfo("winget.exe", "install --id Git.Git --id Python.Python.3.12 --accept-source-agreements") { UseShellExecute = true });
                break;
        }
    }

    /// <summary>The page file was changed: the app tells the user that it counts after a restart.</summary>
    public bool RestartNeeded { get; private set; }

    /// <summary>Windows would not change it, although the user agreed; null otherwise.</summary>
    public string? PageFileProblem { get; private set; }

    public enum PageFileResult { Done, Declined, Failed }

    /// <summary>A fixed page file on `drive`. Windows' UAC prompt asks the user.</summary>
    public static PageFileResult SetPageFile(ulong sizeMb, string drive)
    {
        var bytes = Encoding.Unicode.GetBytes(PageFileScript(sizeMb, drive));
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(bytes))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit();
            return p?.ExitCode == 0 ? PageFileResult.Done : PageFileResult.Failed;
        }
        catch (Win32Exception) { return PageFileResult.Declined; }   // the prompt was declined
    }

    internal static string PageFileScript(ulong sizeMb, string drive)
    {
        string letter = drive[..1];
        return $$"""
            $ErrorActionPreference = 'Stop'
            Set-CimInstance -InputObject (Get-CimInstance Win32_ComputerSystem) -Property @{ AutomaticManagedPagefile = $false }
            $pf = Get-CimInstance Win32_PageFileSetting -Filter "Name like '{{letter}}:%'"
            if (-not $pf) { $pf = New-CimInstance -ClassName Win32_PageFileSetting -Property @{ Name = '{{letter}}:\pagefile.sys' } }
            Set-CimInstance -InputObject $pf -Property @{ InitialSize = [uint32]{{sizeMb}}; MaximumSize = [uint32]{{sizeMb}} }
            """;
    }
}
