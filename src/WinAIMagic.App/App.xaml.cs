using Microsoft.UI.Xaml;

namespace WinAIMagic.App;

public partial class App : Application
{
    private Window? window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new Window { Title = "WinAIMagic" };
        window.Content = new Microsoft.UI.Xaml.Controls.TextBlock { Text = "WinAIMagic" };
        window.Activate();
    }
}
