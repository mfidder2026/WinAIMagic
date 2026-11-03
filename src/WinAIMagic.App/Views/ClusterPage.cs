using Microsoft.UI.Xaml;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>The cluster; placeholder for now.</summary>
public sealed class ClusterPage : IPage
{
    public FrameworkElement View { get; }

    public ClusterPage(AppState app)
    {
        View = Ui.Page(Ui.PageHead(Ui.PageIcon(Glyph.Cluster), L("Cluster"), Ui.Label("", 14, Theme.Secondary)));
    }
}
