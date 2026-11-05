namespace CeratoSoundAI.Domain;

public enum SyncState { Unknown, Pending, Synced, Failed, ManualOnly }

public sealed class AudioProfile
{
    public const int CurrentSchemaVersion = 1;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public EqSettings Settings { get; set; } = new();
    /// <summary>Center frequencies the gains refer to (so a profile can be remapped to other band layouts).</summary>
    public List<double> BandCentersHz { get; set; } = new();
    public string Driver { get; set; } = EngineKind.Diagnostic.ToString();
    public string HardwareId { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public SyncState Sync { get; set; } = SyncState.Unknown;
    public bool IsFavorite { get; set; }
    public bool IsBuiltIn { get; set; }
    /// <summary>Built-in curves are starting points, NOT measured calibrations.</summary>
    public bool IsCalibrated { get; set; }
}

public sealed class ProfileHistoryEntry
{
    public long Id { get; set; }
    public Guid ProfileId { get; set; }
    public DateTime AtUtc { get; set; }
    public string Note { get; set; } = "";
    public AudioProfile Snapshot { get; set; } = new();
}

public interface IProfileRepository
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AudioProfile>> GetAllAsync(CancellationToken ct = default);
    Task<AudioProfile?> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Atomically saves the profile and pushes the previous version to history.</summary>
    Task SaveAsync(AudioProfile profile, string note, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ProfileHistoryEntry>> GetHistoryAsync(Guid profileId, int max = 50, CancellationToken ct = default);
    Task<IReadOnlyList<ProfileHistoryEntry>> GetAllHistoryAsync(int max = 100, CancellationToken ct = default);
    Task<Guid?> GetActiveProfileIdAsync(CancellationToken ct = default);
    Task SetActiveProfileIdAsync(Guid id, CancellationToken ct = default);
    Task<AudioProfile?> GetLastStableAsync(CancellationToken ct = default);
    Task SetLastStableAsync(AudioProfile profile, CancellationToken ct = default);
    Task AddLogAsync(string level, string message, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetLogsAsync(int max = 100, CancellationToken ct = default);
}
