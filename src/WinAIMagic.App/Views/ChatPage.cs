using Microsoft.UI.Xaml;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>The chat of app.Store.Selection; placeholder for now.</summary>
public sealed class ChatPage : IPage
{
    public FrameworkElement View { get; }

    public ChatPage(AppState app)
    {
        View = Ui.Page(Ui.PageHead(Ui.PageIcon(Glyph.Chat), L("Chat"), Ui.Label(app.Store.Current?.DisplayTitle ?? "", 14, Theme.Secondary)));
    }
}
