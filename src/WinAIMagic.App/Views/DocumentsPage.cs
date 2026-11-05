using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinAIMagic.Documents;
using WinAIMagic.Studio;
using static WinAIMagic.Core.Support.Lang;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using IOPath = System.IO.Path;

namespace WinAIMagic.App.Views;

/// <summary>The documents module: projects with attachments, a house style and documents. Say what you want made; the model reads the
/// attachments, searches the web for what they do not say, and the house style's layout comes last.</summary>
public sealed class DocumentsPage : IPage
{
    public FrameworkElement View { get; }
    private readonly AppState app;
    private readonly DocumentStudio studio;
    private readonly DocumentEngines engines;
    private readonly DocumentSidePanel side;
    private readonly DocumentProjectPane project;
    private readonly DocumentNewPane create;
    private readonly DocumentDraftPane draft;

    public DocumentsPage(AppState app)
    {
        this.app = app;
        studio = app.Studio;
        engines = app.DocumentEngines;
        side = new DocumentSidePanel(app);
        project = new DocumentProjectPane(app);
        create = new DocumentNewPane(app);
        draft = new DocumentDraftPane(app);

        var root = Ui.Row("270,1,*", 0, side.View, new Border { Background = Theme.Hairline },
            new Grid { Children = { project.View, create.View, draft.View } });
        View = root;
        app.Watch(root, side.UpdateEngines);
        root.Loaded += (_, _) =>
        {
            studio.Appeared();
            studio.PropertyChanged += OnStudio;
            engines.PropertyChanged += OnEngines;
            Update();
            draft.Sync();
        };
        root.Unloaded += (_, _) =>
        {
            studio.PropertyChanged -= OnStudio;
            engines.PropertyChanged -= OnEngines;
        };
    }

    /// <summary>Hidden test flags look at this.</summary>
    internal TextBox Editor => draft.Editor;

    private void OnEngines(object? _, PropertyChangedEventArgs e) => side.UpdateEngines();

    private void OnStudio(object? _, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentStudio.Text): draft.TextChanged(); break;
            case nameof(DocumentStudio.Selection): draft.SelectionChanged(); break;
            default: Update(); break;
        }
    }

    private void Update()
    {
        side.Update();
        project.View.Show(studio.Page == StudioPage.Project);
        create.View.Show(studio.Page == StudioPage.NewDocument);
        draft.View.Show(studio.Page == StudioPage.Draft);
        project.Update();
        create.Update();
        draft.Update();
    }
}

/// <summary>What the panes of the documents module share.</summary>
internal static class DocUi
{
    /// <summary>A button whose text stays readable when it cannot be used yet.</summary>
    public static Button Action(string text, Action click, string? icon = null, bool prominent = true, string? tip = null)
    {
        var b = Ui.Btn(text, click, icon, prominent ? Theme.Accent : null);
        Brush off = Theme.Solid(Colors.White, 0.07), on = Theme.Solid(Colors.White, 0.62);
        b.Resources["AccentButtonBackgroundDisabled"] = b.Resources["ButtonBackgroundDisabled"] = off;
        b.Resources["AccentButtonForegroundDisabled"] = b.Resources["ButtonForegroundDisabled"] = on;
        return b.Tip(tip);
    }

    public static Button Danger(string text, Action click) => Ui.Btn(text, click, Glyph.Stop, Theme.Pink);

    /// <summary>A button in a list: all of its width takes clicks, the current one is coloured.</summary>
    public static Button Side(string title, string icon, Action click)
    {
        var b = new Button
        {
            Content = Ui.Row("auto,*", 7, Ui.Icon(icon, 13), Ui.Label(title, 13)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        b.Click += (_, _) => click();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, title);
        return b.Tip(title);
    }

    public static void Mark(Button b, bool active) => b.Background = active ? Theme.Solid(Theme.Accent, 0.28) : new SolidColorBrush(Colors.Transparent);

    /// <summary>A big field for text with several lines.</summary>
    public static TextBox LargeInput(string placeholder, double height)
    {
        var t = new TextBox
        {
            PlaceholderText = placeholder,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = height,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 10, 12, 10),
            FontFamily = Theme.Text,
            FontSize = 14,
            IsSpellCheckEnabled = false,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(t, ScrollBarVisibility.Auto);
        return t;
    }

    public static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); }
        catch (Win32Exception e) { App.Log(e); }
    }

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Win32Exception e) { App.Log(e); }
    }

    public static string Name(string path) => IOPath.GetFileName(IOPath.TrimEndingDirectorySeparator(path));

    public static string Style(DocumentStudio s) => s.HouseStyle is { } h ? Name(h) : L("Eenvoudig (ingebouwd)");

    public static async Task<IReadOnlyList<string>> PickFiles(AppState app, bool many, params string[] extensions)
    {
        try
        {
            return await Task.FromResult(NativePickers.Files(WinRT.Interop.WindowNative.GetWindowHandle(app.Window), many, extensions));
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            App.Log(e);
            return [];
        }
    }
}

