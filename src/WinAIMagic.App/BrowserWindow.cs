using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using WinAIMagic.Chat.Agent;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App;

/// <summary>The model's own browser window (Instellingen → Agent → Browser): WebView2 with cookies of its own in the app data
/// folder, apart from Edge and Chrome. The user can watch it, and logs in there themselves. It comes to the front without taking
/// the keyboard away from the chat.</summary>
public sealed class BrowserWindow(DispatcherQueue ui) : IBrowserHost
{
    private Window? window;
    private WebView2? web;
    private TaskCompletionSource<string?>? loading;
    private bool navigating;

    public bool IsOpen => web?.CoreWebView2?.Source is { Length: > 0 } s && s != "about:blank";
    public bool CanGoBack => web?.CoreWebView2?.CanGoBack == true;

    private Task<T> OnUi<T>(Func<Task<T>> work)
    {
        if (ui.HasThreadAccess) return work();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.TryEnqueue(async () =>
        {
            try { done.SetResult(await work()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }

    private async Task<CoreWebView2> Ensure()
    {
        if (web?.CoreWebView2 is { } core) { Show(); return core; }
        window = new Window { Title = L("Browser van het model") };
        web = new WebView2();
        window.Content = web;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 800));
        window.AppWindow.SetIcon(Path.Combine(Paths.AppRoot, "Assets", "WinAIMagic.ico"));
        window.Closed += (_, _) => { web = null; window = null; loading?.TrySetResult("the window was closed"); };
        // cookies of its own, kept between starts
        var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(Paths.AppData, "Browser"), new CoreWebView2EnvironmentOptions());
        Show();
        await web.EnsureCoreWebView2Async(env);
        var c = web.CoreWebView2;
        c.NavigationStarting += (_, _) => navigating = true;
        c.NavigationCompleted += (_, e) =>
        {
            navigating = false;
            loading?.TrySetResult(e.IsSuccess ? null : e.WebErrorStatus.ToString());
            loading = null;
            Show();
        };
        c.DocumentTitleChanged += (_, _) => Show();
        // links that open a new window stay in this one
        c.NewWindowRequested += (_, e) => { e.Handled = true; c.Navigate(e.Uri); };
        return c;
    }

    private void Show()
    {
        if (window == null) return;
        var title = web?.CoreWebView2?.DocumentTitle;
        window.Title = L("Browser van het model") + (string.IsNullOrEmpty(title) ? "" : " · " + title);
        window.AppWindow.Show(false);
    }

    public Task<string?> Open(Uri url) => OnUi(async () =>
    {
        var c = await Ensure();
        loading?.TrySetResult("replaced by another page");
        var wait = loading = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Navigate(url.AbsoluteUri);
        // a slow page: read what is there after half a minute
        var done = await Task.WhenAny(wait.Task, Task.Delay(30_000));
        var error = done == wait.Task ? await wait.Task : null;
        await Task.Delay(400);   // scripts that fill the page after load
        return error;
    });

    public Task<string?> Eval(string script) => OnUi(async () =>
    {
        if (web?.CoreWebView2 is not { } c) return null;
        string json;
        try { json = await c.ExecuteScriptAsync(script); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException) { return null; }
        // the script returns JSON.stringify(…): ExecuteScriptAsync gives that string as JSON once more
        try { return JsonSerializer.Deserialize<string>(json); }
        catch (JsonException) { return null; }
    });

    public Task Back() => OnUi(async () =>
    {
        web?.CoreWebView2?.GoBack();
        await Task.Yield();
        return true;
    });

    public Task Settle() => OnUi(async () =>
    {
        await Task.Delay(700);
        var start = DateTime.UtcNow;
        while (navigating && (DateTime.UtcNow - start).TotalSeconds < 20) await Task.Delay(200);
        await Task.Delay(400);
        Show();
        return true;
    });

    public void Close() => ui.TryEnqueue(() => window?.Close());
}
