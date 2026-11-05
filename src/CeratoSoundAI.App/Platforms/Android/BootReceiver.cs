using Android.App;
using Android.Content;
using CeratoSoundAI.Application.Profiles;

namespace CeratoSoundAI.App.Platforms.AndroidImpl;

/// <summary>
/// Best-effort re-application after boot. Android may not deliver BOOT_COMPLETED on some head units, and effects
/// are released if the process is killed. Persistence after ignition-off is NOT claimed until proven in vehicle tests.
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = true, DirectBootAware = false)]
[IntentFilter(new[] { Intent.ActionBootCompleted, "android.intent.action.QUICKBOOT_POWERON" })]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action is not (Intent.ActionBootCompleted or "android.intent.action.QUICKBOOT_POWERON")) return;
        var pending = GoAsync();
        Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var sp = IPlatformApplication.Current?.Services;
                var profiles = sp?.GetService(typeof(ProfileService)) as ProfileService;
                if (profiles != null)
                {
                    await profiles.InitializeAsync(cts.Token);
                    await profiles.StartupAsync(cts.Token);
                }
            }
            catch { /* logged by StartupAsync when it gets that far */ }
            finally { pending?.Finish(); }
        });
    }
}