/// <summary>The left column: the project, what to do, the documents in it and the two models.</summary>
internal sealed class DocumentSidePanel
{
    public FrameworkElement View { get; }
    private readonly AppState app;
    private readonly DocumentStudio studio;
    private readonly DocumentEngines engines;
    private readonly Button projectButton, makeButton, projectPage;
    private readonly TextBlock projectName = Ui.Label("", 14, null, FontWeights.SemiBold);
    private readonly StackPanel drafts = Ui.V(3);
    private readonly TextBlock noDrafts = Ui.Label(L("Nog geen documenten."), 13, Theme.Secondary);
    private readonly TextBlock heavyName = Ui.Label("", 13, null, FontWeights.Medium), heavyLine = Line(), fastName = Ui.Label("", 13, null, FontWeights.Medium), fastLine = Line(), oneModel = Line();
    private readonly Ellipse heavyDot = Dot(), fastDot = Dot(), imageDot = Dot();
    private readonly FrameworkElement fastRow;
    private readonly Button fastButton, heavyButton, imageButton;
    private readonly TextBlock imageName = Ui.Label("", 13, null, FontWeights.Medium), imageLine = Line();
    private IReadOnlyList<string>? shownDrafts;
    private string? shownSelected;
    private bool? shownBusy;

    public DocumentSidePanel(AppState app)
    {
        this.app = app;
        studio = app.Studio;
        engines = app.DocumentEngines;

        var head = Ui.V(2, Ui.Label(L("Documenten"), 26, null, FontWeights.Bold, display: true),
            Ui.Caption(L("Alle tekst blijft op deze pc. Naar internet gaan alleen zoekvragen en het ophalen van pagina's.")));

        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        menu.Opening += (_, _) => FillMenu(menu);
        projectButton = new Button
        {
            Content = Ui.Row("auto,*,auto", 7, Ui.Icon(Glyph.Folder, 14, Theme.Solid(Theme.Amber)), projectName, Ui.Icon(Glyph.ChevronDown, 10, Theme.Secondary)),
            Flyout = menu,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(8),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(projectButton, L("Project"));
        var projectPart = Ui.V(3, Ui.Label(L("Project"), 12, Theme.Secondary), projectButton);

        makeButton = DocUi.Action(L("Document voorbereiden"), () => studio.Page = StudioPage.NewDocument, Glyph.Star);
        makeButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        projectPage = DocUi.Side(L("Bijlagen, huisstijl en gegevens"), Glyph.Folder, () => studio.Page = StudioPage.Project);

        var top = Ui.V(14, head, projectPart, makeButton, projectPage, Ui.Label(L("Documenten in dit project"), 12, Theme.Secondary));
        var list = new ScrollViewer { Content = Ui.V(3, noDrafts, drafts), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        fastButton = DocUi.Action(L("Start nu"), ToggleFast, null, false);
        fastButton.HorizontalAlignment = HorizontalAlignment.Left;
        fastButton.Margin = new Thickness(24, 2, 0, 0);
        fastRow = Ui.V(3, ModelHead(Glyph.Bolt, L("Snel"), fastName, fastDot), fastLine, oneModel, fastButton);
        // the same row for heavy, fast and images: role, model, a dot that is green while it runs, a line and a button
        heavyButton = DocUi.Action(L("Start nu"), ToggleHeavy, null, false);
        heavyButton.HorizontalAlignment = HorizontalAlignment.Left;
        heavyButton.Margin = new Thickness(24, 2, 0, 0);
        imageButton = DocUi.Action(L("Stop"), () => app.Host.Images.Stop(), null, false);
        imageButton.HorizontalAlignment = HorizontalAlignment.Left;
        imageButton.Margin = new Thickness(24, 2, 0, 0);
        var models = Ui.V(8, Ui.Label(L("Modellen"), 12, Theme.Secondary),
            Ui.V(3, ModelHead(Glyph.Think, L("Zwaar"), heavyName, heavyDot), heavyLine, heavyButton), fastRow,
            Ui.V(3, ModelHead(Glyph.Image, L("Afbeeldingen"), imageName, imageDot), imageLine, imageButton));
        oneModel.Margin = new Thickness(24, 0, 0, 0);

        var grid = new Grid { Padding = new Thickness(16, 4, 16, 16), RowSpacing = 14 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(top.At(0));
        grid.Children.Add(list.At(1));
        grid.Children.Add(models.At(2));
        View = grid;
    }

    private static TextBlock Line()
    {
        var t = Ui.Caption();
        t.Margin = new Thickness(24, 0, 0, 0);
        t.MaxLines = 4;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    private static Ellipse Dot() => new() { Width = 7, Height = 7 };

    private static Grid ModelHead(string icon, string role, TextBlock name, Ellipse dot)
    {
        var mark = Ui.Icon(icon, 13, Theme.Solid(Theme.Cyan));
        mark.Width = 18;
        return Ui.Row("auto,auto,*,auto", 6, mark, Ui.Label(role, 13), name, dot);
    }

    public void Update()
    {
        projectName.SetIfChanged(DocUi.Name(studio.Folder));
        DocUi.Mark(projectPage, studio.Page == StudioPage.Project);
        bool busy = studio.Busy;
        if (projectButton.IsEnabled == busy) projectButton.IsEnabled = !busy;
        if (makeButton.IsEnabled == busy) makeButton.IsEnabled = !busy;

        var selected = studio.Page == StudioPage.Draft ? studio.Selected : null;
        if (ReferenceEquals(shownDrafts, studio.Drafts) && shownSelected == selected && shownBusy == busy) return;
        shownDrafts = studio.Drafts;
        shownSelected = selected;
        shownBusy = busy;
        noDrafts.Show(studio.Drafts.Count == 0);
        drafts.Children.Clear();
        foreach (var path in studio.Drafts)
        {
            var b = DocUi.Side(IOPath.GetFileNameWithoutExtension(path), Glyph.Document, () => studio.Open(path));
            b.IsEnabled = !busy;
            DocUi.Mark(b, path == selected);
            var menu = new MenuFlyout();
            var reveal = new MenuFlyoutItem { Text = L("Toon in Verkenner") };
            reveal.Click += (_, _) => DocUi.Reveal(path);
            var trash = new MenuFlyoutItem { Text = L("Naar de prullenbak") };
            trash.Click += (_, _) => studio.Delete(path);
            menu.Items.Add(reveal);
            menu.Items.Add(trash);
            b.ContextFlyout = menu;
            drafts.Children.Add(b);
        }
    }

    /// <summary>The second engine and its button, kept apart: they change without the studio knowing.</summary>
    public void UpdateEngines()
    {
        heavyName.SetIfChanged(app.Host.SelectedModel?.ShortName ?? L("Geen model"));
        heavyLine.SetIfChanged(engines.HeavyStatusLine);
        heavyDot.Fill = engines.HeavyIsRunning ? Theme.Solid(Theme.Mint) : Theme.Solid(Colors.White, 0.25);
        var state = app.EngineState;
        bool failed = state.Status == Engine.EngineStatus.Failed;
        bool heavyStops = state.IsActive && !failed;
        var heavyLabel = (TextBlock)((StackPanel)heavyButton.Content).Children[^1];
        heavyLabel.SetIfChanged(heavyStops ? L("Stop") : failed ? L("Probeer opnieuw") : L("Start nu"));
        // the name a screen reader says follows the label
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(heavyButton, heavyLabel.Text);
        heavyButton.IsEnabled = heavyStops ? !studio.Busy : app.Host.CanStart;

        var images = app.Host.Images;
        var imageModel = app.Host.ImageModel;
        bool imageReady = imageModel != null && app.Host.Store.StatusOf(imageModel.Id).IsComplete;
        imageName.SetIfChanged(imageModel?.ShortName ?? L("Geen model"));
        imageLine.SetIfChanged(images.Busy ? images.Status : imageReady ? L("Start vanzelf als er een afbeelding wordt gemaakt.") : L("Nog niet gedownload: zie Modellen."));
        imageDot.Fill = images.Busy ? Theme.Solid(Theme.Mint) : Theme.Solid(Colors.White, 0.25);
        imageButton.Show(images.Busy);

        var fast = engines.FastChoice;
        oneModel.Show(fast == null);
        oneModel.SetIfChanged(engines.OneModelLine);
        fastLine.Show(fast != null);
        fastButton.Show(fast != null);
        if (fast == null)
        {
            // one model does everything: the fast row names it, as the Mac does
            fastName.SetIfChanged(app.Host.SelectedModel?.ShortName ?? L("Geen model"));
            fastDot.Fill = heavyDot.Fill;
            return;
        }
        fastName.SetIfChanged(fast.ShortName);
        fastLine.SetIfChanged(engines.FastStatusLine);
        fastLine.Foreground = engines.FastProblem != null ? Theme.Solid(Theme.Amber) : Theme.Secondary;
        fastDot.Fill = engines.FastIsRunning ? Theme.Solid(Theme.Mint) : Theme.Solid(Colors.White, 0.25);
        var label = (TextBlock)((StackPanel)fastButton.Content).Children[^1];
        label.SetIfChanged(engines.FastButtonTitle);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fastButton, label.Text);
        fastButton.IsEnabled = !(engines.FastButtonStops && studio.Busy);
    }

    private void ToggleHeavy()
    {
        var state = app.EngineState;
        if (state.IsActive && state.Status != Engine.EngineStatus.Failed) app.StopEngine();
        else app.StartEngine();
    }

    private void ToggleFast()
    {
        if (engines.FastButtonStops) engines.StopFast();
        else _ = engines.StartFastNow();
    }

    private void FillMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        foreach (var path in studio.Projects)
        {
            var item = new ToggleMenuFlyoutItem { Text = DocUi.Name(path), IsChecked = string.Equals(IOPath.GetFullPath(path), IOPath.GetFullPath(studio.Folder), StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => studio.Choose(path);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        void Add(string text, Action click)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }
        Add(L("Nieuw project…"), () => _ = NewProject());
        Add(L("Open een bestaande map…"), () => _ = ChooseFolder());
        Add(L("Toon in Verkenner"), () => DocUi.Reveal(studio.Folder));
    }

    private async Task NewProject()
    {
        var name = new TextBox { PlaceholderText = L("Naam van het project"), Width = 340 };
        var dialog = Ui.Dialog(app);
        dialog.Title = L("Nieuw project");
        dialog.Content = name;
        dialog.PrimaryButtonText = L("Maak project");
        dialog.CloseButtonText = L("Annuleren");
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.IsPrimaryButtonEnabled = false;
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) studio.CreateProject(name.Text);
    }

    private async Task ChooseFolder()
    {
        try
        {
            if (NativePickers.Folder(WinRT.Interop.WindowNative.GetWindowHandle(app.Window)) is { } picked) studio.Choose(picked);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { App.Log(e); }
    }
}

/// <summary>What holds for all documents of the project: entered once.</summary>
internal sealed class DocumentProjectPane
{
    public FrameworkElement View { get; }
    private readonly AppState app;
    private readonly DocumentStudio studio;
    private readonly TextBlock title = Ui.Label("", 28, null, FontWeights.Bold, display: true);
    private readonly StackPanel attachments = Ui.V(8), fields = Ui.V(7);
    private readonly Grid styleRow = new();
    private readonly TextBox newField = new() { PlaceholderText = L("Nieuw veld"), Width = 170 };
    private readonly Button addField;
    private readonly DocumentStatusPanel status;
    private IReadOnlyList<string>? shownAttachments;
    private string? shownStyle = "?";
    private DocumentProject? shownProject;
    private readonly List<Action> unhook = [];

    public DocumentProjectPane(AppState app)
    {
        this.app = app;
        studio = app.Studio;
        status = new DocumentStatusPanel(app);

        var addAttachment = DocUi.Action(L("Voeg bijlagen toe…"), () => _ = ChooseAttachments(), Glyph.Add, false);
        addAttachment.HorizontalAlignment = HorizontalAlignment.Left;
        addField = DocUi.Action(L("Voeg toe"), AddField, null, false);
        addField.IsEnabled = false;
        newField.TextChanged += (_, _) => addField.IsEnabled = newField.Text.Trim().Length > 0;

        var page = Ui.Page(
            Ui.V(2, title, Ui.Label(L("Wat je hier zet, geldt voor elk document in dit project."), 14, Theme.Secondary)),
            Section(L("Bijlagen"), Glyph.Copy, Theme.Cyan, L("De context en informatie: wat een document moet bevatten, verslagen, afspraken. Het model kijkt hier als eerste."), Ui.V(8, attachments, addAttachment)),
            Section(L("Huisstijl"), Glyph.Image, Theme.Amber, L("Alleen de opmaak: voorblad, kop- en voettekst, koppen en tabellen. Die wordt als laatste op het document toegepast."), styleRow),
            Section(L("Gegevens voor het voorblad"), Glyph.Bullets, Theme.Violet, L("Een veld dat hier is ingevuld, hoef je nooit meer in te vullen."),
                Ui.V(7, fields, Ui.Row("auto,auto,*", 10, newField, addField))),
            status.View);
        var body = (StackPanel)page.Content;
        body.MaxWidth = 820 + 2 * Theme.PagePadding;
        body.HorizontalAlignment = HorizontalAlignment.Left;
        View = page;
    }

    private static Border Section(string name, string icon, Windows.UI.Color glow, string detail, UIElement content) =>
        Ui.Card(Ui.V(10, Ui.Caption(detail, size: 13), content), name, icon, glow);

    public void Update()
    {
        title.SetIfChanged(DocUi.Name(studio.Folder));
        status.Update();
        if (!ReferenceEquals(shownAttachments, studio.Attachments))
        {
            shownAttachments = studio.Attachments;
            attachments.Children.Clear();
            foreach (var path in studio.Attachments)
                attachments.Children.Add(Ui.Row("auto,*,auto", 8, Ui.Icon(Glyph.Document, 13, Theme.Secondary), Ui.Label(DocUi.Name(path), 13).Tip(path),
                    Ui.IconBtn(Glyph.Close, () => studio.RemoveAttachment(path), L("Verwijderen"))));
        }
        if (shownStyle != studio.HouseStyle)
        {
            shownStyle = studio.HouseStyle;
            styleRow.Children.Clear();
            var choose = DocUi.Action(studio.HouseStyle == null ? L("Kies een Word-bestand…") : L("Vervang…"), () => _ = ChooseStyle(), null, false);
            styleRow.Children.Add(studio.HouseStyle is { } style
                ? Ui.Row("auto,auto,auto,*", 10, Ui.Icon(Glyph.Document, 14, Theme.Solid(Theme.Amber)), Ui.Label(DocUi.Name(style), 13, null, FontWeights.SemiBold).Tip(style), choose)
                : Ui.Row("auto,auto,*", 10, Ui.Label(L("Eenvoudig (ingebouwd)"), 14, Theme.Secondary), choose));
        }
        if (!ReferenceEquals(shownProject, studio.Project))
        {
            if (shownProject != null) shownProject.Fields.CollectionChanged -= OnFields;
            shownProject = studio.Project;
            shownProject.Fields.CollectionChanged += OnFields;
            BuildFields();
        }
    }

    private void OnFields(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => BuildFields();

    private void BuildFields()
    {
        foreach (var u in unhook) u();
        unhook.Clear();
        fields.Children.Clear();
        var project = studio.Project;
        if (project.Fields.Count == 0)
            fields.Children.Add(Ui.Label(L("Nog geen velden. Ze komen vanzelf uit de huisstijl."), 13, Theme.Secondary));
        foreach (var field in project.Fields)
        {
            var box = new TextBox { Text = field.Value };
            box.TextChanged += (_, _) => field.Value = box.Text;
            PropertyChangedEventHandler changed = (_, _) => { if (box.Text != field.Value) box.Text = field.Value; };
            field.PropertyChanged += changed;
            unhook.Add(() => field.PropertyChanged -= changed);
            var key = Ui.Label(field.Key, 13).Tip(field.Key);
            key.TextAlignment = TextAlignment.Right;
            fields.Children.Add(Ui.Row("170,*,auto", 10, key, box, Ui.IconBtn(Glyph.Close, () => project.Fields.Remove(field), L("Verwijderen"))));
        }
    }

    private void AddField()
    {
        studio.Project.Fields.Add(new ProjectField(newField.Text.Trim(), ""));
        newField.Text = "";
    }

    private async Task ChooseStyle()
    {
        foreach (var f in await DocUi.PickFiles(app, false, ".docx", ".dotx")) studio.UseHouseStyle(f);
    }

    private async Task ChooseAttachments()
    {
        foreach (var f in await DocUi.PickFiles(app, true, ".docx", ".dotx", ".pdf", ".txt", ".md")) studio.AddAttachment(f);
    }
}

/// <summary>Say what you want made.</summary>
internal sealed class DocumentNewPane
{
    public FrameworkElement View { get; }
    private readonly DocumentStudio studio;
    private readonly TextBox request = DocUi.LargeInput(L("Bijvoorbeeld: maak een AQS voor deze applicatie, of: welke eisen stelt de BIO aan multi-factor authenticatie?"), 220);
    private readonly TextBlock routeAttachments = RouteText(), routeStyle = RouteText();
    private readonly ToggleSwitch useWeb = new() { IsOn = true, OnContent = "", OffContent = "", MinWidth = 0 };
    private readonly Button make, blank, stop;
    private readonly DocumentStatusPanel status;

    public DocumentNewPane(AppState app)
    {
        studio = app.Studio;
        status = new DocumentStatusPanel(app);
        make = DocUi.Action(L("Maak het document"), () =>
        {
            studio.Make(request.Text, useWeb.IsOn);
            request.Text = "";
        }, Glyph.Star);
        blank = DocUi.Action(L("Leeg document: zelf schrijven"), () =>
        {
            studio.NewDocument(IsEmpty ? L("Nieuw document") : DocumentStudio.FileName(request.Text));
            request.Text = "";
        }, null, false);
        stop = DocUi.Danger(L("Stop"), studio.Stop);
        request.TextChanged += (_, _) => Update();

        var web = Ui.Row("*,auto", 12,
            Ui.V(1, Ui.Label(L("Op internet zoeken als de bijlagen niet genoeg zeggen"), 13, null, FontWeights.SemiBold, wrap: true),
                Ui.Caption(L("Bijvoorbeeld over het onderwerp en regelgeving. Namen en gegevens van het project gaan niet mee."))), useWeb);

        var page = Ui.Page(
            Ui.Label(L("Wat wil je maken?"), 28, null, FontWeights.Bold, display: true),
            request,
            Ui.V(4, Route("1", routeAttachments), Route("2", RouteText(L("Internet: alleen voor wat de bijlagen niet zeggen."))),
                Route("3", RouteText(L("Concept in Markdown: daarin pas je alle tekst aan."))), Route("4", routeStyle)),
            web,
            Ui.Row("auto,auto,*,auto", 10, make, blank, null, stop),
            status.View);
        var body = (StackPanel)page.Content;
        body.MaxWidth = 820 + 2 * Theme.PagePadding;
        body.HorizontalAlignment = HorizontalAlignment.Left;
        View = page;
    }

    private bool IsEmpty => request.Text.Trim().Length == 0;

    private static TextBlock RouteText(string text = "") => Ui.Label(text, 13, Theme.Secondary, wrap: true);

    private static Grid Route(string number, TextBlock text)
    {
        var n = Ui.Label(number, 13, Theme.Solid(Theme.Cyan), FontWeights.Bold);
        n.VerticalAlignment = VerticalAlignment.Top;
        n.Width = 14;
        return Ui.Row("auto,*", 8, n, text);
    }

    public void Update()
    {
        string count = studio.Attachments.Count == 0 ? L("nog geen") : studio.Attachments.Count.ToString();
        routeAttachments.SetIfChanged(L($"Bijlagen: {count}. Hierin zoekt het model waaruit het document moet bestaan."));
        routeStyle.SetIfChanged(L($"Huisstijl als laatste: {DocUi.Style(studio)}."));
        make.IsEnabled = !IsEmpty && !studio.Busy;
        blank.IsEnabled = !studio.Busy;
        stop.Show(studio.Busy);
        status.Update();
    }
}

/// <summary>A draft: the text, what is happening to it, and what you can ask of it.</summary>
internal sealed partial class DocumentDraftPane
{
    public FrameworkElement View { get; }
    public TextBox Editor { get; }
    private readonly AppState app;
    private readonly DocumentStudio studio;
    private readonly TextBlock title = Ui.Bold("", 14), style = Ui.Label("", 12, Theme.Secondary), scope = Ui.Label("", 13, null, FontWeights.SemiBold);
    private readonly Button undo, deliver, apply, writeAll, stop;
    private readonly List<Button> quick = [];
    private readonly ToggleButton markdownTab, previewTab;
    private readonly ScrollViewer previewScroll;
    private readonly MarkdownView preview = new();
    private readonly TextBox request = DocUi.LargeInput(L("Schrijf wat er in dit stuk moet veranderen…"), 96);
    private readonly DocumentStatusPanel status;
    private readonly Border statusLine = Ui.Divider();
    private readonly FrameworkElement statusPart;
    private readonly Grid toolbar;
    private readonly FrameworkElement toolbarTitle;
    private readonly WrapPanel toolbarRight;
    private readonly Grid quickRow;
    private readonly StackPanel quickButtons;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer pushTimer;
    private ScrollViewer? scroller;
    /// <summary>The text as the editor has it, with \n.</summary>
    private string shown = "";
    private bool isPreview, follow = true;
    private long lastPush;
    private double pushCost = 1;

    public DocumentDraftPane(AppState app)
    {
        this.app = app;
        studio = app.Studio;
        status = new DocumentStatusPanel(app);
        pushTimer = app.Ui.CreateTimer();
        pushTimer.IsRepeating = false;
        pushTimer.Tick += (_, _) => Push();

        Editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Theme.Mono,
            FontSize = 13,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            Padding = new Thickness(20, 16, 20, 16),
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(Editor, ScrollBarVisibility.Auto);
        Brush clear = new SolidColorBrush(Colors.Transparent);
        foreach (var part in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
            Editor.Resources[part] = clear;
        Editor.Resources["TextControlForegroundFocused"] = Theme.Primary;
        Editor.TextChanged += (_, _) => OnEdited();
        Editor.SelectionChanged += (_, _) =>
        {
            if (studio.Busy) return;
            studio.Selection = new NSRange(Editor.SelectionStart, Editor.SelectionLength);
            UpdateScope();
        };
        // a hand on the scrollbar or the wheel: the text stays where the user put it, until the writing is on screen again
        Editor.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => follow = false), true);
        Editor.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => follow = false), true);

        var previewBody = Ui.V(0, preview);
        previewBody.MaxWidth = 760;
        previewBody.Padding = new Thickness(24);
        previewBody.HorizontalAlignment = HorizontalAlignment.Center;
        previewScroll = new ScrollViewer { Content = previewBody, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };

        undo = DocUi.Action(L("Ongedaan maken"), studio.Undo, "", false, L("Ongedaan maken"));
        markdownTab = Tab(L("Markdown"), false, new CornerRadius(8, 0, 0, 8));
        previewTab = Tab(L("Voorbeeld"), true, new CornerRadius(0, 8, 8, 0));
        deliver = DocUi.Action(L("Goedkeuren en maak .docx"), studio.Deliver, Glyph.Document, true, L("Zet dit concept in de huisstijl van het project. Een eerder .docx-bestand blijft staan."));

        toolbarTitle = Ui.V(1, title, style);
        toolbarRight = new WrapPanel { Gap = 10, Children = { undo, Ui.H(0, markdownTab, previewTab), deliver } };
        quickRow = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
        quickRow.ColumnDefinitions.Add(new ColumnDefinition());
        quickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        quickRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        quickRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        toolbar = new Grid { Padding = new Thickness(16, 2, 16, 10), ColumnSpacing = 10, RowSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toolbar.Children.Add(toolbarTitle);
        toolbar.Children.Add(toolbarRight);

        foreach (var action in Enum.GetValues<QuickAction>())
            quick.Add(DocUi.Action(action.Title(), () => studio.Apply(action), Glyph.Bolt, false, L("Met het snelle model, op de selectie of anders op de sectie waar de cursor staat.")));
        stop = DocUi.Danger(L("Stop"), studio.Stop);
        apply = DocUi.Action(L("Pas aan"), () =>
        {
            studio.Apply(request.Text);
            request.Text = "";
        }, Glyph.Fixed, true, L("Met het zware model, op de selectie of anders op de sectie waar de cursor staat."));
        writeAll = DocUi.Action(L("Schrijf alle secties"), () =>
        {
            studio.WriteAllSections(request.Text);
            request.Text = "";
        }, Glyph.Bullets, false, L("Schrijft elke sectie onder de koppen van dit concept opnieuw, uit de bijlagen en deze opdracht."));
        apply.HorizontalAlignment = writeAll.HorizontalAlignment = HorizontalAlignment.Stretch;
        request.TextChanged += (_, _) => Update();

        var actions = Ui.V(8, apply, writeAll);
        actions.VerticalAlignment = VerticalAlignment.Bottom;
        quickButtons = Ui.H(8, quick[0], quick[1], quick[2], stop);
        quickRow.Children.Add(scope);
        quickRow.Children.Add(quickButtons);
        toolbar.SizeChanged += (_, e) => LayToolbar(e.NewSize.Width);
        LayToolbar(1000);
        var composer = Ui.V(8, quickRow, Ui.Row("*,200", 10, request, actions));
        composer.Padding = new Thickness(16, 12, 16, 12);

        var statusBody = status.View;
        statusBody.Margin = new Thickness(16, 8, 16, 8);
        statusPart = Ui.V(0, statusLine, statusBody);

        var grid = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        grid.Children.Add(toolbar.At(0));
        grid.Children.Add(Ui.Divider().At(1));
        grid.Children.Add(Editor.At(2));
        grid.Children.Add(previewScroll.At(2));
        grid.Children.Add(statusPart.At(3));
        grid.Children.Add(Ui.Divider().At(4));
        grid.Children.Add(composer.At(5));
        View = grid;
    }

    private ToggleButton Tab(string text, bool toPreview, CornerRadius corners)
    {
        var t = new ToggleButton { Content = text, FontSize = 13, MinWidth = 78, Padding = new Thickness(10, 6, 10, 6), CornerRadius = corners, IsChecked = !toPreview };
        t.Click += (_, _) => ShowPreview(toPreview);
        return t;
    }

    private void ShowPreview(bool on)
    {
        isPreview = on;
        markdownTab.IsChecked = !on;
        previewTab.IsChecked = on;
        Editor.Show(!on);
        previewScroll.Show(on);
        if (on) preview.Set(PreviewText);
    }

    /// <summary>The instructions in the draft stay out of the document, so out of its preview too.</summary>
    private string PreviewText => Comment().Replace(new DocumentDraft(studio.Text).Body, "");

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    /// <summary>On a narrow window the name gets a row of its own, so the buttons always fit.</summary>
    private void LayToolbar(double width)
    {
        bool narrow = width < 680;
        // the words of undo go first: its icon and tip remain
        ((StackPanel)undo.Content).Children[^1].Show(!narrow);
        Grid.SetColumnSpan(toolbarTitle, narrow ? 2 : 1);
        Grid.SetRow(toolbarRight, narrow ? 1 : 0);
        Grid.SetColumn(toolbarRight, narrow ? 0 : 1);
        Grid.SetColumnSpan(toolbarRight, narrow ? 2 : 1);
        toolbarRight.Gap = narrow ? 6 : 10;
        toolbarRight.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        toolbarRight.InvalidateMeasure();
        // and what an action works on stays readable
        Grid.SetColumnSpan(scope, narrow ? 2 : 1);
        Grid.SetRow(quickButtons, narrow ? 1 : 0);
        Grid.SetColumn(quickButtons, narrow ? 0 : 1);
        Grid.SetColumnSpan(quickButtons, narrow ? 2 : 1);
    }

    public void Update()
    {
        title.SetIfChanged(studio.Selected is { } s ? IOPath.GetFileNameWithoutExtension(s) : "");
        style.SetIfChanged(L($"Huisstijl: {DocUi.Style(studio)}"));
        bool busy = studio.Busy, chosen = studio.Selected != null;
        undo.Show(studio.Previous != null && !busy);
        deliver.IsEnabled = chosen && !busy;
        foreach (var q in quick) q.IsEnabled = !busy;
        stop.Show(busy);
        apply.IsEnabled = request.Text.Trim().Length > 0 && !busy;
        writeAll.IsEnabled = !busy;
        if (Editor.IsReadOnly != busy)
        {
            Editor.IsReadOnly = busy;
            if (busy) follow = true;
            else Flush();
        }
        statusPart.Show(studio.Steps.Count > 0 || studio.Problem != null || studio.Notes.Count > 0 || studio.Report != null);
        status.Update();
        UpdateScope();
    }

    private void UpdateScope()
    {
        if (!studio.Busy) scope.SetIfChanged(L($"Op {studio.ScopeTitle}:"));
    }

    // MARK: - the text between the studio and the editor

    private static string Clean(string text) => text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    /// <summary>Typed: on to the studio. A WinUI TextBox says so later and with \r; while a model writes, the studio has the last word.</summary>
    private void OnEdited()
    {
        if (studio.Busy) return;
        var now = Clean(Editor.Text);
        if (now == shown) return;
        shown = now;
        studio.Text = now;
        UpdateScope();
    }

    /// <summary>When the page comes back.</summary>
    public void Sync()
    {
        Flush();
        SelectionChanged();
    }

    public void TextChanged()
    {
        if (studio.Text == shown) return;
        if (!studio.Busy)
        {
            Flush();
            UpdateScope();
            return;
        }
        // a long document costs more per update, so it gets fewer of them: the screen keeps reacting
        long wait = (long)Math.Max(100, pushCost * 3) - (Environment.TickCount64 - lastPush);
        if (wait <= 0) Push();
        else if (!pushTimer.IsRunning)
        {
            pushTimer.Interval = TimeSpan.FromMilliseconds(wait);
            pushTimer.Start();
        }
    }

    public void SelectionChanged()
    {
        var s = studio.Selection;
        if (studio.Busy || s.Location + s.Length > Editor.Text.Length) return;
        if (Editor.SelectionStart != s.Location || Editor.SelectionLength != s.Length) Editor.Select(s.Location, s.Length);
        UpdateScope();
    }

    /// <summary>The whole text at once: another document, undo, the end of the work.</summary>
    private void Flush()
    {
        pushTimer.Stop();
        var target = studio.Text;
        if (target != shown || Clean(Editor.Text) != target)
        {
            shown = target;
            Editor.Text = target;
        }
        if (isPreview) preview.Set(PreviewText);
    }

    /// <summary>While a model writes only what changed goes in, so that the place on screen stays and a long document stays fast.</summary>
    private void Push()
    {
        var a = shown;
        var b = studio.Text;
        if (a == b) return;
        var clock = Stopwatch.StartNew();
        int max = Math.Min(a.Length, b.Length), head = 0, tail = 0;
        while (head < max && a[head] == b[head]) head++;
        while (tail < max - head && a[a.Length - 1 - tail] == b[b.Length - 1 - tail]) tail++;
        if (head > 0 && head < b.Length && char.IsLowSurrogate(b[head])) head--;
        if (tail > 0 && char.IsHighSurrogate(b[b.Length - tail - 1])) tail--;
        shown = b;
        Editor.IsReadOnly = false;
        Editor.Select(head, a.Length - head - tail);
        Editor.SelectedText = b.Substring(head, b.Length - head - tail);
        Editor.IsReadOnly = studio.Busy;
        if (isPreview) preview.Set(PreviewText);
        else Follow(b.Length - tail);
        lastPush = Environment.TickCount64;
        pushCost = clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>Live following: the place where the model writes stays on screen.</summary>
    private void Follow(int at)
    {
        scroller ??= Find<ScrollViewer>(Editor);
        if (scroller == null) return;
        var spot = Editor.GetRectFromCharacterIndex(Math.Max(at - 1, 0), true);
        // in the coordinates of the whole text
        double top = spot.Top, height = scroller.ViewportHeight;
        bool visible = top >= scroller.VerticalOffset && spot.Bottom <= scroller.VerticalOffset + height;
        if (visible) follow = true;
        else if (follow) scroller.ChangeView(null, Math.Max(top - height * 0.7, 0), null, true);
    }

    private static T? Find<T>(DependencyObject parent) where T : class
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as T ?? Find<T>(child)) is { } found) return found;
        }
        return null;
    }
}

