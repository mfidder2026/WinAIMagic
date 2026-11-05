using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using WinAIMagic.Engine;
using WinAIMagic.Studio;
using static WinAIMagic.Core.Support.Lang;
using Automation = Microsoft.UI.Xaml.Automation.AutomationProperties;

namespace WinAIMagic.App.Views;

/// <summary>Makes images from a description, on this PC. The model comes down here too; it is not one of the models the chat can choose.</summary>
public sealed class ImagePage : IPage
{
    private const int ThumbWidth = 132;

    public FrameworkElement View { get; }
    private readonly AppState app;
    /// <summary>The model chosen under Modellen, else the first.</summary>
    private ModelInfo? model => app.Host.ImageModel;

    private readonly StackPanel body;
    private readonly Border modelCard;
    private readonly StackPanel modelPart = Ui.V(10);
    private readonly TextBlock modelName = Ui.Label("", 18, null, FontWeights.Bold, display: true), modelSummary = Ui.Caption();
    private readonly ProgressBar downloadBar = new() { Minimum = 0, Maximum = 1 };
    private readonly TextBlock downloadText = Ui.Caption(), diskNote = Ui.Caption("", Theme.Solid(Theme.Amber)), downloadError = Ui.Caption("", Theme.Solid(Theme.Pink));
    private readonly Button download, pause, deleteModel;

