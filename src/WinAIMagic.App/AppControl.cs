using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using WinAIMagic.App.Views;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App;

/// <summary>The app as the model sees it: its state for app_status, and the actions of app_action, done the way the buttons do them.
/// Actions that would stop the engine this conversation runs on wait until the answer is done.</summary>
public sealed class AppControl : IAppControl
{
    private readonly AppState app;
    private readonly List<Func<Task>> afterAnswer = [];

    /// <summary>The settings the model may change. Not: its own rights, API keys, the server address, the model folder, the cluster.</summary>
    private static readonly Dictionary<string, string[]?> Changeable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Language"] = ["nl", "en", "de", "fr"],
        ["ContextSize"] = ["32768", "65536", "131072", "262144", "524288", "1048576"],
        ["Vision"] = null, ["Thinking"] = null, ["WebSearch"] = null, ["Mtp"] = null, ["AutoStart"] = null, ["AnimatedBackground"] = null,
        ["WorkspaceTools"] = null, ["LearnFromChats"] = null, ["ImageImprove"] = null, ["Skills"] = null, ["SystemManagement"] = null,
        ["ReasoningEffort"] = ["low", "medium", "high", "max"],
        ["ThinkingBudget"] = ["0", "4000", "8000", "16000", "32000"],
        ["Temperature"] = null,
        ["SkipExperts"] = ["off", "light", "strong"],
        ["ImageSize"] = ["", "512x512", "768x768", "1024x1024", "1024x768", "768x1024"],
        ["SystemPrompt"] = null,
        // Instellingen → Telegram and Agent; pairing, the token and the allowed programs stay with the user
        ["FeatureTelegram"] = null, ["TelegramNotify"] = null,
        ["FeatureMail"] = null, ["FeatureBrowser"] = null, ["FeatureComputerUse"] = null, ["FeatureVoice"] = null, ["SpeakAnswers"] = null,
    };

    private static readonly Dictionary<string, PaneKind> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chat"] = PaneKind.Chat, ["live"] = PaneKind.Live, ["models"] = PaneKind.Models, ["modellen"] = PaneKind.Models,
        ["documents"] = PaneKind.Documents, ["documenten"] = PaneKind.Documents, ["images"] = PaneKind.Images, ["beeld"] = PaneKind.Images,
        ["compare"] = PaneKind.Compare, ["vergelijken"] = PaneKind.Compare, ["cluster"] = PaneKind.Cluster,
        ["system"] = PaneKind.System, ["systeem"] = PaneKind.System, ["help"] = PaneKind.Help, ["hulp"] = PaneKind.Help,
    };

    public AppControl(AppState app)
    {
        this.app = app;
        // what waited for the answer runs once the conversation is done
        app.Chat.StateChanged += () =>
        {
            if (app.Chat.Busy || afterAnswer.Count == 0) return;
            var todo = afterAnswer.ToList();
            afterAnswer.Clear();
            app.Ui.TryEnqueue(async () =>
            {
                foreach (var t in todo)
                    try { await t(); } catch (Exception e) { App.Log(e); }
            });
        };
    }

    private EngineHost Host => app.Host;
    private bool ChatEngineRuns => app.EngineState.IsActive;

    public IReadOnlyList<string> SettingNames => Changeable.Select(kv =>
    {
        var type = typeof(Settings).GetProperty(kv.Key)?.PropertyType;
        var values = kv.Value != null ? string.Join("/", kv.Value.Where(v => v.Length > 0)) : type == typeof(bool) ? "true/false" : type == typeof(double) ? "0-2" : "text";
        return $"{kv.Key} ({values})";
    }).ToList();

    // MARK: - status

    public string Status()
    {
        var h = Host;
        var s = h.System;
        var o = new StringBuilder();
        o.AppendLine($"WinAIMagic {Paths.AppVersion}{(Paths.Portable ? " (portable)" : "")}, language {Settings.Shared.Language}, on screen: {app.Pane.Kind}.");
        o.AppendLine($"This PC: {s.Cpu}; {(s.HasGpu ? $"{s.Gpu}, {s.VramTotal.Gib()} GB VRAM" : "no usable graphics card")}; {s.RamTotal.Gib()} GB RAM ({s.RamAvailable.Gib()} GB free); {s.FreeDisk.GbShort()} free on the model drive; {s.WindowsVersion}.");
        o.AppendLine($"Engine backend: {h.Choice.Reason}");
        var engine = h.Engine;
        o.AppendLine($"Main chat engine: {app.EngineState}{(engine?.Model is { } running ? $", running {running.Id}" : "")}; heavy model chosen: {h.SelectedModelId}{(h.CanStart ? "" : $" (cannot start now: {h.WhyNotStartable})")}.");
        var fast = app.FastEngine;
        o.AppendLine($"Fast model (Snel): {app.DocumentEngines.FastChoice?.Id ?? "none"}{(fast is { } f && f.State.IsActive ? ", running on the second engine" : "")}. Image model: {h.ImageModel?.Id ?? "none"}{(h.Images.Busy ? ", making an image now" : "")}.");
        o.AppendLine();
        o.AppendLine("Models (id: name, size, on this PC, downloaded):");
        var recommended = h.Recommended?.Id;
        foreach (var m in h.Store.Catalog.Models.Concat(h.Store.Catalog.Images))
        {
            var st = h.Store.StatusOf(m.Id);
            bool images = h.Store.Catalog.Images.Contains(m);
            var fit = images ? "makes images" : h.FitOf(m) == MemoryPlanner.Fit.Fits ? "fits in VRAM + RAM" : "streams from the SSD";
            var state = st.IsComplete ? (st.UpdateAvailable ? "downloaded, an update is available" : "downloaded")
                : h.Store.Downloading.Contains(m.Id) ? $"downloading, {st.Have.GbShort()} of {st.Expected.GbShort()}"
                : st.Have > 0 ? $"partly downloaded ({st.Have.GbShort()} of {st.Expected.GbShort()})" : "not downloaded";
            o.AppendLine($"- {m.Id}: {m.Name}, {m.TotalSize.GbShort()}, {fit}, {state}{(m.Id == recommended ? ", RECOMMENDED for this PC" : "")}{(m.License != null ? ", licence shown before download" : "")}");
        }
        o.AppendLine();
        o.AppendLine("Settings the model may change with set_setting (name = value):");
        foreach (var (name, options) in Changeable)
        {
            var value = typeof(Settings).GetProperty(name)?.GetValue(Settings.Shared);
            var shown = value is string text && name == "SystemPrompt" ? (text.Length == 0 ? "(empty)" : Cut(text, 80)) : Convert.ToString(value, CultureInfo.InvariantCulture);
            o.AppendLine($"- {name} = {shown}{(options != null ? $" (one of: {string.Join(", ", options.Select(x => x.Length == 0 ? "\"\" for automatic" : x))})" : "")}");
        }
        o.AppendLine();
        o.AppendLine();
        var c = h.Cluster;
        o.AppendLine($"Cluster: this PC is {(c.IsHelperRole ? "a helper" : "the main PC")}; local network {(Settings.Shared.ClusterAllowLan ? "allowed" : "not allowed (direct cable only)")}; addresses: {string.Join(", ", ClusterController.Addresses().Select(x => $"{x.Interface} {x.Ip}{(x.Direct ? " (direct cable)" : "")}"))}.");
        if (c.IsHelperRole) o.AppendLine(c.HelperRunning ? $"Helper running on {c.HelperAddress}." : "Helper not running (start_helper).");
        else
        {
            o.AppendLine(h.ClusterApplies ? "The chosen heavy model can use helpers (second engine)." : $"The chosen heavy model runs on the main engine, which cannot use helpers; models that can: {string.Join(", ", h.ClusterModels.Select(m => m.Id))}.");
            var helpers = c.Helpers;
            if (helpers.Count == 0) o.AppendLine("No helpers found on the network. A helper is another PC or Mac with WinAIMagic or MacAIMagic in the role helper and its helper started.");
            foreach (var x in helpers)
                o.AppendLine($"- helper {x.Id}: {x.Name}, {x.Chip}, {x.GpuGb:0.#} GB for the model{(x.MemoryGb > 0 ? $", {x.MemoryGb:0} GB memory" : "")}, {(x.ViaDirectLink ? "direct cable" : "network")}{(x.Manual ? ", added by hand" : "")}{(x.Unreachable ? ", NOT REACHABLE on its port" : "")}{(c.Selected.Contains(x.Id) ? ", in use" : ", not in use")}");
        }
        if (c.Transfer is { } t) o.AppendLine($"Sending model parts to the helpers: {t.Sent / 1e9:0.0} of about {t.Expected / 1e9:0} GB, {t.BytesPerSecond / 1e6:0} MB/s.");
        if (c.Problem is { } problem) o.AppendLine("Cluster problem: " + problem);
        o.AppendLine($"Rights (only the user changes these, in Settings): work folder read {Settings.Shared.RightRead}, write {Settings.Shared.RightWrite}, run {Settings.Shared.RightRun}; system read {Settings.Shared.RightSystemRead}, change {Settings.Shared.RightSystemChange}.");
        o.AppendLine($"Model folder: {Paths.ModelsRoot}. Own skills: {Paths.UserSkills}. Images: {Paths.PicturesFolder}. Documents: {Paths.DocumentsRoot}.");
        o.Append($"Local API: http://{Settings.Shared.Host}:{Settings.Shared.Port}/v1 (OpenAI) and /v1/messages (Anthropic){(Settings.Shared.ApiKey.Length > 0 ? ", with an API key" : "")}.");
        return o.ToString();
    }

    // MARK: - actions

    private static string Arg(JsonObject a, string key) => (a[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : a[key]?.ToString() ?? "").Trim();

    private ModelInfo? Model(JsonObject a) => Host.Store.Catalog.Model(Arg(a, "model"));

    private string NoModel(JsonObject a) => $"There is no model \"{Arg(a, "model")}\". Ids: {string.Join(", ", Host.Store.Catalog.Models.Concat(Host.Store.Catalog.Images).Select(m => m.Id))}.";

    public AppActionPlan Plan(string action, JsonObject a)
    {
        var store = Host.Store;
        switch (action)
        {
            case "open_page":
                return Pages.ContainsKey(Arg(a, "page")) ? new(ReadOnly: true) : new(Error: $"Unknown page. Pages: {string.Join(", ", Pages.Keys.Where(k => k.All(char.IsAscii)).Take(9))}.");
            case "open_help":
                return new(ReadOnly: true);
            case "new_conversation":
                return new(ReadOnly: true, AfterAnswer: true);
            case "download_model":
                {
                    if (Model(a) is not { } m) return new(Error: NoModel(a));
                    var st = store.StatusOf(m.Id);
                    if (st.IsComplete && !st.UpdateAvailable) return new(Error: $"{m.Name} is downloaded already.");
                    if (store.Downloading.Contains(m.Id)) return new(Error: $"{m.Name} is downloading already.");
                    if (m.License != null) return new(Error: $"{m.Name} has a licence the user must read and accept first: ask them to press Download on its card under Models (open_page models).");
                    ulong left = st.UpdateAvailable ? st.UpdateBytes : st.Expected - Math.Min(st.Have, st.Expected);
                    return new(L($"{m.Name} downloaden ({left.GbShort()})"), L($"Er is {Host.System.FreeDisk.GbShort()} vrij op de schijf van de modelmap."));
                }
            case "pause_download":
                return Model(a) is { } pm && store.Downloading.Contains(pm.Id) ? new(L($"Download van {pm.Name} pauzeren")) : new(Error: "That model is not downloading.");
            case "check_update":
                return Model(a) is { } c ? (store.StatusOf(c.Id).IsComplete ? new(ReadOnly: true) : new(Error: $"{c.Name} is not downloaded.")) : new(Error: NoModel(a));
            case "delete_model":
                {
                    if (Model(a) is not { } m) return new(Error: NoModel(a));
                    if (InUse(m)) return new(Error: $"{m.Name} is in use. Stop it first (stop_engine, after this answer).");
                    var have = store.StatusOf(m.Id).Have;
                    return new(L($"{m.Name} verwijderen"), L($"De map van dit model met alle bestanden ({have.GbShort()}) wordt van de schijf verwijderd: {Paths.ModelDir(m.Id)}. Je kunt het model later opnieuw downloaden."), Ask: true);
                }
            case "choose_model":
                {
                    if (Model(a) is not { } m) return new(Error: NoModel(a));
                    var role = Arg(a, "role").ToLowerInvariant();
                    bool images = store.Catalog.Images.Contains(m);
                    if (role is "images" or "image" or "afbeeldingen") return images ? new(L($"{m.Name} kiezen voor afbeeldingen")) : new(Error: $"{m.Name} does not make images.");
                    if (images) return new(Error: $"{m.Name} makes images: choose it with role images.");
                    if (role is "fast" or "snel") return m.Id == Host.SelectedModelId ? new(Error: "The heavy model cannot be the fast one too.") : new(L($"{m.Name} kiezen als snel model"));
                    if (role is "heavy" or "zwaar" or "")
                    {
                        if (!store.StatusOf(m.Id).IsComplete) return new(Error: $"{m.Name} is not downloaded yet (download_model first).");
                        bool restart = ChatEngineRuns && Host.Engine?.Model?.Id != m.Id;
                        return new(L($"{m.Name} kiezen als zwaar model"), restart ? L("De engine stopt en start daarna met dit model.") : null, AfterAnswer: restart);
                    }
                    return new(Error: "role is heavy, fast or images.");
                }
            case "start_engine":
                if (ChatEngineRuns) return new(Error: "The engine runs already.");
                return Host.CanStart ? new(L($"Engine starten met {Host.SelectedModel?.Name}")) : new(Error: Host.WhyNotStartable ?? "The engine cannot start now.");
            case "stop_engine":
                return ChatEngineRuns ? new(L("Engine stoppen"), L("Daarna antwoordt dit gesprek niet meer tot de engine weer draait."), AfterAnswer: true) : new(Error: "The engine is not running.");
            case "set_setting":
                {
                    var name = Arg(a, "setting");
                    var value = Arg(a, "value");
                    if (!Changeable.TryGetValue(name, out var options))
                        return new(Error: $"\"{name}\" cannot be changed from the chat. Changeable: {string.Join(", ", Changeable.Keys)}. Rights, keys, the server and the model folder only the user changes, in Settings.");
                    var p = typeof(Settings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)!;
                    if (options != null && !options.Contains(value, StringComparer.OrdinalIgnoreCase)) return new(Error: $"{name} is one of: {string.Join(", ", options)}.");
                    if (Parse(p.PropertyType, value) is null) return new(Error: $"\"{value}\" is not a value for {name}.");
                    var now = Convert.ToString(p.GetValue(Settings.Shared), CultureInfo.InvariantCulture);
                    return new(L($"Instelling {p.Name}: {now} → {value}"));
                }
            case "find_helpers":
                return new(ReadOnly: true);
            case "add_helper":
                return Arg(a, "address").Length == 0 ? new(Error: "Give the address (host or host:port).") : new(L($"Helper toevoegen: {Arg(a, "address")}"));
            case "use_helper":
                {
                    var helper = Host.Cluster.Helpers.FirstOrDefault(x => x.Id.Equals(Arg(a, "helper"), StringComparison.OrdinalIgnoreCase) || x.Name.Equals(Arg(a, "helper"), StringComparison.OrdinalIgnoreCase));
                    if (helper == null) return new(Error: $"No helper \"{Arg(a, "helper")}\". Known: {string.Join(", ", Host.Cluster.Helpers.Select(x => x.Id))}.");
                    bool on = Arg(a, "value") is not ("false" or "0" or "off");
                    return new(on ? L($"Helper gebruiken: {helper.Name}") : L($"Helper niet meer gebruiken: {helper.Name}"), L("Dit geldt bij de volgende start van de engine."));
                }
            case "cluster_role":
                {
                    var role = Arg(a, "value").ToLowerInvariant();
                    if (role is not ("main" or "helper")) return new(Error: "value is main or helper.");
                    if (role == "helper")
                    {
                        bool direct = ClusterController.Addresses().Any(x => x.Direct);
                        var detail = L("Deze pc leent dan zijn videokaart uit aan een hoofd-pc: de engine stopt, de helper start, en dit gesprek antwoordt niet meer tot de pc weer hoofd-pc is.")
                            + (direct || Settings.Shared.ClusterAllowLan ? "" : " " + L("Er is geen directe kabel: sta ook het lokale netwerk toe, anders kan geen hoofd-pc de helper bereiken."));
                        return new(L("Deze pc wordt helper in het cluster"), detail, AfterAnswer: ChatEngineRuns);
                    }
                    return new(L("Deze pc wordt weer hoofd-pc"));
                }
            case "allow_lan":
                {
                    bool on = Arg(a, "value") is not ("false" or "0" or "off");
                    return on ? new(L("Lokaal netwerk toestaan voor het cluster"), L("Let op: de helper heeft geen wachtwoord. Gebruik dit alleen op een netwerk dat je vertrouwt, nooit op een openbaar netwerk."), Ask: true)
                              : new(L("Alleen een directe kabel voor het cluster"));
                }
            case "start_helper":
                if (Host.Cluster.HelperRunning) return new(Error: "The helper runs already.");
                return !Host.Cluster.IsHelperRole ? new(Error: "This PC is the main PC: cluster_role helper first.") : new(L("Helper starten"), AfterAnswer: ChatEngineRuns);
            case "stop_helper":
                return Host.Cluster.HelperRunning ? new(L("Helper stoppen")) : new(Error: "The helper is not running.");
            case "make_image":
                if (Arg(a, "prompt").Length == 0) return new(Error: "Give a prompt.");
                if (Host.ImageModel is not { } im || !store.StatusOf(im.Id).IsComplete) return new(Error: "No image model is downloaded (download_model flux-2-klein-4b first).");
                return new(L($"Afbeelding maken: {Cut(Arg(a, "prompt"), 120)}"), ChatEngineRuns ? L("De chatengine stopt eerst, zodat het afbeeldingsmodel de videokaart heeft.") : null, AfterAnswer: ChatEngineRuns);
            default:
                return new(Error: "Unknown action.");
        }
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private bool InUse(ModelInfo m) =>
        Host.Store.Catalog.Images.Contains(m) ? Host.Images.Busy
        : Host.Engine?.Model?.Id == m.Id && app.EngineState.IsActive || app.FastEngine is { } f && f.State.IsActive && f.Model?.Id == m.Id;

    private static object? Parse(Type t, string v) =>
        t == typeof(bool) ? (bool.TryParse(v, out var b) ? b : v is "1" or "aan" or "on" ? true : v is "0" or "uit" or "off" ? false : null)
        : t == typeof(int) ? (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null)
        : t == typeof(double) ? (double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d is >= 0 and <= 2 ? d : null)
        : t == typeof(string) ? v : null;

    public async Task<string> Run(string action, JsonObject a)
    {
        var plan = Plan(action, a);
        if (plan.Error != null) return "Error: " + plan.Error;
        var store = Host.Store;
        if (plan.AfterAnswer)
        {
            afterAnswer.Add(() => Do(action, a));
            return $"Approved. This happens as soon as your answer is done ({action}). Finish your answer now and tell the user what will happen; do not call more tools for it.";
        }
        return await Do(action, a);
    }

    private async Task<string> Do(string action, JsonObject a)
    {
        var store = Host.Store;
        switch (action)
        {
            case "open_page":
                var kind = Pages[Arg(a, "page")];
                app.Pane = kind == PaneKind.Chat ? new Pane(PaneKind.Chat, app.Store.Selection) : new Pane(kind);
                return $"The {kind} screen is open.";
            case "open_help":
                app.OpenHelp(HelpCenter.Search(Arg(a, "topic"), HelpCenter.Topics()).FirstOrDefault()?.Id ?? "start");
                return $"The help is open at {app.HelpTopic}.";
            case "new_conversation":
                app.NewConversation();
                return "A new conversation is open.";
            case "download_model":
                {
                    var m = Model(a)!;
                    store.StartDownload(m.Id);
                    return $"The download of {m.Name} started; it shows on its card under Models and goes on in the background (app_status shows how far).";
                }
            case "pause_download":
                store.StopDownload(Model(a)!.Id);
                return "The download is paused; download_model resumes it.";
            case "check_update":
                {
                    var m = Model(a)!;
                    await store.CheckForUpdate(m.Id);
                    return store.CheckResult.TryGetValue(m.Id, out var r) ? r.Text : "Checked.";
                }
            case "delete_model":
                {
                    var m = Model(a)!;
                    var error = await Task.Run(() => store.DeleteModel(m.Id));
                    return error ?? $"{m.Name} is deleted from the disk.";
                }
            case "choose_model":
                {
                    var m = Model(a)!;
                    var role = Arg(a, "role").ToLowerInvariant();
                    if (store.Catalog.Images.Contains(m)) { Settings.Shared.ImageModel = m.Id; app.Notify(); return $"{m.Name} makes the images now."; }
                    if (role is "fast" or "snel") { Settings.Shared.FastModel = m.Id; app.Notify(); return $"{m.Name} is the fast model now."; }
                    await Host.Select(m.Id);
                    app.Notify();
                    return $"{m.Name} is the heavy model now{(app.EngineState.IsActive ? " and the engine is starting with it" : "; start_engine starts it")}.";
                }
            case "start_engine":
                await app.StartEngineTask();
                return "The engine is starting; that can take a minute for a large model.";
            case "stop_engine":
                app.StopEngine();
                return "The engine is stopping.";
            case "set_setting":
                {
                    var p = typeof(Settings).GetProperty(Arg(a, "setting"), BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)!;
                    p.SetValue(Settings.Shared, Parse(p.PropertyType, Arg(a, "value")));
                    if (p.Name == "FeatureTelegram") app.Telegram.Restart();
                    app.Notify();
                    var later = p.Name is "ContextSize" or "Mtp" or "SkipExperts" or "Vision" ? " It counts after the engine restarts." : "";
                    return $"{p.Name} is now {Convert.ToString(p.GetValue(Settings.Shared), CultureInfo.InvariantCulture)}.{later}";
                }
            case "find_helpers":
                {
                    Host.Cluster.RecheckManual();
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    var list = Host.Cluster.Helpers;
                    return list.Count == 0
                        ? "No helpers found. Each other PC needs WinAIMagic (or MacAIMagic) as helper with its helper started, on a direct cable or with the local network allowed on both. A helper on another subnet: add_helper with its address."
                        : "Helpers: " + string.Join("; ", list.Select(x => $"{x.Id} {x.Name} ({x.Chip}, {x.GpuGb:0.#} GB{(x.Unreachable ? ", not reachable" : "")})"));
                }
            case "add_helper":
                Host.Cluster.AddManual(Arg(a, "address"));
                await Task.Delay(TimeSpan.FromSeconds(5));
                return "Added. " + (Host.Cluster.Helpers.FirstOrDefault(x => x.Manual && x.Host == Arg(a, "address").Split(':')[0]) is { } added
                    ? added.Unreachable ? $"{added.Id} does not answer: is its helper started, and does its firewall allow the app?" : $"{added.Id} answers, {added.GpuGb:0.#} GB for the model."
                    : "It is in the list.");
            case "use_helper":
                {
                    var helper = Host.Cluster.Helpers.First(x => x.Id.Equals(Arg(a, "helper"), StringComparison.OrdinalIgnoreCase) || x.Name.Equals(Arg(a, "helper"), StringComparison.OrdinalIgnoreCase));
                    var set = new HashSet<string>(Host.Cluster.Selected);
                    if (Arg(a, "value") is "false" or "0" or "off") set.Remove(helper.Id); else set.Add(helper.Id);
                    Host.Cluster.Selected = set;
                    app.Notify();
                    return $"{helper.Name} is {(set.Contains(helper.Id) ? "in use" : "not in use")} from the next engine start.";
                }
            case "cluster_role":
                {
                    var role = Arg(a, "value").ToLowerInvariant();
                    if (role == "helper")
                    {
                        if (app.EngineState.IsActive) { await Task.Run(Host.StopEngine); await Task.Delay(TimeSpan.FromSeconds(3)); }
                        Settings.Shared.ClusterRole = "helper";
                        await Task.Run(() => Host.Cluster.StartHelper());
                        app.Notify();
                        return Host.Cluster.HelperRunning ? $"This PC is a helper now, on {Host.Cluster.HelperAddress}." : "This PC is a helper, but the helper did not start: " + string.Join(" ", Host.Cluster.HelperLog.TakeLast(2));
                    }
                    Host.Cluster.StopHelper();
                    Settings.Shared.ClusterRole = "main";
                    app.Notify();
                    return "This PC is the main PC again.";
                }
            case "allow_lan":
                {
                    Settings.Shared.ClusterAllowLan = Arg(a, "value") is not ("false" or "0" or "off");
                    if (Host.Cluster.HelperRunning) await Host.Cluster.RestartHelper();
                    app.Notify();
                    return Settings.Shared.ClusterAllowLan ? "The local network is allowed for the cluster." : "Only a direct cable now.";
                }
            case "start_helper":
                if (app.EngineState.IsActive) { await Task.Run(Host.StopEngine); await Task.Delay(TimeSpan.FromSeconds(3)); }
                await Task.Run(() => Host.Cluster.StartHelper());
                return Host.Cluster.HelperRunning ? $"The helper runs on {Host.Cluster.HelperAddress}." : "The helper did not start: " + string.Join(" ", Host.Cluster.HelperLog.TakeLast(2));
            case "stop_helper":
                Host.Cluster.StopHelper();
                return "The helper is stopped.";
            case "make_image":
                {
                    var size = ImageSize.Parse(Settings.Shared.ImageSize) is { } chosen && ImagePlanner.Sizes.Contains(chosen) ? chosen : ImagePlanner.DefaultSize(Host.System);
                    app.Pane = new Pane(PaneKind.Images);
                    _ = app.MakeImage(Arg(a, "prompt"), size);
                    return $"The image is being made on the Beeld screen ({size.Width} x {size.Height}); it goes to {Paths.PicturesFolder}.";
                }
            default:
                return "Unknown action.";
        }
    }
}
