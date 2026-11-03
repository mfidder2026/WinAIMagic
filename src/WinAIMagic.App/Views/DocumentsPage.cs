using Microsoft.UI.Xaml;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>The documents module; placeholder for now.</summary>
public sealed class DocumentsPage : IPage
{
    public FrameworkElement View { get; }

    public DocumentsPage(AppState app)
    {
        View = Ui.Page(Ui.PageHead(Ui.PageIcon(Glyph.Document), L("Documenten"), Ui.Label("", 14, Theme.Secondary)));
    }
}
