using CeratoSoundAI.Application;
using CeratoSoundAI.Application.Audio;
using CeratoSoundAI.Application.Profiles;
using CeratoSoundAI.Domain;
using CeratoSoundAI.Infrastructure.Llm;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CeratoSoundAI.App.Services;

public enum EngineMode { AndroidCompatible, Native, Manual, Diagnostic, Simulation }

public sealed class PreferencesSettingsStore : ISettingsStore
{
    public string? Get(string key) => Preferences.Default.ContainsKey(key) ? Preferences.Default.Get<string?>(key, null) : null;
    public void Set(string key, string? value)
    {
        if (value == null) Preferences.Default.Remove(key); else Preferences.Default.Set(key, value);
    }
}

public sealed class DiagnosticOnlyEngine : AudioEngineBase
{
    public override EngineKind Kind => EngineKind.Diagnostic;
    protected override Task<AudioCapabilities> ProbeAsync(CancellationToken ct) => Task.FromResult(
        AudioCapabilities.None(Kind, "Modo de diagnóstico: nenhum ajuste é enviado ao áudio."));
    protected override Task<ApplyResult> ApplyCoreAsync(EqSettings s, AudioCapabilities c, CancellationToken ct) => Task.FromResult(ApplyResult.Fail("Modo de diagnóstico: sem aplicação."));
    public override Task<EqSettings?> ReadCurrentAsync(CancellationToken ct = default) => Task.FromResult<EqSettings?>(null);
}

/// <summary>Routes IAudioEngine calls to the engine selected by the user. Never switches silently.</summary>
public sealed class EngineHost : IAudioEngine
{
    private readonly Dictionary<EngineMode, Func<IAudioEngine>> _factories;
    private readonly Dictionary<EngineMode, IAudioEngine> _cache = new();
    private readonly ISettingsStore _settings;
    public EngineMode Mode { get; private set; }
    public IAudioEngine Current => _cache.TryGetValue(Mode, out var e) ? e : _cache[Mode] = _factories[Mode]();

    public EngineHost(ISettingsStore settings, Dictionary<EngineMode, Func<IAudioEngine>> factories)
    {
        _settings = settings; _factories = factories;
        Mode = Enum.TryParse<EngineMode>(settings.Get("engine_mode"), out var m) ? m : EngineMode.AndroidCompatible;
    }

    public void SetMode(EngineMode mode)
    {
        Mode = mode;
        _settings.Set("engine_mode", mode.ToString());
    }

    /// <summary>Band layout of the ORIGINAL equalizer, as entered by the user (never assumed).</summary>
    public static IReadOnlyList<double> ParseManualBands(string? s)
    {
        var parsed = (s ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1).ToList();
        return parsed.Count >= 2 && parsed.All(v => v > 0) && parsed.Zip(parsed.Skip(1)).All(p => p.Second > p.First) ? parsed : FrequencyMath.OctaveCenters;
    }

    public EngineKind Kind => Current.Kind;
    public Task<AudioCapabilities> DetectCapabilitiesAsync(CancellationToken ct = default) => Current.DetectCapabilitiesAsync(ct);
    public Task<EqSettings?> ReadCurrentAsync(CancellationToken ct = default) => Current.ReadCurrentAsync(ct);
    public Task<IReadOnlyList<BandInfo>> GetBandsAsync(CancellationToken ct = default) => Current.GetBandsAsync(ct);
    public Task<ApplyResult> ApplyAsync(EqSettings s, CancellationToken ct = default) => Current.ApplyAsync(s, ct);
    public Task SaveAsync(CancellationToken ct = default) => Current.SaveAsync(ct);
    public Task<ApplyResult> RestoreAsync(CancellationToken ct = default) => Current.RestoreAsync(ct);
    public Task<IReadOnlyList<AudioConflict>> DetectConflictsAsync(CancellationToken ct = default) => Current.DetectConflictsAsync(ct);
    public Task<EngineState> GetStateAsync(CancellationToken ct = default) => Current.GetStateAsync(ct);
    public Task<ApplyResult> RollbackAsync(CancellationToken ct = default) => Current.RollbackAsync(ct);
}

public sealed class AppOptions
{
    private readonly ISettingsStore _s;
    public AppOptions(ISettingsStore s) => _s = s;

    public OllamaOptions Ollama() => new()
    {
        BaseUrl = _s.Get("llm_url") ?? "https://ollama.com",
        ApiKey = ReadKey()
    };

    public string? Model { get => _s.Get("llm_model"); set => _s.Set("llm_model", value); }
    public string? ManualBands { get => _s.Get("manual_bands"); set => _s.Set("manual_bands", value); }
    public bool AllowHighBoost { get => _s.Get("allow_high_boost") == "1"; set => _s.Set("allow_high_boost", value ? "1" : "0"); }
    public bool SpeakReplies { get => _s.Get("speak") == "1"; set => _s.Set("speak", value ? "1" : "0"); }