/// <summary>The steps of the running work, what went wrong, and the finished document. All of it scrolls in its own space, at most 92 + 110
/// high: what there is to report must never push the buttons out of the window.</summary>
internal sealed class DocumentStatusPanel
{
    public StackPanel View { get; }
    private readonly DocumentStudio studio;
    private readonly StackPanel steps = Ui.V(3), messages = Ui.V(4);
    private readonly ScrollViewer stepScroll, messageScroll;
    private IReadOnlyList<Step>? shownSteps;
    private (string? Problem, IReadOnlyList<string>? Notes, DocxCompose.Report? Report) shownMessages;
    private int shownCount;

    public DocumentStatusPanel(AppState app)
    {
        studio = app.Studio;
        stepScroll = new ScrollViewer { Content = steps, MaxHeight = 92, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        messageScroll = new ScrollViewer { Content = messages, MaxHeight = 110, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        View = Ui.V(4, stepScroll, messageScroll);
    }

    public void Update()
    {
        bool anySteps = studio.Steps.Count > 0, anyMessages = studio.Problem != null || studio.Notes.Count > 0 || studio.Report != null;
        View.Show(anySteps || anyMessages);
        stepScroll.Show(anySteps);
        messageScroll.Show(anyMessages);
        if (!ReferenceEquals(shownSteps, studio.Steps))
        {
            shownSteps = studio.Steps;
            steps.Children.Clear();
            foreach (var s in studio.Steps) steps.Children.Add(StepRow(s));
            if (studio.Steps.Count != shownCount)
            {
                shownCount = studio.Steps.Count;
                stepScroll.UpdateLayout();
                stepScroll.ChangeView(null, stepScroll.ScrollableHeight, null, true);
            }
        }
        var now = (studio.Problem, (IReadOnlyList<string>?)studio.Notes, studio.Report);
        if (now == shownMessages) return;
        shownMessages = now;
        messages.Children.Clear();
        if (studio.Problem is { } p) messages.Children.Add(Message(Glyph.Warning, p));
        foreach (var n in studio.Notes) messages.Children.Add(Message(Glyph.Eye, n));
        if (studio.Report is { } r) Result(r);
    }

    private static FrameworkElement StepRow(Step s)
    {
        FrameworkElement mark = s.State switch
        {
            StepState.Running => new ProgressRing { Width = 13, Height = 13, IsActive = true },
            StepState.Done => Ui.Icon(Glyph.Done, 13, Theme.Solid(Theme.Mint)),
            _ => Ui.Icon(Glyph.Blocked, 13, Theme.Solid(Theme.Amber)),
        };
        mark.Width = 14;
        return Ui.Row("auto,*", 6, mark, Ui.Label(s.Text, 13).Tip(s.Text));
    }

    private static Grid Message(string icon, string text)
    {
        var mark = Ui.Icon(icon, 13, Theme.Solid(Theme.Amber));
        mark.VerticalAlignment = VerticalAlignment.Top;
        mark.Margin = new Thickness(0, 2, 0, 0);
        var line = Ui.Label(text, 13, Theme.Solid(Theme.Amber), wrap: true, select: true);
        return Ui.Row("auto,*", 6, mark, line);
    }

    private void Result(DocxCompose.Report r)
    {
        if (r.Url is { } url)
        {
            bool sound = r.Problems.Count == 0;
            messages.Children.Add(Ui.Row("auto,*,auto,auto", 8,
                Ui.Icon(sound ? Glyph.Done : Glyph.Warning, 14, Theme.Solid(sound ? Theme.Mint : Theme.Amber)),
                Ui.Label(DocUi.Name(url), 13, null, FontWeights.SemiBold).Tip(url),
                DocUi.Action(L("Open"), () => DocUi.Open(url), null, false),
                DocUi.Action(L("Toon in Verkenner"), () => DocUi.Reveal(url), null, false)));
        }
        foreach (var p in r.Problems) messages.Children.Add(Ui.Caption(p, Theme.Solid(Theme.Amber)));
        foreach (var w in r.Warnings) messages.Children.Add(Ui.Label(w, 13, Theme.Secondary, wrap: true));
    }
}
