using CeratoSoundAI.App.Services;

namespace CeratoSoundAI.App;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly IServiceProvider _sp;
    public App(IServiceProvider sp) { _sp = sp; InitializeComponent(); UserAppTheme = AppTheme.Dark; }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_sp.GetRequiredService<AppShell>());
        var state = _sp.GetRequiredService<AppState>();
        window.Created += async (_, _) => await state.InitializeAsync();
        // Resume (ignition on / wake): reconcile real state with the stored profile again.
        window.Resumed += async (_, _) =>
        {
            if (!state.Initialized) return;
            try { var r = await state.Profiles.StartupAsync(); state.StartupSummary = r.Summary; await state.RefreshAsync(); } catch { }
        };
        return window;
    }
}
