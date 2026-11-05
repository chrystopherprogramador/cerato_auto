namespace CeratoSoundAI.Domain;

public enum ControlLevel { Active, Limited, Unavailable }

public enum EngineKind { Diagnostic, Simulation, Manual, AndroidEffect, NativeDsp }

public sealed record BandInfo(int Index, double CenterHz, double MinHz, double MaxHz);

/// <summary>Capabilities that were actually probed (or explicitly declared as simulated/manual).</summary>
public sealed record AudioCapabilities
{
    public EngineKind Engine { get; init; }
    public ControlLevel Level { get; init; }
    /// <summary>True only when applying settings really changes the audio output path. Always false for Simulation/Manual.</summary>
    public bool AffectsRealAudio { get; init; }
    public IReadOnlyList<BandInfo> Bands { get; init; } = Array.Empty<BandInfo>();
    public double MinGainDb { get; init; }
    public double MaxGainDb { get; init; }
    public bool SupportsPreamp { get; init; }
    public bool SupportsBalanceFader { get; init; }
    public bool SupportsParametric { get; init; }
    public bool SupportsCrossover { get; init; }
    public bool SupportsTimeAlignment { get; init; }
    public bool SupportsReadback { get; init; }
    public double MaxPreampDb { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public int BandCount => Bands.Count;
    public static AudioCapabilities None(EngineKind kind, params string[] notes) =>
        new() { Engine = kind, Level = ControlLevel.Unavailable, Notes = notes };
}

public sealed class EqSettings
{
    public List<double> BandGainsDb { get; set; } = new();
    public double PreampDb { get; set; }
    public double Balance { get; set; }   // -1..1, only when supported
    public double Fader { get; set; }     // -1..1, only when supported

    public EqSettings Clone() => new()
    {
        BandGainsDb = new List<double>(BandGainsDb),
        PreampDb = PreampDb, Balance = Balance, Fader = Fader
    };

    public static EqSettings Flat(int bands) => new() { BandGainsDb = Enumerable.Repeat(0d, bands).ToList() };
}

public sealed record ApplyResult(bool Success, bool Verified, bool AffectsRealAudio, string Message, EqSettings? ReadBack = null)
{
    public static ApplyResult Fail(string message) => new(false, false, false, message);
}

public sealed record AudioConflict(string Source, string Description, bool Confirmed);

public sealed record EngineState(EngineKind Engine, ControlLevel Level, bool Enabled, bool AffectsRealAudio, string Description);

public interface IAudioEngine
{
    EngineKind Kind { get; }
    Task<AudioCapabilities> DetectCapabilitiesAsync(CancellationToken ct = default);
    /// <summary>Reads the real state. Returns null when readback is not supported.</summary>
    Task<EqSettings?> ReadCurrentAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BandInfo>> GetBandsAsync(CancellationToken ct = default);
    Task<ApplyResult> ApplyAsync(EqSettings settings, CancellationToken ct = default);
    /// <summary>Marks the currently applied settings as the last stable ones.</summary>
    Task SaveAsync(CancellationToken ct = default);
    Task<ApplyResult> RestoreAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AudioConflict>> DetectConflictsAsync(CancellationToken ct = default);
    Task<EngineState> GetStateAsync(CancellationToken ct = default);
    /// <summary>Returns to the previously applied settings (or the last stable ones).</summary>
    Task<ApplyResult> RollbackAsync(CancellationToken ct = default);
}
