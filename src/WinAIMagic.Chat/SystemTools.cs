using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat;

/// <summary>System management (Instellingen → Systeembeheer): the model reads this PC's settings with system_read, and changes them
/// with system_change, a plan of steps that each have a check, an apply and an undo. The app shows the plan, runs it after the user's
/// yes, records the values before and after, and can undo it later (system_undo, when the user asks for it). Admin steps run together
/// after one UAC prompt of Windows: the model never sees a password. Sensitive changes are not blocked; the approval card warns.</summary>
public static class SystemTools
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string> { "system_read", "system_change", "system_undo" };

    public static JsonArray Definitions => new(
        Function("system_read", "Read this PC's settings and state without changing anything (diagnosis). Runs one PowerShell command in the user's home folder. Only read-only commands are allowed, without pipes, ;, &, redirection, $, (, ), {, } or [ ]: for example Get-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize', Get-NetAdapter, Get-DnsClientServerAddress, Get-Process, Get-Service, Get-CimInstance Win32_OperatingSystem, Get-PSDrive, Get-WinEvent -LogName System -MaxEvents 30, powercfg /query, ipconfig /all, winget list, reg query, systeminfo, netsh advfirewall show allprofiles. Sort or filter the result yourself. For a change, use system_change.",
            new JsonObject { ["command"] = Prop("string", "One read-only command line") }, "command"),
        Function("system_change", "Change this PC's settings with a plan the user approves first. Give every step a check (a read-only command that shows the current value), an apply (the change, PowerShell) and an undo (puts the old value back; write the value from the check in it). Set admin to true for a step that needs administrator rights: Windows then shows one UAC prompt for all admin steps; never use Start-Process -Verb RunAs. Restart what reads the setting in the same apply (Stop-Process -Name explorer -Force for the taskbar and Explorer). Use the skill for the subject first when there is one.",
            new JsonObject
            {
                ["summary"] = Prop("string", "What the plan does, in one sentence for the user"),
                ["steps"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["title"] = Prop("string", "The step in plain words, e.g. Taskbar on the left"),
                            ["check"] = Prop("string", "Read-only command that shows the current value"),
                            ["apply"] = Prop("string", "The command that makes the change"),
                            ["undo"] = Prop("string", "The command that puts the old value back"),
                            ["admin"] = Prop("boolean", "true when the step needs administrator rights"),
                        },
                        ["required"] = new JsonArray("title", "apply", "undo"),
                    },
                },
            }, "summary", "steps"),
        Function("system_undo", "Undo an earlier system change by its id (the id system_change returned). The user approves it first.",
            new JsonObject { ["id"] = Prop("string", "The id of the change") }, "id"));

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static JsonObject Function(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray([.. required.Select(r => JsonValue.Create(r))]) },
        },
    };

    public static string PromptSection() =>
        L("Je kunt de instellingen van deze pc bekijken met system_read (alleen lezen, voor diagnose) en wijzigen met system_change: een plan met per stap een check, een wijziging en een ongedaanmaking, in PowerShell. De gebruiker keurt het plan eerst goed; stappen die beheerdersrechten nodig hebben markeer je met admin, dan vraagt Windows zelf om toestemming. Lees eerst de huidige waarde, gok nooit een instelling, en leg bij een gevoelige wijziging (firewall, Defender, BitLocker, UAC, gebruikers) uit wat het betekent. Gaat iets niet met een commando, open dan het juiste scherm van Instellingen met Start-Process ms-settings:… en zeg wat de gebruiker moet aanklikken.");

    // MARK: - reading only

    /// <summary>The words of a command line, or null when it does more than run one program with plain arguments: $, `, @, a line
    /// break, or ; | &amp; &lt; &gt; ( ) { } [ ] outside quotes. Quotes are taken off. Inside double quotes $ and ` still count: PowerShell
    /// expands them there.</summary>
    public static List<string>? Words(string command)
    {
        if (command.IndexOfAny(['\n', '\r', '`', '$']) >= 0) return null;
        var output = new List<string>();
        var word = new StringBuilder();
        char? quote = null;
        bool inWord = false;
        foreach (var c in command)
        {
            if (quote is { } q)
            {
                if (c == q) quote = null; else word.Append(c);
                continue;
            }
            if (c is '\'' or '"') { quote = c; inWord = true; continue; }
            if (";|&<>(){}[]@".Contains(c)) return null;
            if (c is ' ' or '\t')
            {
                if (inWord) { output.Add(word.ToString()); word.Clear(); inWord = false; }
                continue;
            }
            word.Append(c);
            inWord = true;
        }
        if (quote != null) return null;
        if (inWord) output.Add(word.ToString());
        return output.Count == 0 ? null : output;
    }

    private static readonly HashSet<string> ReadPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Test-NetConnection", "Test-Connection", "Test-Path", "Resolve-DnsName", "Select-String",
        "ls", "dir", "gci", "cat", "type", "gc", "gps", "gsv", "gp", "gip",
        "systeminfo", "tasklist", "whoami", "hostname", "driverquery", "getmac", "netstat", "ping", "tracert", "pathping", "nslookup", "quser",
    };

    private static readonly HashSet<string> PowercfgReads = new(StringComparer.OrdinalIgnoreCase)
    { "/query", "/q", "/list", "/l", "/getactivescheme", "/a", "/availablesleepstates", "/requests", "/lastwake", "/waketimers", "/devicequery" };
    private static readonly HashSet<string> PowercfgReports = new(StringComparer.OrdinalIgnoreCase) { "/batteryreport", "/energy", "/sleepstudy" };

    /// <summary>Whether the app may run this without asking: one program from a list of read-only commands, with arguments that
    /// cannot change anything. Everything else is a change.</summary>
    public static bool IsReadOnly(string command)
    {
        if (Words(command) is not { } w) return false;
        var program = Path.GetFileName(w[0]);
        if (program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) program = program[..^4];
        var args = w.Skip(1).ToList();
        var first = args.FirstOrDefault() ?? "";
        bool Has(params string[] any) => args.Exists(a => any.Contains(a, StringComparer.OrdinalIgnoreCase));
        if (program.StartsWith("Get-", StringComparison.OrdinalIgnoreCase))
            // Get-Credential waits for a password nobody can type here
            return !program.Equals("Get-Credential", StringComparison.OrdinalIgnoreCase);
        if (ReadPrograms.Contains(program))
            return !(program.Equals("Select-String", StringComparison.OrdinalIgnoreCase) && Has("-OutVariable"));
        switch (program.ToLowerInvariant())
        {
            case "powercfg":
                if (PowercfgReads.Contains(first)) return true;
                // a report goes into the home folder, never to a path of the model's choice
                return PowercfgReports.Contains(first) && args.Skip(1).All(a => a.Equals("/duration", StringComparison.OrdinalIgnoreCase) || a.All(char.IsDigit));
            case "ipconfig":
                return args.All(a => a.Equals("/all", StringComparison.OrdinalIgnoreCase) || a.Equals("/displaydns", StringComparison.OrdinalIgnoreCase));
            case "winget":
                {
                    var verb = first.ToLowerInvariant();
                    var flags = args.Skip(1).ToList();
                    if (verb is "--version" or "-v") return args.Count == 1;
                    if (verb == "source") return args.Count == 2 && args[1].Equals("list", StringComparison.OrdinalIgnoreCase);
                    if (verb is "list" or "ls" or "show" or "search" or "find") return !flags.Exists(f => f.Equals("--force", StringComparison.OrdinalIgnoreCase));
                    // without a package winget upgrade only lists what can be updated
                    if (verb == "upgrade") return flags.All(f => f.ToLowerInvariant() is "--include-unknown" or "-u" or "--accept-source-agreements" or "--disable-interactivity");
                    return false;
                }
            case "reg":
                return first.Equals("query", StringComparison.OrdinalIgnoreCase);
            case "route":
                return first.Equals("print", StringComparison.OrdinalIgnoreCase);
            case "arp":
                return args.Count > 0 && args.All(a => a is "-a" or "-g" or "-v" || !a.StartsWith('-'));
            case "netsh":
                {
                    string[] writes = ["set", "add", "delete", "reset", "import", "install", "uninstall", "connect", "disconnect", "start", "stop", "flush", "renew", "release", "exec", "dump", "-f", "-r"];
                    return Has("show") && !Has(writes);
                }
            case "sc":
                return first.ToLowerInvariant() is "query" or "qc" or "queryex" or "qdescription" or "qfailure" or "enumdepend";
            case "schtasks":
                return first.Equals("/query", StringComparison.OrdinalIgnoreCase);
            case "w32tm":
                return first.Equals("/query", StringComparison.OrdinalIgnoreCase) || first.Equals("/tz", StringComparison.OrdinalIgnoreCase);
            case "manage-bde":
                return first.Equals("-status", StringComparison.OrdinalIgnoreCase) || first.Equals("-protectors", StringComparison.OrdinalIgnoreCase) && args.Skip(1).FirstOrDefault()?.Equals("-get", StringComparison.OrdinalIgnoreCase) == true;
            case "bcdedit":
                return args.Count == 0 || first.Equals("/enum", StringComparison.OrdinalIgnoreCase);
            case "vssadmin":
                return first.Equals("list", StringComparison.OrdinalIgnoreCase);
            case "gpresult":
                return args.Count > 0 && args.All(a => a.ToLowerInvariant() is "/r" or "/z" or "/v" or "/scope" or "user" or "computer");
            case "auditpol":
                return first.Equals("/get", StringComparison.OrdinalIgnoreCase) || first.Equals("/list", StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    /// <summary>A check as models often write it, `Get-ItemPropertyValue … 2&gt;$null` or `reg query … 2&gt;nul`, without the parts that only
    /// hide an error: what is left, when that only reads. Null when it does more.</summary>
    public static string? ReadableCheck(string check)
    {
        var core = check.Trim();
        // a fallback at the end (PowerShell 7): || echo "…", || Write-Output '…', || $true
        core = Regex.Replace(core, @"\s*\|\|\s*((echo|Write-Output|Write-Host)(\s+(""[^""$`]*""|'[^']*'|[^;|&<>$`(){}]*))?|\$true|\$null)\s*$", "", RegexOptions.IgnoreCase);
        core = Regex.Replace(core, @"\s*2>\s*(\$null|nul)\b|\s*2>&1|\s*-ErrorAction\s+(SilentlyContinue|Ignore|0)|\s*-EA\s+(SilentlyContinue|Ignore|0)", "", RegexOptions.IgnoreCase).Trim();
        return IsReadOnly(core) ? core : null;
    }

    private const string Word = @"(""[^""$`]+""|'[^']+'|[^\s;|&<>(){}$`'""]+)";
    private static readonly Regex RegAdd = new(@"\breg(?:\.exe)?\s+add\s+" + Word + @"[^;|&\n]*?\s/v\s+" + Word, RegexOptions.IgnoreCase);
    private static readonly Regex SetPathName = new(@"\b(?:Set|New)-ItemProperty\b[^;|&\n]*?-Path\s+" + Word + @"[^;|&\n]*?-Name\s+" + Word, RegexOptions.IgnoreCase);
    private static readonly Regex SetNamePath = new(@"\b(?:Set|New)-ItemProperty\b[^;|&\n]*?-Name\s+" + Word + @"[^;|&\n]*?-Path\s+" + Word, RegexOptions.IgnoreCase);

    /// <summary>The check for a step that writes one registry value: <c>reg add k /v n …</c> → <c>reg query k /v n</c>, and
    /// <c>Set-ItemProperty -Path p -Name n …</c> → <c>Get-ItemPropertyValue -Path p -Name n</c>. Null when the step writes none or
    /// more than one, or when the result would not only read.</summary>
    public static string? DerivedCheck(string apply)
    {
        static bool Flag(string w) => w.StartsWith('-') || w.StartsWith('/');
        var reads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in RegAdd.Matches(apply))
            if (!Flag(m.Groups[1].Value) && !Flag(m.Groups[2].Value)) reads.Add($"reg query {m.Groups[1].Value} /v {m.Groups[2].Value}");
        foreach (Match m in SetPathName.Matches(apply))
            if (!Flag(m.Groups[1].Value) && !Flag(m.Groups[2].Value)) reads.Add($"Get-ItemPropertyValue -Path {m.Groups[1].Value} -Name {m.Groups[2].Value}");
        foreach (Match m in SetNamePath.Matches(apply))
            if (!Flag(m.Groups[1].Value) && !Flag(m.Groups[2].Value)) reads.Add($"Get-ItemPropertyValue -Path {m.Groups[2].Value} -Name {m.Groups[1].Value}");
        // a step that writes through both ways, or two values: no guess
        if (reads.Count != 1 || Keys(apply).Count != 1) return null;
        var check = reads.First();
        return IsReadOnly(check) ? check : null;
    }

    // MARK: - plans

    public sealed record Step(string Title, string Check, string Apply, string Undo, bool Admin);

    public sealed class StepResult
    {
        [JsonPropertyName("before")] public string Before { get; set; } = "";
        [JsonPropertyName("after")] public string After { get; set; } = "";
        [JsonPropertyName("output")] public string Output { get; set; } = "";
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("ran")] public bool Ran { get; set; }
    }

    /// <summary>One plan the model carried out, kept in Changes\&lt;id&gt;\plan.json, in the Mac's format.</summary>
    public sealed class Change
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("date")] public DateTime Date { get; set; }
        [JsonPropertyName("summary")] public string Summary { get; set; } = "";
        [JsonPropertyName("steps")] public List<Step> Steps { get; set; } = [];
        [JsonPropertyName("results")] public List<StepResult> Results { get; set; } = [];
        /// <summary>Registry keys exported before the change: key → .reg file in the change's folder.</summary>
        [JsonPropertyName("exports")] public Dictionary<string, string> Exports { get; set; } = [];
        /// <summary>A restore point was made before the admin steps.</summary>
        [JsonPropertyName("snapshot")] public bool Snapshot { get; set; }
        [JsonPropertyName("undone")] public DateTime? Undone { get; set; }
        [JsonPropertyName("undoReport")] public string? UndoReport { get; set; }

        [JsonIgnore] public bool Admin => Steps.Exists(s => s.Admin);
        [JsonIgnore] public bool Succeeded => Results.TrueForAll(r => r.Ok || !r.Ran) && Results.Exists(r => r.Ran);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed class PlanException(string message) : Exception(message);

    public static (string Summary, List<Step> Steps) Plan(ToolCall call)
    {
        var a = WorkspaceTools.Args(call);
        var raw = a["steps"] as JsonArray;
        // some models send the array as a JSON string
        if (raw == null && WorkspaceTools.Str(a, "steps") is { } text)
        {
            try { raw = JsonNode.Parse(text) as JsonArray; } catch (JsonException) { }
        }
        if (raw == null || raw.Count == 0) throw new PlanException("The plan has no steps. Give steps with title, check, apply and undo.");
        var steps = new List<Step>();
        for (int i = 0; i < raw.Count; i++)
        {
            var s = raw[i] as JsonObject ?? [];
            var apply = (WorkspaceTools.Str(s, "apply") ?? "").Trim();
            if (apply.Length == 0) throw new PlanException($"Step {i + 1} has no apply command.");
            bool admin = s["admin"] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var t) && t.Equals("true", StringComparison.OrdinalIgnoreCase));
            var title = WorkspaceTools.Str(s, "title") is { Length: > 0 } given ? given : apply;
            var check = (WorkspaceTools.Str(s, "check") ?? "").Trim();
            // a model that leaves the check out of a step that writes one value: the card showed no value and nothing was recorded
            if (check.Length == 0) check = DerivedCheck(apply) ?? "";
            steps.Add(new Step(title, check, apply, (WorkspaceTools.Str(s, "undo") ?? "").Trim(), admin));
        }
        return (WorkspaceTools.Str(a, "summary") is { Length: > 0 } summary ? summary : steps[0].Title, steps);
    }

    // MARK: - warnings

    /// <summary>Changes that weaken the PC's protection or are hard to undo. They are not blocked: the card says what they mean.</summary>
    public static readonly (string Pattern, string Warning)[] Sensitive =
    [
        (@"Set-NetFirewallProfile[^\n]*-Enabled\s+(\$?false|0)\b|netsh\s+advfirewall\s+set\s+\w+\s+state\s+off", "De firewall gaat uit: programma's op deze pc zijn dan van buitenaf bereikbaar."),
        (@"Set-MpPreference[^\n]*-Disable\w*\s+(\$?true|1)\b|Add-MpPreference[^\n]*-Exclusion|DisableAntiSpyware|DisableRealtimeMonitoring", "Dit zet een deel van Microsoft Defender uit of maakt een uitzondering: de virusscanner controleert dan minder."),
        (@"SmartScreenEnabled|EnableSmartScreen", "SmartScreen gaat uit: Windows waarschuwt dan niet meer voor onbekende apps en websites."),
        (@"manage-bde\s+-off|Disable-BitLocker|Suspend-BitLocker", "BitLocker gaat uit: de schijf is dan niet meer versleuteld als de pc kwijtraakt."),
        (@"EnableLUA|ConsentPromptBehaviorAdmin|PromptOnSecureDesktop", "Dit wijzigt Gebruikersaccountbeheer (UAC): programma's kunnen dan zonder vraag beheerdersrechten krijgen."),
        (@"\bnet\s+(user|localgroup)\b|\b(New|Remove|Set|Disable|Enable|Rename)-LocalUser\b|\b(Add|Remove)-LocalGroupMember\b", "Dit wijzigt gebruikersaccounts of groepen."),
        (@"\bbcdedit\b(?![^\n]*/enum)", "Opstartinstellingen (bcdedit) gelden bij het opstarten; een verkeerde waarde kan het opstarten verstoren."),
        (@"\b(Remove-Item|ri|rm|del|erase|rd|rmdir)\b[^\n]*(-Recurse\b|\s-r\b|/s\b)", "Dit verwijdert bestanden definitief, niet via de Prullenbak; dit is niet ongedaan te maken."),
        (@"Set-ExecutionPolicy[^\n]*\b(Unrestricted|Bypass)\b", "Dit laat PowerShell-scripts draaien zonder te controleren wie ze maakte."),
        (@"powercfg[^\n]*/h(ibernate)?\s+off", "Zonder sluimerstand kan werk verloren gaan als de batterij leeg raakt."),
        (@"-Verb\s+RunAs|\bsudo\b|\brunas\b", "Deze stap start zelf iets als beheerder. Het model had de stap als beheerder moeten markeren; dan vraagt Windows één keer voor het hele plan."),
        (@"\b(iwr|irm|curl|wget|Invoke-WebRequest|Invoke-RestMethod)\b[^\n]*\|\s*(iex|Invoke-Expression)\b|\b(iex|Invoke-Expression)\b", "Dit voert een script uit dat niemand heeft gelezen, mogelijk van internet."),
        (@"(Stop|Set|Remove)-Service[^\n]*\b(WinDefend|wuauserv|mpssvc|EventLog|BFE|wscsvc|WdNisSvc|SecurityHealthService)\b|\bsc(\.exe)?\s+(stop|config|delete)\s+(WinDefend|wuauserv|mpssvc|EventLog|BFE|wscsvc)\b", "Dit stopt een dienst van Windows zelf."),
        (@"ProxyServer|ProxyEnable|netsh\s+winhttp\s+set\s+proxy", "Een proxy laat al het webverkeer via een ander adres lopen."),
        (@"drivers\\etc\\hosts", "Het hosts-bestand bepaalt waar websites naartoe gaan."),
        (@"\b(Install-WindowsUpdate|usoclient|wuauclt)\b", "Een Windows-update installeren kan de pc laten herstarten."),
        (@"\bshutdown(\.exe)?\s+[/-][rspg]\b|\bRestart-Computer\b|\bStop-Computer\b", "De pc wordt herstart of uitgezet; niet bewaard werk gaat verloren."),
    ];

    public static List<string> Warnings(IReadOnlyList<Step> steps, bool afterWeb)
    {
        var output = new List<string>();
        var text = string.Join("\n", steps.Select(s => s.Apply + "\n" + s.Undo));
        foreach (var (pattern, warning) in Sensitive)
            if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)) output.Add(L(warning));
        if (steps.Any(s => s.Undo.Length == 0)) output.Add(L("Niet elke stap heeft een manier om hem ongedaan te maken."));
        if (afterWeb) output.Add(L("Dit plan volgt op het lezen van het web. Een webpagina kan het model instructies geven: controleer of dit is wat jij wilde."));
        return output;
    }

    // MARK: - running

    public static int? ExitCode(string output)
    {
        var m = Regex.Match(output, @"\[exit code (-?\d+)\]\s*$");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>The output without the app's status line.</summary>
    public static string Clean(string output)
    {
        var t = Regex.Replace(output, @"\n?\[(exit code -?\d+|stopped[^\]]*)\]\s*$", "").Trim();
        if (t == "(no output)") t = "";
        return t.Length > 2000 ? TextLimit.Prefix(t, 2000) + " …" : t;
    }

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static async Task<(string Output, bool Ok)> RunUser(string command, int timeout = 120, CancellationToken ct = default)
    {
        var output = await CommandRunner.RunAsync(command, Home, timeout, ct).ConfigureAwait(false);
        return (Clean(output), ExitCode(output) == 0);
    }

    /// <summary>The restore point before an admin plan. Checkpoint-Computer is only in Windows PowerShell 5.1, so it runs there.
    /// Windows makes at most one per 24 hours; then it only warns, and the step counts as done.</summary>
    public static string RestorePointCommand =>
        // single quotes around the inner command, so this shell expands nothing in it; a warning (one in the last 24 hours) is said
        $"& '{CommandRunner.WindowsPowerShell}' -NoLogo -NoProfile -NonInteractive -Command 'try {{ Checkpoint-Computer -Description ''WinAIMagic'' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop -WarningAction SilentlyContinue -WarningVariable w; if ($w) {{ ''No restore point: '' + $w }} else {{ ''{RestorePointMade}'' }} }} catch {{ ''No restore point: '' + $_ }}'; $global:LASTEXITCODE = 0";

    /// <summary>What the restore point command says when it worked; without a restore point the plan still goes on.</summary>
    public const string RestorePointMade = "restore point made";

    /// <summary>The script an elevated PowerShell runs: each command with its own output and exit status, written to a log, because
    /// a process started through UAC gives no output back.</summary>
    public static string AdminScript(IReadOnlyList<string> commands, string log)
    {
        var lit = "'" + log.Replace("'", "''") + "'";
        var s = new StringBuilder();
        s.AppendLine("try { $__utf8 = New-Object System.Text.UTF8Encoding $false; [Console]::OutputEncoding = $__utf8; $OutputEncoding = $__utf8 } catch { }");
        s.AppendLine($"function __Log($t) {{ Add-Content -LiteralPath {lit} -Value $t -Encoding UTF8 }}");
        for (int i = 0; i < commands.Count; i++)
        {
            s.AppendLine($"__Log '@@STEP {i}'");
            s.AppendLine("$global:LASTEXITCODE = 0; $__bad = $false");
            s.AppendLine("try {");
            s.AppendLine("  $__out = & {");
            s.AppendLine(commands[i]);
            s.AppendLine("  } 2>&1");
            s.AppendLine("  foreach ($__o in $__out) { if ($__o -is [System.Management.Automation.ErrorRecord]) { $__bad = $true } }");
            s.AppendLine("  if ($__out) { __Log (($__out | Out-String).TrimEnd()) }");
            s.AppendLine("} catch { $__bad = $true; __Log ($_ | Out-String).TrimEnd() }");
            s.AppendLine("if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { $__bad = $true }");
            s.AppendLine("__Log ('@@EXIT ' + $(if ($__bad) { 1 } else { 0 }))");
            // a failing step stops the plan: the steps after it are not run
            s.AppendLine("if ($__bad) { __Log '@@END'; exit 1 }");
        }
        s.AppendLine("__Log '@@END'");
        return s.ToString();
    }

    /// <summary>What the log of an admin script says per command.</summary>
    public static List<(string Output, bool Ok)> ParseAdminLog(string text, int count, string missing)
    {
        var results = Enumerable.Repeat((missing, false), count).ToList();
        foreach (var part in text.Replace("\r\n", "\n").Split("@@STEP ").Skip(1))
        {
            var lines = part.Split('\n').ToList();
            if (!int.TryParse(lines[0].Trim(), out var i) || i < 0 || i >= count) continue;
            var body = lines.Skip(1).ToList();
            bool ok = false;
            int e = body.FindLastIndex(l => l.StartsWith("@@EXIT ", StringComparison.Ordinal));
            if (e >= 0)
            {
                ok = body[e].Trim() == "@@EXIT 0";
                body = body.GetRange(0, e);
            }
            else body = body.Where(l => l.Trim() != "@@END").ToList();
            results[i] = (Clean(string.Join("\n", body)), ok);
        }
        return results;
    }

    /// <summary>Tests run the admin script without UAC, to check what it writes.</summary>
    internal static bool ElevateForTests { get; set; } = true;

    /// <summary>The commands as administrator, after one UAC prompt. Each gets its own output and status.</summary>
    public static async Task<List<(string Output, bool Ok)>> RunAdmin(IReadOnlyList<string> commands, int timeout = 900, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var script = Path.Combine(Path.GetTempPath(), $"winaimagic-admin-{id}.ps1");
        var log = Path.Combine(Path.GetTempPath(), $"winaimagic-admin-{id}.log");
        try
        {
            await File.WriteAllTextAsync(script, AdminScript(commands, log), new UTF8Encoding(true), CancellationToken.None).ConfigureAwait(false);
            if (!ElevateForTests)
            {
                await CommandRunner.RunAsync($"& '{script.Replace("'", "''")}'", Home, timeout, ct).ConfigureAwait(false);
            }
            else
            {
                var info = new ProcessStartInfo(CommandRunner.Shell, $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Home,
                };
                using var p = Process.Start(info);
                if (p == null) return commands.Select(_ => ("Could not start the administrator step.", false)).ToList();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(timeout));
                try { await p.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { try { p.Kill(true); } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { } }
            }
            var text = File.Exists(log) ? await File.ReadAllTextAsync(log, CancellationToken.None).ConfigureAwait(false) : "";
            if (!text.Contains("@@STEP 0")) return commands.Select(_ => ("The administrator step did not run.", false)).ToList();
            return ParseAdminLog(text, commands.Count, "Not run: the administrator step stopped before it.");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: No in the UAC prompt, nothing changed
            return commands.Select(_ => ("Cancelled by the user in the Windows prompt (UAC); nothing was changed.", false)).ToList();
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return commands.Select(_ => ($"Could not start the administrator step: {e.Message}", false)).ToList();
        }
        finally
        {
            foreach (var f in new[] { script, log })
                try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Runs the commands in order: one at a time for the user, and runs of admin steps together, so one UAC prompt covers
    /// them. Stops at the first that fails; the rest stay unrun (null). The restore point goes first in the first admin run.</summary>
    public static async Task<((string Output, bool Ok)?[] Results, bool? Snapshot)> RunInOrder(IReadOnlyList<(string Command, bool Admin)> items, bool snapshot, CancellationToken ct = default)
    {
        var results = new (string Output, bool Ok)?[items.Count];
        bool? made = null;
        bool pending = snapshot;
        int i = 0;
        while (i < items.Count)
        {
            if (items[i].Admin)
            {
                int j = i;
                while (j < items.Count && items[j].Admin) j++;
                var commands = items.Skip(i).Take(j - i).Select(x => x.Command).ToList();
                if (pending) commands.Insert(0, RestorePointCommand);
                var output = await RunAdmin(commands, ct: ct).ConfigureAwait(false);
                if (pending)
                {
                    made = output[0].Output.Contains(RestorePointMade, StringComparison.Ordinal);
                    output.RemoveAt(0);
                    pending = false;
                }
                for (int k = i; k < j; k++)
                {
                    results[k] = output[k - i];
                    if (!output[k - i].Ok) return (results, made);
                }
                i = j;
            }
            else
            {
                var output = await RunUser(items[i].Command, ct: ct).ConfigureAwait(false);
                results[i] = output;
                if (!output.Ok) return (results, made);
                i++;
            }
        }
        return (results, made);
    }

    private static readonly Regex RegistryKey = new(
        @"(?:\breg(?:\.exe)?\s+(?:add|delete)\s+|\b(?:Set|New|Remove|Clear)-ItemProperty\b[^\n]*?-(?:Literal)?Path\s+)(""[^""]+""|'[^']+'|\S+)",
        RegexOptions.IgnoreCase);

    /// <summary>A registry path as reg.exe writes it ("HKCU\Software\X"), or null when it is not one.</summary>
    public static string? RegPath(string raw)
    {
        var p = raw.Trim('"', '\'').Trim();
        p = Regex.Replace(p, @"^(Microsoft\.PowerShell\.Core\\)?Registry::", "", RegexOptions.IgnoreCase);
        var m = Regex.Match(p, @"^(HKEY_CURRENT_USER|HKCU|HKEY_LOCAL_MACHINE|HKLM|HKEY_CLASSES_ROOT|HKCR|HKEY_USERS|HKU|HKEY_CURRENT_CONFIG|HKCC):?\\?(.*)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var hive = m.Groups[1].Value.ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" or "HKCU" => "HKCU",
            "HKEY_LOCAL_MACHINE" or "HKLM" => "HKLM",
            "HKEY_CLASSES_ROOT" or "HKCR" => "HKCR",
            "HKEY_USERS" or "HKU" => "HKU",
            _ => "HKCC",
        };
        var rest = m.Groups[2].Value.TrimEnd('\\');
        return rest.Length == 0 ? null : hive + "\\" + rest;
    }

    /// <summary>The registry keys a step writes with reg add/delete or *-ItemProperty -Path.</summary>
    public static List<string> Keys(string command) =>
        RegistryKey.Matches(command).Select(m => RegPath(m.Groups[1].Value)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The programs a step restarts (Stop-Process -Name x, taskkill /im x.exe); Explorer starts again by itself.</summary>
    public static List<string> Restarts(string text) =>
        Regex.Matches(text, @"\bStop-Process\b[^\n]*?-Name\s+[""']?([A-Za-z][\w.-]*)|\btaskkill\b[^\n]*?/im\s+[""']?([A-Za-z][\w.-]*?)(\.exe)?\b", RegexOptions.IgnoreCase)
            .Select(m => (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToLowerInvariant())
            .Distinct().ToList();

    public static string NewId(DateTime? date = null) =>
        (date ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..4];

    private static string RegExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");

    private static async Task<bool> Export(string key, string file)
    {
        var r = await RunUser($"& '{RegExe}' export '{key.Replace("'", "''")}' '{file.Replace("'", "''")}' /y", 30).ConfigureAwait(false);
        return r.Ok && File.Exists(file);
    }

    /// <summary>The current value for each step's check, when the check only reads (for the approval card).</summary>
    public static async Task<List<string?>> Preview(IReadOnlyList<Step> steps, CancellationToken ct = default)
    {
        var output = new List<string?>();
        foreach (var s in steps)
        {
            if (s.Check.Length > 0 && ReadableCheck(s.Check) is { } check)
            {
                var r = await RunUser(check, 20, ct).ConfigureAwait(false);
                // a value that is not there yet gives PowerShell's whole error: its first line says enough
                var first = r.Output.Split('\n')[0].Trim();
                output.Add(r.Output.Length == 0 ? L("(leeg)") : r.Ok ? r.Output : L($"(niet te lezen: {TextLimit.Prefix(first, 160)})"));
            }
            else output.Add(null);
        }
        return output;
    }

    /// <summary>Carries out an approved plan: exports, values before, the changes, values after, and the record.</summary>
    public static async Task<Change> Apply(string summary, IReadOnlyList<Step> steps, bool snapshot, string? folder = null, CancellationToken ct = default)
    {
        folder ??= Paths.Changes;
        var change = new Change
        {
            Id = NewId(), Date = DateTime.Now, Summary = summary, Steps = [.. steps],
            Results = steps.Select(_ => new StepResult()).ToList(),
            Snapshot = snapshot && steps.Any(s => s.Admin),
        };
        var dir = Path.Combine(folder, change.Id);
        Directory.CreateDirectory(dir);

        // an exact copy of every registry key the plan writes for the user, so undo does not depend on the model's undo alone
        int n = 0;
        foreach (var key in steps.Where(s => !s.Admin).SelectMany(s => Keys(s.Apply)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = $"key{++n}.reg";
            if (await Export(key, Path.Combine(dir, file)).ConfigureAwait(false)) change.Exports[key] = file;
        }
        for (int i = 0; i < steps.Count; i++)
            if (steps[i].Check.Length > 0) change.Results[i].Before = (await RunUser(steps[i].Check, 30, ct).ConfigureAwait(false)).Output;
        var (ran, made) = await RunInOrder(steps.Select(s => (s.Apply, s.Admin)).ToList(), change.Snapshot, ct).ConfigureAwait(false);
        if (made is { } m) change.Snapshot = m;
        for (int i = 0; i < ran.Length; i++)
        {
            if (ran[i] is not { } r) { change.Results[i].Output = "Not run: an earlier step failed."; continue; }
            change.Results[i].Ran = true;
            change.Results[i].Ok = r.Ok;
            change.Results[i].Output = r.Output;
            if (steps[i].Check.Length > 0) change.Results[i].After = (await RunUser(steps[i].Check, 30, ct).ConfigureAwait(false)).Output;
        }
        Save(change, folder);
        return change;
    }

    /// <summary>The value names directly under the key's own [section] of a .reg export.</summary>
    public static HashSet<string> ValueNames(string regText, string key)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var full = FullHive(key);
        bool inKey = false;
        foreach (var line in regText.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith('[')) { inKey = line.TrimEnd().Equals("[" + full + "]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inKey) continue;
            if (line.StartsWith("@=")) names.Add("");
            else if (line.StartsWith('"'))
            {
                int end = line.IndexOf("\"=", 1, StringComparison.Ordinal);
                if (end > 0) names.Add(line[1..end].Replace("\\\\", "\\").Replace("\\\"", "\""));
            }
        }
        return names;
    }

    private static string FullHive(string key)
    {
        var hive = key.Split('\\')[0];
        var full = hive switch { "HKCU" => "HKEY_CURRENT_USER", "HKLM" => "HKEY_LOCAL_MACHINE", "HKCR" => "HKEY_CLASSES_ROOT", "HKU" => "HKEY_USERS", _ => "HKEY_CURRENT_CONFIG" };
        return full + key[hive.Length..];
    }

    /// <summary>Puts an earlier change back: the undo commands of the steps that ran, last first; then each exported registry key that
    /// still differs is imported again, and values the change added to it are removed. Then every check is compared with its value
    /// from before.</summary>
    public static async Task<Change> Undo(Change change, string? folder = null, CancellationToken ct = default)
    {
        folder ??= Paths.Changes;
        var dir = Path.Combine(folder, change.Id);
        var order = Enumerable.Range(0, change.Steps.Count).Reverse().Where(i => change.Results[i].Ran && change.Steps[i].Undo.Length > 0).ToList();
        var (ran, _) = await RunInOrder(order.Select(i => (change.Steps[i].Undo, change.Steps[i].Admin)).ToList(), false, ct).ConfigureAwait(false);
        var lines = new List<string>();
        for (int k = 0; k < order.Count; k++)
        {
            int i = order[k];
            lines.Add($"{i + 1}. {change.Steps[i].Title}: " + (ran[k] is not { } r ? "not run" : r.Ok ? "undone" : $"failed: {r.Output}"));
        }
        bool restored = false;
        foreach (var (key, file) in change.Exports.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var saved = Path.Combine(dir, file);
            if (!File.Exists(saved)) continue;
            var now = Path.Combine(Path.GetTempPath(), "winaimagic-" + Guid.NewGuid().ToString("N") + ".reg");
            try
            {
                bool exists = await Export(key, now).ConfigureAwait(false);
                var before = await File.ReadAllTextAsync(saved, ct).ConfigureAwait(false);
                var after = exists ? await File.ReadAllTextAsync(now, ct).ConfigureAwait(false) : "";
                if (after == before) continue;
                var r = await RunUser($"& '{RegExe}' import '{saved.Replace("'", "''")}'", 30, ct).ConfigureAwait(false);
                if (!r.Ok) { lines.Add($"{key}: could not restore the copy made before the change: {r.Output}"); continue; }
                // import adds and overwrites; what the change added beside the old values goes too
                foreach (var extra in ValueNames(after, key).Except(ValueNames(before, key), StringComparer.OrdinalIgnoreCase))
                    await RunUser(extra.Length == 0 ? $"& '{RegExe}' delete '{key.Replace("'", "''")}' /ve /f" : $"& '{RegExe}' delete '{key.Replace("'", "''")}' /v '{extra.Replace("'", "''")}' /f", 30, ct).ConfigureAwait(false);
                restored = true;
                lines.Add($"{key}: restored from the copy made before the change");
            }
            finally
            {
                try { File.Delete(now); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        if (restored)
        {
            // what reads those settings sees them only after a restart; Explorer starts again by itself
            foreach (var p in change.Steps.SelectMany(s => Restarts(s.Apply + "\n" + s.Undo)).Distinct().Where(p => p == "explorer"))
                await RunUser($"Stop-Process -Name {p} -Force", 20, ct).ConfigureAwait(false);
        }
        for (int i = 0; i < change.Steps.Count; i++)
        {
            var s = change.Steps[i];
            if (!change.Results[i].Ran || s.Check.Length == 0) continue;
            var now = (await RunUser(s.Check, 30, ct).ConfigureAwait(false)).Output;
            var before = change.Results[i].Before;
            lines.Add($"{i + 1}. check: " + (now == before ? $"back to \"{TextLimit.Prefix(before, 200)}\"" : $"now \"{TextLimit.Prefix(now, 200)}\", before it was \"{TextLimit.Prefix(before, 200)}\""));
        }
        change.Undone = DateTime.Now;
        change.UndoReport = string.Join("\n", lines);
        Save(change, folder);
        return change;
    }

    // MARK: - the record

    public static void Save(Change change, string? folder = null)
    {
        var dir = Path.Combine(folder ?? Paths.Changes, change.Id);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "plan.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(change, Json));
        File.Move(file + ".tmp", file, true);
    }

    /// <summary>Every recorded change, newest first.</summary>
    public static List<Change> Load(string? folder = null)
    {
        folder ??= Paths.Changes;
        if (!Directory.Exists(folder)) return [];
        var list = new List<Change>();
        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            try
            {
                var file = Path.Combine(dir, "plan.json");
                if (File.Exists(file) && JsonSerializer.Deserialize<Change>(File.ReadAllText(file), Json) is { } c) list.Add(c);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return list.OrderByDescending(c => c.Date).ToList();
    }

    /// <summary>What the model reads back after a plan ran.</summary>
    public static string Report(Change c)
    {
        var lines = new List<string> { $"Change {c.Id}: {c.Summary}" };
        for (int i = 0; i < c.Steps.Count; i++)
        {
            var s = c.Steps[i];
            var r = c.Results[i];
            var state = !r.Ran ? "not run" : r.Ok ? "ok" : "FAILED";
            var line = $"{i + 1}. {s.Title}: {state}";
            if (s.Check.Length > 0) line += $" — before: \"{TextLimit.Prefix(r.Before, 300)}\", after: \"{TextLimit.Prefix(r.After, 300)}\"";
            if (!r.Ok && r.Output.Length > 0) line += $"\n   output: {TextLimit.Prefix(r.Output, 800)}";
            lines.Add(line);
        }
        if (c.Admin && c.Snapshot) lines.Add("A restore point was made before the administrator steps.");
        lines.Add(c.Succeeded
            ? $"Tell the user what changed, and that you can undo it when they ask (system_undo id {c.Id})."
            : $"Not everything worked. Tell the user what did and did not change; offer system_undo id {c.Id} for the steps that ran.");
        return string.Join("\n", lines);
    }

    public static string Label(ToolCall call)
    {
        var a = WorkspaceTools.Args(call);
        return call.Name switch
        {
            "system_read" => L($"Systeem lezen: {WorkspaceTools.Str(a, "command") ?? ""}"),
            "system_change" => L($"Systeemwijziging: {WorkspaceTools.Str(a, "summary") ?? ""}"),
            "system_undo" => L($"Ongedaan maken: {WorkspaceTools.Str(a, "id") ?? ""}"),
            _ => call.Name,
        };
    }
}