    private readonly StackPanel makePart = Ui.V(12);
    private readonly TextBox prompt = DocUi.LargeInput(L("Bijvoorbeeld: een rode vos die de krant leest op een bankje in Amsterdam, herfstlicht, foto"), 96);
    private readonly ComboBox sizes;
    private readonly Button make, stop;
    private readonly TextBlock planNote = Ui.Caption(), engineNote = Ui.Caption("", Theme.Solid(Theme.Amber));
    private readonly StackPanel working = Ui.V(5);
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 1 };
    private readonly TextBlock step = Ui.Label("", 13, Theme.Secondary), error = Ui.Caption("", Theme.Solid(Theme.Pink)), note = Ui.Caption("", Theme.Solid(Theme.Amber));

    private readonly Border resultCard;
    private readonly Image picture = new() { Stretch = Stretch.Uniform, MaxHeight = 620, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock caption = Ui.Caption(), about = Ui.Label("", 14, wrap: true);

    private readonly Border galleryCard;
    private readonly WrapPanel gallery = new() { Gap = 8, RowGap = 8 };

    private ImageResult? shown;
    private string galleryKey = "?";
    private bool wasBusy;

    public ImagePage(AppState app)
    {
        this.app = app;

        var store = app.Host.Store;
        download = Ui.Btn(L("Download"), () => { if (model != null) store.StartDownload(model.Id); }, Glyph.Download, Theme.Violet);
        pause = Ui.Btn(L("Pauzeer"), () => { if (model != null) store.StopDownload(model.Id); }, Glyph.Pause);
        deleteModel = Ui.Btn(L("Verwijder model"), () => { if (model != null) _ = ModelCard.ConfirmDelete(app, model); }, Glyph.Trash);
        var modelHead = Ui.V(3, modelName, modelSummary);
        modelPart.Children.Add(modelHead);
        modelPart.Children.Add(Ui.V(5, downloadBar, downloadText));
        modelPart.Children.Add(diskNote);
        modelPart.Children.Add(downloadError);
        modelPart.Children.Add(Ui.H(8, download, pause, deleteModel));

        Automation.SetName(prompt, L("Beschrijving"));
        prompt.TextChanged += (_, _) => Update();
        var current = ChosenSize();
        sizes = SettingsDialog.Pick(ImagePlanner.Sizes.Select(s => (SizeLabel(s), s.Key)), current.Key, v =>
        {
            app.Settings.ImageSize = v;
            Update();
        });
        Automation.SetName(sizes, L("Grootte"));
        make = Ui.Btn(L("Maak afbeelding"), Make, Glyph.Image, Theme.Violet);
        stop = Ui.Btn(L("Stop"), () => app.Host.Images.Stop(), Glyph.Stop);
        working.Children.Add(bar);
        working.Children.Add(step);
        makePart.Children.Add(prompt);
        makePart.Children.Add(Ui.Row("auto,auto,auto,*", 10, Ui.Label(L("Grootte"), 13, Theme.Secondary), sizes, make, stop));
        ((FrameworkElement)((Grid)makePart.Children[1]).Children[0]).VerticalAlignment = VerticalAlignment.Center;
        makePart.Children.Add(working);
        makePart.Children.Add(planNote);
        makePart.Children.Add(Ui.Caption(L("Het model volgt een beschrijving in het Engels het best: in het Nederlands mist het soms waar het om gaat.")));
        makePart.Children.Add(engineNote);
        makePart.Children.Add(note);
        makePart.Children.Add(error);

        var copy = Ui.Btn(L("Kopieer"), () => _ = CopyShown(), Glyph.Copy);
        var inFolder = Ui.Btn(L("Toon in map"), ShowInFolder, Glyph.Folder);
        var remove = Ui.Btn(L("Verwijderen"), () => _ = RemoveShown(), Glyph.Trash);
        resultCard = Ui.Card(Ui.V(10, picture, about, caption, Ui.H(8, copy, inFolder, remove)), L("Afbeelding"), Glyph.Image, Theme.Violet);
        galleryCard = Ui.Card(gallery, L("Eerdere afbeeldingen"), Glyph.Clock, Theme.Cyan);

        var head = Ui.PageHead(Ui.PageIcon(Glyph.Image), L("Beeld"), Ui.Label(L("Maakt afbeeldingen op deze pc. Je beschrijving gaat niet naar buiten."), 13, Theme.Secondary));
        modelCard = Ui.Card(modelPart, L("Model"), Glyph.Models, Theme.Violet);
        var page = Ui.Page(head, modelCard, Ui.Card(makePart, L("Beschrijf wat je wilt zien"), Glyph.Image, Theme.Violet), resultCard, galleryCard);
        body = (StackPanel)page.Content;
        View = page;
        app.Watch(View, Update);
        app.WatchSamples(View, Elapsed);
    }

    private ImageSize ChosenSize()
    {
        var chosen = ImageSize.Parse(app.Settings.ImageSize);
        return chosen is { } size && ImagePlanner.Sizes.Contains(size) ? size : ImagePlanner.DefaultSize(app.Host.System);
    }

    private static string SizeLabel(ImageSize s)
    {
        var text = $"{s.Width} × {s.Height}";
        return s.Width > s.Height ? text + " " + L("(liggend)") : s.Width < s.Height ? text + " " + L("(staand)") : text;
    }

    private void Make() => _ = app.MakeImage(prompt.Text.Trim(), ChosenSize());

    private void Update()
    {
        if (this.model is not { } model) return;
        modelName.SetIfChanged(model.Name);
        modelSummary.SetIfChanged(LKey(model.Summary));
        var host = app.Host;
        var images = host.Images;
        var store = host.Store;
        var status = store.StatusOf(model.Id);
        bool downloading = store.Downloading.Contains(model.Id);

        // first the download; once the model is here, the images come first and the model moves to the end
        int place = status.IsComplete ? body.Children.Count - 1 : 1;
        if (body.Children.IndexOf(modelCard) != place)
        {
            body.Children.Remove(modelCard);
            body.Children.Insert(place, modelCard);
        }
        downloadBar.Value = status.Fraction;
        downloadBar.Show(!status.IsComplete);
        var eta = store.EtaSeconds(model.Id);
        downloadText.SetIfChanged(status.IsComplete
            ? L($"{status.Expected.GbShort()} op schijf")
            : L($"{status.Have.GbShort()} van {status.Expected.GbShort()}") + (eta is { } e ? " · " + L($"{store.Speed / 1e6:0} MB/s · nog {Ui.Duration(e)}") : ""));
        ulong missing = status.Expected > status.Have ? status.Expected - status.Have : 0;
        bool shortOfDisk = !status.IsComplete && !downloading && missing > host.System.FreeDisk;
        diskNote.SetIfChanged(shortOfDisk ? L($"Op deze schijf is {host.System.FreeDisk.GbShort()} vrij; {model.Name} heeft nog {missing.GbShort()} nodig.") : "");
        diskNote.Show(shortOfDisk);
        var downloadProblem = store.LastError.TryGetValue(model.Id, out var le) ? le : ModelCard.DeleteErrors.TryGetValue(model.Id, out var de) ? de : "";
        downloadError.SetIfChanged(downloadProblem);
        downloadError.Show(downloadProblem.Length > 0);
        download.Show(!status.IsComplete && !downloading);
        pause.Show(downloading);
        deleteModel.Show(!downloading && status.Have > 0);
        deleteModel.IsEnabled = !images.Busy;

        var size = ChosenSize();
        bool busy = images.Busy;
        make.Show(!busy);
        stop.Show(busy);
        make.IsEnabled = status.IsComplete && prompt.Text.Trim().Length > 0;
        make.Tip(!status.IsComplete ? L("Download eerst het model.") : prompt.Text.Trim().Length == 0 ? L("Beschrijf eerst wat je wilt zien.") : null);
        sizes.IsEnabled = !busy;
        prompt.IsEnabled = !busy;
        working.Show(busy);
        bar.Value = images.Fraction;
        Elapsed();

        var plan = host.ImagePlanFor(size);
        planNote.SetIfChanged(plan.OnCard
            ? L("Op de videokaart duurt een afbeelding seconden tot een minuut.")
            : L("Op de processor duurt een afbeelding minuten. Hoe lang precies hangt af van deze pc en de grootte.") + (plan.Tiling ? " " + L("Op dit geheugen werkt de laatste stap in delen, en dat duurt langer.") : ""));
        bool engineRuns = host.Engine?.State.IsActive == true;
        engineNote.SetIfChanged(engineRuns && host.Engine?.Model is { } running
            ? L($"{running.ShortName} stopt zolang er een afbeelding wordt gemaakt: daarnaast is meestal geen ruimte. Start het daarna weer in een gesprek.") : "");
        engineNote.Show(engineRuns && !busy);
        note.SetIfChanged(images.Note ?? "");
        note.Show(images.Note != null);
        error.SetIfChanged(images.Error ?? "");
        error.Show(images.Error != null);

        // a new image comes on screen by itself
        if (wasBusy && !busy && images.Last is { } made) shown = made;
        wasBusy = busy;
        ShowResult();
        FillGallery();
    }

    private void Elapsed()
    {
        var images = app.Host.Images;
        if (!images.Busy) return;
        step.SetIfChanged(images.Status + " · " + Ui.Clock((DateTime.Now - images.Started).TotalSeconds));
    }

    private string shownPath = "";

    private void ShowResult()
    {
        shown ??= app.Host.Images.Last;
        resultCard.Show(shown != null);
        if (shown == null) return;
        if (shown.Path != shownPath)
        {
            shownPath = shown.Path;
            picture.Source = new BitmapImage(new Uri(shown.Path));
            about.Text = shown.Prompt;
            about.Show(shown.Prompt.Length > 0);
            string where = shown.OnCard ? L("op de videokaart") : L("op de processor");
            caption.Text = shown.Seconds > 0
                ? L($"{shown.Width} × {shown.Height} · {Ui.Clock(shown.Seconds)} {where} · nummer {shown.Seed}")
                : System.IO.Path.GetFileName(shown.Path);
        }
    }

    private void FillGallery()
    {
        var list = app.Host.Images.History();
        var key = string.Join("|", list.Select(i => i.Path));
        galleryCard.Show(list.Count > 0);
        if (key == galleryKey) return;
        galleryKey = key;
        gallery.Children.Clear();
        foreach (var item in list)
        {
            var thumb = new Image { Width = ThumbWidth, Height = ThumbWidth, Stretch = Stretch.UniformToFill, Source = new BitmapImage(new Uri(item.Path)) { DecodePixelWidth = ThumbWidth * 2, DecodePixelType = DecodePixelType.Logical } };
            var b = new Button { Content = thumb, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0) };
            string name = item.Prompt.Length > 0 ? item.Prompt : System.IO.Path.GetFileName(item.Path);
            Automation.SetName(b, name);
            b.Tip(name);
            b.Click += (_, _) => { shown = item; ShowResult(); };
            gallery.Children.Add(b);
        }
    }

    private async Task CopyShown()
    {
        if (shown == null) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(shown.Path);
            var data = new DataPackage();
            data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            Clipboard.SetContent(data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            await Ui.Ask(app, L("Kopieer"), e.Message, null, close: L("Sluiten"));
        }
    }

    private void ShowInFolder()
    {
        if (shown != null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{shown.Path}\"") { UseShellExecute = true });
    }

    /// <summary>To the recycle bin: what the user removes stays recoverable.</summary>
    private async Task RemoveShown()
    {
        if (shown == null) return;
        var path = shown.Path;
        await Task.Run(() =>
        {
            RecycleBin.Move(path);
            var side = System.IO.Path.ChangeExtension(path, ".json");
            if (File.Exists(side)) RecycleBin.Move(side);
        });
        app.Host.Images.Forget(path);
        shown = null;
        shownPath = "";
        Update();
    }
}
