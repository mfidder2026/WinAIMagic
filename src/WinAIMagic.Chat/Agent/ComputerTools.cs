using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat.Agent;

/// <summary>Controlling other programs (phase 6): only the programs the user allows (Instellingen → Agent), through the UI
/// Automation tree of their window: the model sees numbered buttons, fields and menus, and presses, types or sends keys. Every
/// action shows a card (unless "Altijd in dit gesprek"); one that sends, pays or deletes asks every time. Never in a password
/// field, and never in programs that hold passwords, run commands or change Windows' settings.</summary>
public sealed class ComputerTools : IAgentToolSet
{
    public static ComputerTools Shared { get; } = new();

    public IReadOnlySet<string> Names { get; } = new HashSet<string> { "computer_apps", "computer_look", "computer_click", "computer_type", "computer_key" };
    public bool IsOn => Settings.Shared.FeatureComputerUse || ForTests;
    public string Icon => "computer";
    public bool ForTests { get; set; }

    /// <summary>Programs that are never controlled, whatever the list says (by file name): command lines and scripts, Windows'
    /// settings and the registry, password managers, the sign-in and UAC screens, and WinAIMagic itself.</summary>
    public static readonly IReadOnlySet<string> Never = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "powershell_ise.exe", "WindowsTerminal.exe", "wt.exe", "conhost.exe", "OpenConsole.exe",
        "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regedit.exe", "mmc.exe", "control.exe", "SystemSettings.exe",
        "taskmgr.exe", "credwiz.exe", "consent.exe", "LogonUI.exe", "msiexec.exe", "SecHealthUI.exe",
        "1Password.exe", "Bitwarden.exe", "KeePass.exe", "KeePassXC.exe", "LastPass.exe", "Dashlane.exe", "NordPass.exe", "RoboForm.exe",
        "WinAIMagic.exe",
    };

    public static bool IsNever(string path) => Never.Contains(Path.GetFileName(path.Trim()));

    /// <summary>The programs the user allows: the paths of their .exe.</summary>
    public List<string> Allowed =>
        [.. Settings.Shared.ComputerApps.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !IsNever(x)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>False for a program on the never-list.</summary>
    public bool Allow(string path)
    {
        if (IsNever(path)) return false;
        if (!Allowed.Contains(path, StringComparer.OrdinalIgnoreCase)) Settings.Shared.ComputerApps = string.Join("\n", Allowed.Append(path.Trim()));
        return true;
    }

    public void Disallow(string path) =>
        Settings.Shared.ComputerApps = string.Join("\n", Allowed.Where(x => !x.Equals(path, StringComparison.OrdinalIgnoreCase)));

    public JsonArray Definitions
    {
        get
        {
            static JsonObject P(string t, string d) => AgentTools.Prop(t, d);
            return
            [
                AgentTools.Function("computer_apps", "The programs you may control on this PC (the user chose them), with their names and whether they run. Read-only."),
                AgentTools.Function("computer_look", "Look at an allowed program: it is started if needed, and you get its window title and the numbered buttons, fields, menus and texts in its window. Read-only.",
                    new JsonObject { ["app"] = P("string", "The program's name or path from computer_apps") }, "app"),
                AgentTools.Function("computer_click", "Press a button, checkbox, menu item or other element by its number from computer_look. The user approves it first.",
                    new JsonObject { ["app"] = P("string", "The program's name or path"), ["element"] = P("integer", "The element's number") }, "app", "element"),
                AgentTools.Function("computer_type", "Put text into a text field by its number from computer_look. Never a password field. The user approves it first.",
                    new JsonObject { ["app"] = P("string", "The program's name or path"), ["element"] = P("integer", "The field's number"), ["text"] = P("string", "The text") }, "app", "element", "text"),
                AgentTools.Function("computer_key", "Press a key or key combination in an allowed program, e.g. enter, escape, tab, ctrl+s, ctrl+shift+n. The user approves it first.",
                    new JsonObject { ["app"] = P("string", "The program's name or path"), ["keys"] = P("string", "The keys") }, "app", "keys"),
            ];
        }
    }

    public string PromptSection => L("Je kunt programma's op deze pc besturen die de gebruiker heeft toegestaan: computer_apps geeft de lijst, computer_look laat de genummerde knoppen, velden en menu's van het venster zien, en computer_click, computer_type en computer_key doen een handeling. Kijk na elke handeling opnieuw met computer_look. Elke handeling vraagt eerst toestemming. Typ nooit wachtwoorden en doe geen betalingen.");

    public string Label(ToolCall call)
    {
        var a = WorkspaceTools.Args(call);
        var app = AppName(AgentTools.Str(a, "app"));
        return call.Name switch
        {
            "computer_apps" => L("Programma's bekijken"),
            "computer_look" => L($"Kijken in {app}"),
            "computer_click" => L($"Klik in {app}"),
            "computer_type" => L($"Typen in {app}"),
            _ => L($"Toetsen in {app}: {AgentTools.Str(a, "keys")}"),
        };
    }

    // programs

    /// <summary>The name a person knows a program by: its description ("Kladblok"), else its file name.</summary>
    public static string NameOf(string path)
    {
        try
        {
            if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d) return d.Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private string AppName(string idOrName) => Resolve(idOrName) is { } path ? NameOf(path) : idOrName;

    /// <summary>An allowed program's path from its path, its file name or its name.</summary>
    public string? Resolve(string idOrName)
    {
        var want = idOrName.Trim();
        if (want.Length == 0) return null;
        return Allowed.Find(p => p.Equals(want, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(p).Equals(want, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(p).Equals(want, StringComparison.OrdinalIgnoreCase)
            || NameOf(p).Equals(want, StringComparison.OrdinalIgnoreCase));
    }

    // the UI Automation tree

    public sealed record Item(int N, string Role, string Title, string Value, bool Enabled, bool Secure)
    {
        public bool IsText => Role is "Edit" or "Document" or "ComboBox";
    }
    private readonly Dictionary<string, Dictionary<int, (Item Item, AutomationElement Element)>> items = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The kinds worth showing: what can be pressed or typed in, and the texts that say where one is.</summary>
    private static readonly HashSet<ControlType> Shown =
    [
        ControlType.Button, ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox, ControlType.Edit, ControlType.Document,
        ControlType.Hyperlink, ControlType.ListItem, ControlType.MenuItem, ControlType.TabItem, ControlType.TreeItem, ControlType.DataItem,
        ControlType.Slider, ControlType.Spinner, ControlType.SplitButton, ControlType.Text, ControlType.Header, ControlType.HeaderItem, ControlType.Image,
    ];

    private static Process? Running(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                if (p.MainWindowHandle != 0 && string.Equals(p.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) return p;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        // a program that hands its window to another process of the same name (Notepad on Windows 11)
        return Process.GetProcessesByName(name).FirstOrDefault(p => p.MainWindowHandle != 0);
    }

    private static string RoleOf(ControlType t) => t.ProgrammaticName.Replace("ControlType.", "");

    private async Task<string> Look(string path)
    {
        var process = Running(path);
        if (process == null)
        {
            if (!File.Exists(path)) return "That program is not on this PC.";
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return "The program did not start: " + e.Message; }
            for (int i = 0; i < 20 && (process = Running(path)) == null; i++) await Task.Delay(250);
            await Task.Delay(800);
            process = Running(path);
        }
        if (process == null) return $"{NameOf(path)} has no window open.";
        return await Task.Run(() =>
        {
            var window = AutomationElement.FromHandle(process.MainWindowHandle);
            var found = new List<(Item, AutomationElement)>();
            var walker = TreeWalker.ControlViewWalker;
            void Walk(AutomationElement e, int depth)
            {
                if (depth >= 30 || found.Count >= 300) return;
                AutomationElement.AutomationElementInformation info;
                try { info = e.Current; } catch (ElementNotAvailableException) { return; }
                if (Shown.Contains(info.ControlType))
                {
                    var title = info.Name ?? "";
                    if (title.Length == 0) title = info.HelpText ?? "";
                    bool secure = info.IsPassword;
                    var value = "";
                    if (!secure && e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) value = ((ValuePattern)vp).Current.Value ?? "";
                    var role = RoleOf(info.ControlType);
                    // a text without words, an image without a name: nothing to go by
                    if (!((role == "Text" || role == "Image") && title.Length == 0 && value.Length == 0))
                        found.Add((new Item(found.Count + 1, role, TextLimit.Prefix(title.Trim(), 80), TextLimit.Prefix(value.Replace("\r", ""), 120), info.IsEnabled, secure), e));
                }
                for (var child = walker.GetFirstChild(e); child != null; child = walker.GetNextSibling(child)) Walk(child, depth + 1);
            }
            Walk(window, 0);
            lock (items) items[path] = found.ToDictionary(f => f.Item1.N);
            var lines = found.Select(f =>
            {
                var i = f.Item1;
                var line = $"[{i.N}] {i.Role}";
                if (i.Title.Length > 0) line += $" \"{i.Title}\"";
                if (i.Value.Length > 0) line += $" = \"{i.Value}\"";
                if (!i.Enabled) line += " (disabled)";
                if (i.Secure) line += " (password field: the user types here)";
                return line;
            });
            return $"{NameOf(path)}, window \"{window.Current.Name}\"\n" + string.Join("\n", lines);
        });
    }

    // checks

    private static readonly Regex RiskyWords = new(AgentTools.Risky, RegexOptions.Compiled);
    private static readonly string[] RiskyKeys = ["enter", "return", "ctrl+enter", "ctrl+return", "delete", "shift+delete", "ctrl+d", "alt+s"];

    public Task<AgentStep> Check(ToolCall call)
    {
        var a = WorkspaceTools.Args(call);
        if (call.Name == "computer_apps") return Task.FromResult<AgentStep>(new AgentStep.Run());
        if (Resolve(AgentTools.Str(a, "app")) is not { } path)
            return Task.FromResult<AgentStep>(new AgentStep.Refuse($"That program is not allowed. The allowed programs: {string.Join(", ", Allowed.Select(NameOf))}. The user adds programs in Settings → Agent."));
        if (call.Name == "computer_look") return Task.FromResult<AgentStep>(new AgentStep.Run());
        var name = NameOf(path);
        if (call.Name == "computer_key")
        {
            var keys = AgentTools.Str(a, "keys").ToLowerInvariant().Replace(" ", "");
            if (KeyCode(keys) == null) return Task.FromResult<AgentStep>(new AgentStep.Refuse($"Error: unknown or not allowed keys '{keys}'. Use e.g. enter, escape, tab, ctrl+s. Never alt+f4 or the Windows key."));
            bool risky = RiskyKeys.Contains(keys);
            return Task.FromResult<AgentStep>(new AgentStep.Ask(L($"Toetsen in {name}: {keys}"), keys, risky ? [L("Deze toets kan iets versturen of verwijderen.")] : [], risky));
        }
        (Item Item, AutomationElement Element) found;
        lock (items)
            if (!items.TryGetValue(path, out var list) || !list.TryGetValue(AgentTools.Int(a, "element"), out found))
                return Task.FromResult<AgentStep>(new AgentStep.Refuse("There is no element with that number. Use computer_look first for the current numbers."));
        var item = found.Item;
        var what = item.Title.Length == 0 ? item.Role : $"\"{item.Title}\"";
        if (call.Name == "computer_type")
        {
            if (item.Secure) return Task.FromResult<AgentStep>(new AgentStep.Refuse("That is a password field. You never type passwords: ask the user to type it themselves."));
            if (!item.IsText) return Task.FromResult<AgentStep>(new AgentStep.Refuse($"Element {item.N} is no text field ({item.Role})."));
            return Task.FromResult<AgentStep>(new AgentStep.Ask(L($"Typen in {name}: {what}"), AgentTools.Str(a, "text")));
        }
        bool riskyButton = RiskyWords.IsMatch(item.Title);
        return Task.FromResult<AgentStep>(new AgentStep.Ask(L($"Klik in {name}: {what}"), item.Role + (item.Value.Length == 0 ? "" : " = " + item.Value),
            riskyButton ? [L("Deze knop verstuurt, koopt, plaatst of verwijdert misschien iets.")] : [], riskyButton));
    }

    // running

    public async Task<string> Run(ToolCall call, CancellationToken ct)
    {
        var a = WorkspaceTools.Args(call);
        if (call.Name == "computer_apps")
        {
            if (Allowed.Count == 0) return "The user has not allowed any program yet. They add programs in Settings → Agent.";
            return string.Join("\n", Allowed.Select(p => $"{NameOf(p)} ({p}){(Running(p) != null ? ", running" : "")}"));
        }
        if (Resolve(AgentTools.Str(a, "app")) is not { } path) return "That program is not allowed.";
        if (call.Name == "computer_look") return await Look(path);
        if (call.Name == "computer_key")
        {
            if (Running(path) is not { } process || KeyCode(AgentTools.Str(a, "keys").ToLowerInvariant().Replace(" ", "")) is not { } key)
                return "The program does not run, or the keys are unknown.";
            Front(process.MainWindowHandle);
            await Task.Delay(300);
            Press(key.Vk, key.Modifiers);
            await Task.Delay(500);
            return $"Pressed {AgentTools.Str(a, "keys")}. Use computer_look to see the result.";
        }
        (Item Item, AutomationElement Element) found;
        lock (items)
            if (!items.TryGetValue(path, out var list) || !list.TryGetValue(AgentTools.Int(a, "element"), out found))
                return "The element is gone. Use computer_look.";
        var (item, element) = found;
        return await Task.Run(async () =>
        {
            try
            {
                if (call.Name == "computer_type")
                {
                    var text = AgentTools.Str(a, "text");
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && !((ValuePattern)vp).Current.IsReadOnly)
                    {
                        ((ValuePattern)vp).SetValue(text);
                    }
                    else
                    {
                        // a document without a value to set: put the cursor there and type
                        if (Running(path) is { } p) Front(p.MainWindowHandle);
                        element.SetFocus();
                        await Task.Delay(200);
                        TypeText(text);
                    }
                    return $"Typed into {(item.Title.Length == 0 ? $"field {item.N}" : item.Title)}. Use computer_look to check.";
                }
                // a text field has no press: a click there puts the cursor in it
                if (item.IsText || item.Secure)
                {
                    if (Running(path) is { } p) Front(p.MainWindowHandle);
                    element.SetFocus();
                    return $"The cursor is in {(item.Title.Length == 0 ? $"field {item.N}" : item.Title)} now.";
                }
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
                else if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand)) ((ExpandCollapsePattern)expand).Expand();
                else if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) ((TogglePattern)toggle).Toggle();
                else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select)) ((SelectionItemPattern)select).Select();
                else return "It could not be pressed (it has no action); try another element or computer_key.";
                await Task.Delay(600);
                return "Pressed. Use computer_look to see the result.";
            }
            catch (Exception e) when (e is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException or COMException)
            {
                return "That did not work: " + e.Message + " Use computer_look for the current state.";
            }
        });
    }

    // keys

    [Flags] public enum Mods { None = 0, Ctrl = 1, Shift = 2, Alt = 4 }

    public static readonly IReadOnlyDictionary<string, ushort> Codes = BuildCodes();

    private static Dictionary<string, ushort> BuildCodes()
    {
        var d = new Dictionary<string, ushort>
        {
            ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
            ["escape"] = 0x1B, ["esc"] = 0x1B, ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
            ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22, ["insert"] = 0x2D,
            [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF, ["-"] = 0xBD, ["="] = 0xBB,
        };
        for (char c = 'a'; c <= 'z'; c++) d[c.ToString()] = (ushort)char.ToUpperInvariant(c);
        for (char c = '0'; c <= '9'; c++) d[c.ToString()] = c;
        for (int f = 1; f <= 12; f++) d["f" + f] = (ushort)(0x70 + f - 1);
        return d;
    }

    /// <summary>"ctrl+shift+n" → the key and the modifiers; null for a key it does not know, the Windows key, or alt+f4.</summary>
    public static (ushort Vk, Mods Modifiers)? KeyCode(string keys)
    {
        var parts = keys.Replace(" ", "").ToLowerInvariant().Split('+').ToList();
        if (parts.Count == 0 || !Codes.TryGetValue(parts[^1], out var code)) return null;
        var mods = Mods.None;
        foreach (var m in parts.Take(parts.Count - 1))
        {
            switch (m)
            {
                case "ctrl" or "control": mods |= Mods.Ctrl; break;
                case "shift": mods |= Mods.Shift; break;
                case "alt": mods |= Mods.Alt; break;
                default: return null;   // the Windows key opens Start and Run: outside the allowed program
            }
        }
        // closing a program or signing out is not something to do for the user
        if (mods.HasFlag(Mods.Alt) && parts[^1] == "f4") return null;
        return (code, mods);
    }

    /// <summary>INPUT on x64: the type, then the union (as large as MOUSEINPUT), 40 bytes in all.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint Extra;
    }

    private const uint InputKeyboard = 1, KeyUp = 0x0002, Unicode = 0x0004;

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);

    private static Input Key(ushort vk, bool up, ushort scan = 0, uint flags = 0) =>
        new() { Type = InputKeyboard, Ki = new KeyboardInput { Vk = vk, Scan = scan, Flags = flags | (up ? KeyUp : 0) } };

    private static void Front(nint hwnd)
    {
        if (hwnd == 0) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);   // SW_RESTORE
        SetForegroundWindow(hwnd);
        try { AutomationElement.FromHandle(hwnd).SetFocus(); } catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException) { }
    }

    private static void Press(ushort vk, Mods mods)
    {
        var mod = new List<ushort>();
        if (mods.HasFlag(Mods.Ctrl)) mod.Add(0x11);
        if (mods.HasFlag(Mods.Shift)) mod.Add(0x10);
        if (mods.HasFlag(Mods.Alt)) mod.Add(0x12);
        var seq = mod.Select(m => Key(m, false)).Append(Key(vk, false)).Append(Key(vk, true)).Concat(Enumerable.Reverse(mod).Select(m => Key(m, true))).ToArray();
        SendInput((uint)seq.Length, seq, Marshal.SizeOf<Input>());
    }

    private static void TypeText(string text)
    {
        var seq = text.Replace("\r\n", "\n").SelectMany(c => c == '\n'
            ? new[] { Key(0x0D, false), Key(0x0D, true) }
            : new[] { Key(0, false, c, Unicode), Key(0, true, c, Unicode) }).ToArray();
        if (seq.Length > 0) SendInput((uint)seq.Length, seq, Marshal.SizeOf<Input>());
    }
}
