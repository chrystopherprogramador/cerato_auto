using CeratoSoundAI.Domain;

namespace CeratoSoundAI.Application.Audio;

/// <summary>Shared snapshot/rollback logic. Engines implement only the hardware-facing parts.</summary>
public abstract class AudioEngineBase : IAudioEngine
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EqSettings? _current;
    private EqSettings? _previous;
    private EqSettings? _stable;
    protected AudioCapabilities? Caps;

    public abstract EngineKind Kind { get; }
    protected abstract Task<AudioCapabilities> ProbeAsync(CancellationToken ct);
    protected abstract Task<ApplyResult> ApplyCoreAsync(EqSettings settings, AudioCapabilities caps, CancellationToken ct);
    public abstract Task<EqSettings?> ReadCurrentAsync(CancellationToken ct = default);
    public virtual Task<IReadOnlyList<AudioConflict>> DetectConflictsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AudioConflict>>(Array.Empty<AudioConflict>());

    public async Task<AudioCapabilities> DetectCapabilitiesAsync(CancellationToken ct = default)
        => Caps = await ProbeAsync(ct);

    public async Task<IReadOnlyList<BandInfo>> GetBandsAsync(CancellationToken ct = default)
        => (Caps ?? await DetectCapabilitiesAsync(ct)).Bands;

    public async Task<ApplyResult> ApplyAsync(EqSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var caps = Caps ?? await DetectCapabilitiesAsync(ct);
            var issues = SettingsValidator.Validate(settings, caps, enforceSafeBoost: false);
            if (issues.Count > 0)
                return ApplyResult.Fail("Rejected: " + string.Join("; ", issues.Select(i => $"{i.Field}: {i.Message}")));
            var result = await ApplyCoreAsync(settings, caps, ct);
            if (result.Success)
            {
                _previous = _current;
                _current = settings.Clone();
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public Task SaveAsync(CancellationToken ct = default)
    {
        if (_current != null) _stable = _current.Clone();
        return Task.CompletedTask;
    }

    public async Task<ApplyResult> RestoreAsync(CancellationToken ct = default)
        => _stable == null ? ApplyResult.Fail("No stable configuration stored.") : await ApplyAsync(_stable, ct);

    public async Task<ApplyResult> RollbackAsync(CancellationToken ct = default)
    {
        var target = _previous ?? _stable;
        if (target == null) return ApplyResult.Fail("Nothing to roll back to.");
        return await ApplyAsync(target.Clone(), ct);
    }

    public virtual async Task<EngineState> GetStateAsync(CancellationToken ct = default)
    {
        var c = Caps ?? await DetectCapabilitiesAsync(ct);
        return new EngineState(Kind, c.Level, c.Level != ControlLevel.Unavailable, c.AffectsRealAudio, string.Join(" ", c.Notes));
    }
}

/// <summary>Development engine. Changes NOTHING real and says so.</summary>
public sealed class SimulationAudioEngine : AudioEngineBase
{
    private EqSettings? _state;
    public override EngineKind Kind => EngineKind.Simulation;

    protected override Task<AudioCapabilities> ProbeAsync(CancellationToken ct)
    {
        var bands = FrequencyMath.OctaveCenters.Select((f, i) => new BandInfo(i, f, f / 1.414, f * 1.414)).ToList();
        return Task.FromResult(new AudioCapabilities
        {
            Engine = Kind, Level = ControlLevel.Limited, AffectsRealAudio = false, Bands = bands,
            MinGainDb = -12, MaxGainDb = 12, SupportsReadback = true,
            Notes = new[] { "SIMULATION: nothing is sent to the real audio system." }
        });
    }

    protected override Task<ApplyResult> ApplyCoreAsync(EqSettings s, AudioCapabilities caps, CancellationToken ct)
    {
        _state = s.Clone();
        return Task.FromResult(new ApplyResult(true, false, false, "Simulated only. Real sound was NOT changed."));
    }

    public override Task<EqSettings?> ReadCurrentAsync(CancellationToken ct = default) => Task.FromResult(_state?.Clone());
}

/// <summary>No programmatic control: produces values the user can reproduce on the original equalizer.</summary>
public sealed class ManualDspAudioEngine : AudioEngineBase
{
    private readonly IReadOnlyList<double> _bandCenters;
    private readonly double _min, _max;
    public EqSettings? LastInstructions { get; private set; }

    /// <param name="bandCenters">Band layout of the ORIGINAL equalizer as entered by the user (not assumed).</param>
    public ManualDspAudioEngine(IReadOnlyList<double> bandCenters, double minDb = -12, double maxDb = 12)
    { _bandCenters = bandCenters; _min = minDb; _max = maxDb; }

    public override EngineKind Kind => EngineKind.Manual;

    protected override Task<AudioCapabilities> ProbeAsync(CancellationToken ct) => Task.FromResult(new AudioCapabilities
    {
        Engine = Kind, Level = ControlLevel.Limited, AffectsRealAudio = false,
        Bands = _bandCenters.Select((f, i) => new BandInfo(i, f, f / 1.414, f * 1.414)).ToList(),
        MinGainDb = _min, MaxGainDb = _max, SupportsReadback = false,
        Notes = new[] { "MANUAL: values must be entered by hand in the original equalizer. The app cannot verify them." }
    });

    protected override Task<ApplyResult> ApplyCoreAsync(EqSettings s, AudioCapabilities caps, CancellationToken ct)
    {
        LastInstructions = s.Clone();
        return Task.FromResult(new ApplyResult(true, false, false, "Instructions generated. Not applied automatically."));
    }

    public override Task<EqSettings?> ReadCurrentAsync(CancellationToken ct = default) => Task.FromResult<EqSettings?>(null);

    public string RenderInstructions(EqSettings s) =>
        string.Join(Environment.NewLine, _bandCenters.Select((f, i) => $"{FrequencyMath.Format(f)} Hz: {s.BandGainsDb[i]:+0.#;-0.#;0} dB"));
}

/// <summary>Placeholder for a proprietary DSP. Stays unavailable until a control interface is PROVEN.</summary>
public sealed class NativeDspAudioEngine : AudioEngineBase
{
    public override EngineKind Kind => EngineKind.NativeDsp;
    protected override Task<AudioCapabilities> ProbeAsync(CancellationToken ct) => Task.FromResult(
        AudioCapabilities.None(Kind, "No proven control interface for the Wintech DSP/MCU. Requires hardware identification and in-vehicle tests."));
    protected override Task<ApplyResult> ApplyCoreAsync(EqSettings s, AudioCapabilities caps, CancellationToken ct)
        => Task.FromResult(ApplyResult.Fail("Native DSP control is not implemented (not proven)."));
    public override Task<EqSettings?> ReadCurrentAsync(CancellationToken ct = default) => Task.FromResult<EqSettings?>(null);
}
