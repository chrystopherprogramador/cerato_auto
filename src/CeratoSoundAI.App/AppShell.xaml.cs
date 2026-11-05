namespace CeratoSoundAI.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        // Wide screens (e.g. 1024x600 head unit): keep the navigation menu permanently visible.
        SizeChanged += (_, _) => FlyoutBehavior = Width >= 900 ? FlyoutBehavior.Locked : FlyoutBehavior.Flyout;
    }

    public static async Task GoAsync(string route) => await Current.GoToAsync("//" + route);
}