    private static string? ReadKey()
    {
        try { return SecureStorage.Default.GetAsync("llm_key").GetAwaiter().GetResult(); } catch { return null; }
    }
    public static async Task<bool> SaveKeyAsync(string? key)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(key)) SecureStorage.Default.Remove("llm_key");
            else await SecureStorage.Default.SetAsync("llm_key", key.Trim());
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Shared UI state: capabilities, current curve, active profile and honest status text.</summary>
public sealed partial class AppState : ObservableObject
{
    private readonly EngineHost _engine;
    private readonly ProfileService _profiles;
    private readonly IProfileRepository _repo;

    private readonly AppOptions _opts;

    public AppState(EngineHost engine, ProfileService profiles, IProfileRepository repo, AppOptions opts)
    { _engine = engine; _profiles = profiles; _repo = repo; _opts = opts; }

    [ObservableProperty] private AudioCapabilities _caps = AudioCapabilities.None(EngineKind.Diagnostic, "Ainda não detectado.");
    [ObservableProperty] private EqSettings _current = new();
    [ObservableProperty] private AudioProfile? _activeProfile;
    [ObservableProperty] private string _dspStatusText = "DSP: detectando…";
    [ObservableProperty] private string _dspStatusDetail = "";
    [ObservableProperty] private string _dspStatusColor = "#A9BACD";
    [ObservableProperty] private string _syncText = "Perfil: —";
    [ObservableProperty] private string _startupSummary = "";
    [ObservableProperty] private bool _initialized;

    public EngineHost Engine => _engine;
    public ProfileService Profiles => _profiles;
    public IProfileRepository Repo => _repo;
    public bool HasMeasurements => false;

    public async Task InitializeAsync()
    {
        try
        {
            await _profiles.InitializeAsync();
            var res = await _profiles.StartupAsync();
            StartupSummary = res.Summary;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StartupSummary = "Falha na inicialização: " + ex.Message;
            await RefreshAsync();
        }
        Initialized = true;
    }

    private Guid? _previousProfileId;

    /// <summary>Applies + persists a profile, remembering the previous one so "go back" works.</summary>
    public async Task<ApplyResult> ApplyProfileAsync(AudioProfile profile)
    {
        var before = ActiveProfile?.Id;
        var r = await _profiles.ApplyAsync(profile, _opts.AllowHighBoost);
        if (r.Success && before != profile.Id) _previousProfileId = before;
        await RefreshAsync();
        return r;
    }

    public async Task<ApplyResult> RevertAsync()
    {
        if (_previousProfileId.HasValue && await _repo.GetAsync(_previousProfileId.Value) is { } prev)
            return await ApplyProfileAsync(prev);
        var r = await _profiles.ReturnToLastStableAsync();
        await RefreshAsync();
        return r;
    }

    public async Task RefreshAsync()
    {
        Caps = await _engine.DetectCapabilitiesAsync();
        var id = await _repo.GetActiveProfileIdAsync();
        ActiveProfile = id.HasValue ? await _repo.GetAsync(id.Value) : null;
        var real = Caps.SupportsReadback ? await _engine.ReadCurrentAsync() : null;
        if (real != null && real.BandGainsDb.Count == Caps.BandCount) Current = real;
        else if (ActiveProfile != null) Current = ProfileService.ToEngineBands(ActiveProfile, Caps);
        else Current = EqSettings.Flat(Caps.BandCount);

        (DspStatusText, DspStatusColor) = Caps.Level switch
        {
            ControlLevel.Active => ("DSP: CONTROLE ATIVO", "#5FD38D"),
            ControlLevel.Limited when Caps.AffectsRealAudio => ("DSP: CONTROLE LIMITADO (não comprovado audivelmente)", "#E8B04A"),
            ControlLevel.Limited => (Caps.Engine == EngineKind.Simulation ? "SIMULAÇÃO: não altera o som real" : "MANUAL: ajuste na central original", "#E8B04A"),
            _ => ("DSP: CONTROLE INDISPONÍVEL", "#E06C75")
        };
        DspStatusDetail = string.Join(" ", Caps.Notes);
        SyncText = ActiveProfile == null ? "Perfil: nenhum aplicado" : $"Perfil: {ActiveProfile.Name} · {ActiveProfile.Sync switch
        {
            SyncState.Synced => "aplicado e confirmado",
            SyncState.Pending => "aplicado (sem confirmação)",
            SyncState.Failed => "FALHA ao aplicar",
            SyncState.ManualOnly => "salvo (não aplicado automaticamente)",
            _ => "salvo"
        }}";
    }
}

