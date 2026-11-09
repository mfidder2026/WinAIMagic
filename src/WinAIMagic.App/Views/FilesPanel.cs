using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;
using IOPath = System.IO.Path;

namespace WinAIMagic.App.Views;

/// <summary>The working folder beside the chat: its files as a tree, and the text of the one clicked, read only. A developer
/// view the owner asked for, to see the source the model writes; a button in the chat's top bar turns it on and off. It follows
/// the folder: what the model writes shows up within a second, also in the file that is open.</summary>
public sealed partial class FilesPanel : Grid
{
    private readonly TextBlock title = Ui.Label("", 14, null, FontWeights.SemiBold);
    private readonly StackPanel tree = new();
    private readonly ScrollViewer treeScroll;
    private readonly TextBlock fileName = Ui.Label("", 13, null, FontWeights.SemiBold);
    private readonly TextBlock fileNote = Ui.Label("", 12, Theme.Secondary);
    private readonly TextBox text = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = Theme.Mono, FontSize = 12,
        BorderThickness = new Thickness(0), Padding = new Thickness(12, 8, 12, 8),
    };
    private readonly Grid viewer;
    private readonly RowDefinition viewerRow = new() { Height = new GridLength(0) };
    private readonly HashSet<string> open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer settle;
    private FileSystemWatcher? watcher;
    private string? folder, shownFile;
    private bool dragging;
    private double dragFrom, widthFrom;

    public const double Narrowest = 240;

    public FilesPanel()
    {
        Width = Math.Clamp(Settings.Shared.FilesPanelWidth, Narrowest, 1200);
        Background = Theme.Solid(Colors.Black, 0.12);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            RowDefinitions.Add(new RowDefinition { Height = h });
        RowDefinitions.Add(viewerRow);

        // the edge on the left: drag it to make the panel wider or narrower
        var edge = new Border { Background = new SolidColorBrush(Colors.Transparent), BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1, 0, 0, 0) };
        edge.PointerPressed += (_, e) =>
        {
            dragging = edge.CapturePointer(e.Pointer);
            dragFrom = e.GetCurrentPoint(null).Position.X;
            widthFrom = Width;
        };
        edge.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            double max = XamlRoot is { } r ? Math.Max(Narrowest, r.Size.Width - 520) : 1200;
            Width = Math.Clamp(widthFrom + dragFrom - e.GetCurrentPoint(null).Position.X, Narrowest, max);
        };
        edge.PointerReleased += (_, e) => EndDrag(edge, e);
        edge.PointerCaptureLost += (_, _) => { if (dragging) { dragging = false; Settings.Shared.FilesPanelWidth = Width; } };
        edge.PointerEntered += (_, _) => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        edge.PointerExited += (_, _) => { if (!dragging) ProtectedCursor = null; };
        Grid.SetRowSpan(edge, 4);
        Children.Add(edge);

        var head = Ui.Row("auto,*,auto,auto", 6, Ui.Icon(Glyph.Folder, 14, new SolidColorBrush(Theme.Amber)), title,
            Ui.IconBtn(Glyph.Refresh, Reload, L("Vernieuwen")),
            Ui.IconBtn(Glyph.Folder, () => { if (folder != null) Composer.OpenFolder(folder); }, L("Open in de Verkenner")));
        head.Padding = new Thickness(12, 6, 6, 6);
        Add(head, 0);
        Add(Ui.Divider(), 1);

        tree.Padding = new Thickness(4, 4, 4, 12);
        treeScroll = new ScrollViewer { Content = tree, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Add(treeScroll, 2);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(text, L("Inhoud van het bestand"));
        foreach (var part in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled" })
            text.Resources[part] = Theme.Solid(Colors.Black, 0.18);
        ScrollViewer.SetHorizontalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        var fileHead = Ui.Row("auto,*,auto,auto", 6, Ui.Icon(Glyph.Document, 13, Theme.Secondary), Ui.V(0, fileName, fileNote),
            Ui.IconBtn(Glyph.Folder, () => { if (shownFile != null) DocUi.Reveal(shownFile); }, L("Toon in Verkenner")),
            Ui.IconBtn(Glyph.Close, CloseFile, L("Sluit het bestand")));
        fileHead.Padding = new Thickness(12, 6, 6, 6);
        viewer = new Grid { BorderBrush = Theme.Hairline, BorderThickness = new Thickness(0, 1, 0, 0) };
        viewer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        viewer.RowDefinitions.Add(new RowDefinition());
        viewer.Children.Add(fileHead);
        viewer.Children.Add(text.At(1));
        viewer.Visibility = Visibility.Collapsed;
        Add(viewer, 3);

        settle = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        settle.Interval = TimeSpan.FromMilliseconds(400);
        settle.IsRepeating = false;
        settle.Tick += (_, _) => Reload();
        Loaded += (_, _) => Watch();
        Unloaded += (_, _) => StopWatching();
    }

    private void Add(FrameworkElement e, int row)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, 1);
        Children.Add(e);
    }

    private void EndDrag(UIElement edge, PointerRoutedEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        edge.ReleasePointerCapture(e.Pointer);
        ProtectedCursor = null;
        Settings.Shared.FilesPanelWidth = Width;
    }

    /// <summary>The folder of the conversation on screen; another folder starts with all closed.</summary>
    public void SetFolder(string? path)
    {
        if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)) return;
        folder = path;
        open.Clear();
        CloseFile();
        title.Text = path == null ? "" : IOPath.GetFileName(IOPath.TrimEndingDirectorySeparator(path));
        ToolTipService.SetToolTip(title, path);
        Watch();
        Reload();
    }

    private void Watch()
    {
        StopWatching();
        if (folder == null || !IsLoaded || !Directory.Exists(folder)) return;
        try
        {
            watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, e) => Changed(e.FullPath);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, e) => Changed(e.FullPath);
            watcher.Error += (_, _) => DispatcherQueue.TryEnqueue(Restart);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { App.Log(e); }
    }

    private void StopWatching()
    {
        watcher?.Dispose();
        watcher = null;
    }

    private void Changed(string path)
    {
        if (path.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase) || path.EndsWith(@"\.git", StringComparison.OrdinalIgnoreCase)) return;
        // many changes in a row (a build, a write in pieces): one redraw when it is quiet
        DispatcherQueue.TryEnqueue(() => { settle.Stop(); settle.Start(); });
    }

    private void Restart()
    {
        Watch();
        Reload();
    }

    private void Reload()
    {
        tree.Children.Clear();
        if (folder == null) return;
        if (!Directory.Exists(folder))
        {
            tree.Children.Add(Note(L("Deze map bestaat niet meer."), 0));
            return;
        }
        open.RemoveWhere(p => !Directory.Exists(p));
        int rows = 0;
        AddFolder(folder, 0, ref rows);
        if (rows == 0) tree.Children.Add(Note(L("De map is leeg."), 0));
        if (shownFile != null)
        {
            if (File.Exists(shownFile)) ShowFile(shownFile, keepPlace: true);
            else CloseFile();
        }
    }

    private void AddFolder(string path, int depth, ref int rows)
    {
        var (entries, more) = WorkspaceFiles.List(path);
        foreach (var e in entries)
        {
            if (rows >= 3000) return;   // a folder with a tree of node_modules: the rest stays out
            tree.Children.Add(Row(e, depth));
            rows++;
            if (e.IsFolder && open.Contains(e.Path)) AddFolder(e.Path, depth + 1, ref rows);
        }
        if (more > 0) tree.Children.Add(Note(L($"en nog {more}"), depth));
    }

    private Button Row(WorkspaceFiles.Entry e, int depth)
    {
        bool isOpen = e.IsFolder && open.Contains(e.Path);
        bool chosen = !e.IsFolder && string.Equals(e.Path, shownFile, StringComparison.OrdinalIgnoreCase);
        var chevron = Ui.Icon(e.IsFolder ? (isOpen ? Glyph.ChevronDown : Glyph.ChevronRight) : "", 10, Theme.Secondary);
        var icon = Ui.Icon(e.IsFolder ? Glyph.Folder : Icon(e.Name), 13, e.IsFolder ? new SolidColorBrush(Theme.Amber) : Theme.Secondary);
        var name = Ui.Label(e.Name, 13);
        var line = Ui.Row("14,18,*", 4, chevron, icon, name);
        line.Margin = new Thickness(depth * 14, 0, 0, 0);
        var b = ChatUi.Plain(line, () =>
        {
            if (e.IsFolder)
            {
                if (!open.Remove(e.Path)) open.Add(e.Path);
                double at = treeScroll.VerticalOffset;
                Reload();
                treeScroll.ChangeView(null, at, null, true);
            }
            else ShowFile(e.Path, keepPlace: false);
        }, e.IsFolder ? null : WorkspaceFiles.Size(e.Size));
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        b.Padding = new Thickness(6, 3, 6, 3);
        if (chosen) b.Background = Theme.Solid(Theme.Accent, 0.22);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, e.Name);
        if (e.IsFolder) Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(b, isOpen ? L("Uitgeklapte map") : L("Ingeklapte map"));
        return b;
    }

    private static string Icon(string name) => IOPath.GetExtension(name).ToLowerInvariant() switch
    {
        ".cs" or ".py" or ".js" or ".ts" or ".tsx" or ".jsx" or ".swift" or ".c" or ".h" or ".cpp" or ".java" or ".go" or ".rs" or ".bas"
            or ".ps1" or ".sh" or ".bat" or ".cmd" or ".html" or ".css" or ".xaml" or ".csproj" or ".sln" => Glyph.Code,
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" => Glyph.Image,
        _ => Glyph.Document,
    };

    private static TextBlock Note(string s, int depth)
    {
        var t = Ui.Label(s, 12, Theme.Secondary);
        t.Margin = new Thickness(depth * 14 + 30, 4, 6, 4);
        return t;
    }

    private void ShowFile(string path, bool keepPlace)
    {
        bool same = string.Equals(path, shownFile, StringComparison.OrdinalIgnoreCase);
        shownFile = path;
        var content = WorkspaceFiles.Read(path);
        fileName.Text = IOPath.GetFileName(path);
        ToolTipService.SetToolTip(fileName, IOPath.GetRelativePath(folder ?? "", path));
        long size = 0;
        try { size = new FileInfo(path).Length; } catch (IOException) { }
        fileNote.Text = content.Note ?? WorkspaceFiles.Size(size);
        var newText = content.Text ?? "";
        if (text.Text != newText)
        {
            // the model rewrote the file that is open: stay where the reader was
            var place = keepPlace && same ? (text.SelectionStart, Scroller(text)?.VerticalOffset) : (0, null);
            text.Text = newText;
            text.Select(Math.Min(place.Item1, newText.Length), 0);
            if (place.Item2 is { } offset) DispatcherQueue.TryEnqueue(() => Scroller(text)?.ChangeView(null, offset, null, true));
        }
        text.Visibility = content.Text == null ? Visibility.Collapsed : Visibility.Visible;
        viewer.Visibility = Visibility.Visible;
        viewerRow.Height = new GridLength(content.Text == null ? 0 : 1.4, content.Text == null ? GridUnitType.Auto : GridUnitType.Star);
        if (!keepPlace) MarkChosen();
    }

    private void MarkChosen()
    {
        double at = treeScroll.VerticalOffset;
        Reload();
        treeScroll.ChangeView(null, at, null, true);
    }

    private void CloseFile()
    {
        bool had = shownFile != null;
        shownFile = null;
        text.Text = "";
        viewer.Visibility = Visibility.Collapsed;
        viewerRow.Height = new GridLength(0);
        if (had) MarkChosen();
    }

    private static ScrollViewer? Scroller(DependencyObject o)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var c = VisualTreeHelper.GetChild(o, i);
            if (c is ScrollViewer s) return s;
            if (Scroller(c) is { } found) return found;
        }
        return null;
    }
}
