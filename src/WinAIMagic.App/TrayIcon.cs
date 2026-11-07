using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinAIMagic.App;

/// <summary>The icon in the notification area while tasks or a heartbeat run with the window closed: what is going on, and the
/// way back in. Win32 underneath (WinUI has no tray icon): the window's own handle gets the icon's messages through a subclass,
/// also while the window is hidden.</summary>
public sealed class TrayIcon : IDisposable
{
    public sealed record Item(string Text, Action? Click = null, bool Enabled = true)
    {
        public static readonly Item Separator = new("-");
    }

    private const uint WmTray = 0x8000 + 77, WmContextMenu = 0x007B, WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, NinSelect = 0x0400, NinKeySelect = 0x0401;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10, NifShowTip = 0x80;
    private readonly IntPtr hwnd;
    private readonly SubclassProc proc;
    private readonly uint taskbarCreated;
    private IntPtr icon;
    private bool shown;
    private string tip = "WinAIMagic";

    /// <summary>The menu, made when it opens.</summary>
    public Func<List<Item>> Menu { get; set; } = () => [];
    /// <summary>A click on the icon.</summary>
    public Action? Activated { get; set; }

    public TrayIcon(IntPtr hwnd, string iconPath)
    {
        this.hwnd = hwnd;
        proc = WndProc;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (File.Exists(iconPath)) icon = LoadImage(IntPtr.Zero, iconPath, 1, 16, 16, 0x10);
        SetWindowSubclass(hwnd, proc, 77, IntPtr.Zero);
    }

    public bool Visible => shown;

    public void Show(string? text = null)
    {
        if (text != null) tip = text.Length > 120 ? text[..120] : text;
        var d = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        if (shown) { Shell_NotifyIcon(NimModify, ref d); return; }
        shown = Shell_NotifyIcon(NimAdd, ref d);
        d.uTimeoutOrVersion = 4;
        Shell_NotifyIcon(NimSetVersion, ref d);
    }

    public void Hide()
    {
        if (!shown) return;
        var d = Data(0);
        Shell_NotifyIcon(NimDelete, ref d);
        shown = false;
    }

    /// <summary>A notification from the icon, for when Windows' own notifications are not available.</summary>
    public void Balloon(string title, string text)
    {
        bool was = shown;
        if (!shown) Show();
        var d = Data(NifInfo);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.dwInfoFlags = 1;   // NIIF_INFO
        Shell_NotifyIcon(NimModify, ref d);
        _ = was;
    }

    private NotifyIconData Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(), hWnd = hwnd, uID = 1, uFlags = flags, uCallbackMessage = WmTray, hIcon = icon, szTip = tip,
        szInfo = "", szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l, IntPtr id, IntPtr data)
    {
        if (msg == WmTray)
        {
            uint ev = (uint)(l.ToInt64() & 0xFFFF);
            if (ev is WmLButtonUp or NinSelect or NinKeySelect) Activated?.Invoke();
            else if (ev is WmContextMenu or WmRButtonUp) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == taskbarCreated && shown)
        {
            // Explorer started again: the icon must be added again
            shown = false;
            Show();
        }
        return DefSubclassProc(h, msg, w, l);
    }

    private void ShowMenu()
    {
        var items = Menu();
        var menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (ReferenceEquals(items[i], Item.Separator) || items[i].Text == "-") AppendMenu(menu, 0x800, 0, null);
                else AppendMenu(menu, items[i].Enabled && items[i].Click != null ? 0u : 1u, (UIntPtr)(i + 1), items[i].Text);
            }
            GetCursorPos(out var p);
            SetForegroundWindow(hwnd);
            int chosen = TrackPopupMenuEx(menu, 0x0100 | 0x0002, p.X, p.Y, hwnd, IntPtr.Zero);   // TPM_RETURNCMD | TPM_RIGHTBUTTON
            PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= items.Count) items[chosen - 1].Click?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        Hide();
        RemoveWindowSubclass(hwnd, proc, 77);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        icon = IntPtr.Zero;
    }

    // MARK: - start with Windows

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "WinAIMagic";

    /// <summary>The app starts at sign-in, in the background (Instellingen → Taken; off by default, the owner's choice).</summary>
    public static bool StartsWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string v && v.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --background");
            else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
        }
    }

    // MARK: - Win32

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint id, IntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
