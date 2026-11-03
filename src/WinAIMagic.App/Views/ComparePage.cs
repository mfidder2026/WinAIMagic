using Microsoft.UI.Xaml;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>Comparing the models; placeholder for now.</summary>
public sealed class ComparePage : IPage
{
    public FrameworkElement View { get; }

    public ComparePage(AppState app)
    {
        View = Ui.Page(Ui.PageHead(Ui.PageIcon(Glyph.Compare), L("Vergelijken"), Ui.Label("", 14, Theme.Secondary)));
    }
}
