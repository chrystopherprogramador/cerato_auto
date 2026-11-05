using CeratoSoundAI.App.Platforms.AndroidImpl;
using CeratoSoundAI.App.Services;
using CeratoSoundAI.App.ViewModels;
using CeratoSoundAI.App.Views;
using CeratoSoundAI.Application.Assistant;
using CeratoSoundAI.Application.Audio;
using CeratoSoundAI.Application.Profiles;
using CeratoSoundAI.Domain;
using CeratoSoundAI.Infrastructure.Llm;
using CeratoSoundAI.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CeratoSoundAI.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var b = MauiApp.CreateBuilder();
        b.UseMauiApp<App>();

        var s = b.Services;
        s.AddSingleton<ISettingsStore, PreferencesSettingsStore>();
        s.AddSingleton<AppOptions>();
        s.AddHttpClient("ollama");
        s.AddSingleton<ILlmProvider>(sp => new OllamaProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("ollama"), () => sp.GetRequiredService<AppOptions>().Ollama()));
        s.AddSingleton<AssistantService>();

        s.AddSingleton<IProfileRepository>(_ => new SqliteProfileRepository(Path.Combine(FileSystem.AppDataDirectory, "cerato.db3")));
        s.AddSingleton<AndroidAudioEngine>();
        s.AddSingleton<IHardwareDiagnostics, AndroidHardwareDiagnostics>();
        s.AddSingleton<IVoiceAssistant, AndroidVoiceAssistant>();
        s.AddSingleton<ICalibrationPlatform, AndroidCalibrationPlatform>();
        s.AddSingleton<IVehicleState, NoMotionInfo>();
        s.AddSingleton(sp => new EngineHost(sp.GetRequiredService<ISettingsStore>(), new Dictionary<EngineMode, Func<IAudioEngine>>
        {
            [EngineMode.AndroidCompatible] = () => sp.GetRequiredService<AndroidAudioEngine>(),
            [EngineMode.Native] = () => new NativeDspAudioEngine(),
            [EngineMode.Manual] = () => new ManualDspAudioEngine(EngineHost.ParseManualBands(sp.GetRequiredService<AppOptions>().ManualBands)),
            [EngineMode.Diagnostic] = () => new DiagnosticOnlyEngine(),
            [EngineMode.Simulation] = () => new SimulationAudioEngine()
        }));
        s.AddSingleton(sp => new ProfileService(sp.GetRequiredService<IProfileRepository>(), sp.GetRequiredService<EngineHost>(),
            $"{DeviceInfo.Manufacturer}/{DeviceInfo.Model}"));
        s.AddSingleton<AppState>();

        s.AddSingleton<AppShell>();
        s.AddSingleton<MainViewModel>(); s.AddSingleton<MainPage>();
        s.AddSingleton<EqualizerViewModel>(); s.AddSingleton<EqualizerPage>();
        s.AddSingleton<AssistantViewModel>(); s.AddSingleton<AssistantPage>();
        s.AddSingleton<ProfilesViewModel>(); s.AddSingleton<ProfilesPage>();
        s.AddSingleton<CalibrationViewModel>(); s.AddSingleton<CalibrationPage>();
        s.AddSingleton<DiagnosticsViewModel>(); s.AddSingleton<DiagnosticsPage>();
        s.AddSingleton<HistoryViewModel>(); s.AddSingleton<HistoryPage>();
        s.AddSingleton<SettingsViewModel>(); s.AddSingleton<SettingsPage>();
        return b.Build();
    }
}

/// <summary>No reliable vehicle motion source is wired in. Unknown is reported as unknown.</summary>
public sealed class NoMotionInfo : IVehicleState { public bool? IsMoving => null; }
