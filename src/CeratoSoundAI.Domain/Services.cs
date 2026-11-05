namespace CeratoSoundAI.Domain;

// ---------- Hardware diagnostics ----------
public sealed class HardwareReport
{
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Device { get; set; } = new();
    public Dictionary<string, string> Memory { get; set; } = new();
    public List<string> AudioPackages { get; set; } = new();
    public List<string> OriginalEqCandidates { get; set; } = new();
    public List<EffectInfo> Effects { get; set; } = new();
    public List<string> OutputDevices { get; set; } = new();
    public Dictionary<string, string> AudioManager { get; set; } = new();
    public List<ProbeResult> Probes { get; set; } = new();
    public List<string> Findings { get; set; } = new();
    public bool Sanitized { get; set; }
}

public sealed record EffectInfo(string Name, string Type, string Uuid, string Implementor);

/// <summary>Result of an actual read/write probe against a control. Never fabricated.</summary>
public sealed record ProbeResult(string Name, bool Attempted, bool ReadOk, bool WriteOk, string Detail);

public interface IHardwareDiagnostics
{
    Task<HardwareReport> CollectAsync(CancellationToken ct = default);
    /// <summary>Read-only ADB commands the user may run for deeper investigation.</summary>
    IReadOnlyList<string> ReadOnlyAdbCommands { get; }
}

// ---------- LLM ----------
public sealed record LlmModel(string Name);
public sealed record ChatMessage(string Role, string Content);

public interface ILlmProvider
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct = default);
    Task<string> ChatAsync(string model, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
}

// ---------- Voice ----------
public interface IVoiceAssistant
{
    bool IsRecognitionAvailable { get; }
    bool IsSpeechAvailable { get; }
    /// <summary>Single explicit listen. Never continuous.</summary>
    Task<string?> ListenOnceAsync(CancellationToken ct = default);
    Task SpeakAsync(string text, CancellationToken ct = default);
    void CancelAll();
}

// ---------- Vehicle ----------
public interface IVehicleState
{
    /// <summary>null when no trustworthy motion information is available.</summary>
    bool? IsMoving { get; }
}

// ---------- Settings ----------
public interface ISettingsStore
{
    string? Get(string key);
    void Set(string key, string? value);
}
