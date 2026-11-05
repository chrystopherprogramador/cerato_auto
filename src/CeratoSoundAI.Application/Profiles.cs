using System.Text.Json;
using System.Text.Json.Serialization;
using CeratoSoundAI.Domain;

namespace CeratoSoundAI.Application.Profiles;

public static class BuiltInProfiles
{
    // Gains over FrequencyMath.OctaveCenters (31.25..16k). Deliberately mild starting points; NONE is a measured calibration.
    private static AudioProfile P(string name, string desc, params double[] g) => new()
    {
        Id = Guid.NewGuid(), Name = name, Description = desc, IsBuiltIn = true, IsCalibrated = false,
        BandCentersHz = FrequencyMath.OctaveCenters.ToList(),
        Settings = new EqSettings { BandGainsDb = g.ToList() },
        Sync = SyncState.Unknown
    };

    public static IReadOnlyList<AudioProfile> Create() => new[]
    {
        P("Original Cerato", "Referência a ser calibrada. Plana até que as medições do sistema original existam.", 0,0,0,0,0,0,0,0,0,0),
        P("Rock Clássico", "Ponto de partida suave: presença de médios e leve brilho. Não calibrado.", 1.5,1.5,0.5,-0.5,-0.5,0.5,1.5,2,1,0.5),
        P("Heavy Metal", "Ponto de partida suave: graves firmes, leve recuo nos médios graves, ataque nos agudos. Não calibrado.", 2,2,1,-1,-1.5,0,1.5,2.5,2,1),
        P("Eletrônica", "Ponto de partida suave: ênfase em sub-grave e ar. Não calibrado.", 3,2.5,1,0,-0.5,0,0.5,1.5,2,2),
        P("Vocal e Acústico", "Ponto de partida suave: foco nos médios e redução de graves. Não calibrado.", -1,-0.5,0,0.5,1,1.5,1,0.5,0.5,0),
        P("Equilibrado", "Leve curva de conforto. Não calibrado.", 1,0.5,0,0,0,0,0.5,1,1,0.5),
        P("Personalizado", "Seu perfil livre.", 0,0,0,0,0,0,0,0,0,0),
        P("Referência Neutra", "Todas as bandas em 0 dB.", 0,0,0,0,0,0,0,0,0,0),
    };
}

public sealed record StartupResult(bool Applied, bool Verified, string Summary, IReadOnlyList<string> Log);

public sealed class ProfileService
{
    private readonly IProfileRepository _repo;
    private readonly IAudioEngine _engine;
    private readonly string _hardwareId;

    public ProfileService(IProfileRepository repo, IAudioEngine engine, string hardwareId)
    { _repo = repo; _engine = engine; _hardwareId = hardwareId; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _repo.InitializeAsync(ct);
        if ((await _repo.GetAllAsync(ct)).Count == 0)
            foreach (var p in BuiltInProfiles.Create()) await _repo.SaveAsync(p, "seed", ct);
    }

    public static EqSettings ToEngineBands(AudioProfile p, AudioCapabilities caps)
    {
        var s = p.Settings.Clone();
        if (caps.BandCount > 0 && p.BandCentersHz.Count == p.Settings.BandGainsDb.Count && p.BandCentersHz.Count > 0)
            s.BandGainsDb = FrequencyMath.Remap(p.BandCentersHz, p.Settings.BandGainsDb, caps.Bands.Select(b => b.CenterHz).ToList());
        return s;
    }

    /// <summary>Boot/resume flow: load, detect, read real state, compare, reapply safe parts, verify, log.</summary>
    public async Task<StartupResult> StartupAsync(CancellationToken ct = default)
    {
        var log = new List<string>();
        void L(string m) { log.Add(m); }
        var id = await _repo.GetActiveProfileIdAsync(ct);
        var profile = id.HasValue ? await _repo.GetAsync(id.Value, ct) : null;
        profile ??= await _repo.GetLastStableAsync(ct);
        if (profile == null) { L("No stored profile."); return await Finish(false, false, "Nenhum perfil armazenado.", log, ct); }
        L($"Loaded profile '{profile.Name}'.");

        var caps = await _engine.DetectCapabilitiesAsync(ct);
        L($"Engine {caps.Engine}: {caps.Level}, real audio: {caps.AffectsRealAudio}.");
        if (caps.Level == ControlLevel.Unavailable || !caps.AffectsRealAudio)
        {
            profile.Sync = SyncState.ManualOnly;
            await _repo.SaveAsync(profile, "startup: no real control", ct);
            return await Finish(false, false, "Sem controle real do áudio. Perfil carregado, não aplicado.", log, ct);
        }

        var desired = ToEngineBands(profile, caps);
        var real = caps.SupportsReadback ? await _engine.ReadCurrentAsync(ct) : null;
        if (real != null && Same(real, desired)) { L("Real state already matches profile."); profile.Sync = SyncState.Synced; await _repo.SaveAsync(profile, "startup: already synced", ct); return await Finish(true, true, "Estado real já coincide com o perfil.", log, ct); }
        L(real == null ? "Real state unreadable." : "Real state differs from profile.");

        var issues = SettingsValidator.Validate(desired, caps);
        if (issues.Count > 0)
        {
            L("Unsafe/incompatible: " + string.Join("; ", issues.Select(i => i.Message)));
            profile.Sync = SyncState.Failed;
            await _repo.SaveAsync(profile, "startup: validation failed", ct);
            return await Finish(false, false, "Perfil incompatível ou fora dos limites seguros; não reaplicado.", log, ct);
        }
        var r = await _engine.ApplyAsync(desired, ct);
        L(r.Message);
        profile.Sync = r.Success ? (r.Verified ? SyncState.Synced : SyncState.Pending) : SyncState.Failed;
        await _repo.SaveAsync(profile, "startup: " + r.Message, ct);
        return await Finish(r.Success, r.Verified, r.Success ? (r.Verified ? "Perfil reaplicado e confirmado por leitura." : "Perfil reaplicado, sem confirmação por leitura.") : "Falha ao reaplicar: " + r.Message, log, ct);
    }

    private async Task<StartupResult> Finish(bool a, bool v, string s, List<string> log, CancellationToken ct)
    {
        await _repo.AddLogAsync("startup", s, ct);
        return new StartupResult(a, v, s, log);
    }

    private static bool Same(EqSettings a, EqSettings b) =>
        a.BandGainsDb.Count == b.BandGainsDb.Count && a.BandGainsDb.Zip(b.BandGainsDb).All(p => Math.Abs(p.First - p.Second) < 0.5);

    /// <summary>Applies a profile (validated), persists the result immediately.</summary>
    public async Task<ApplyResult> ApplyAsync(AudioProfile profile, bool allowOverSafeLimit = false, CancellationToken ct = default)
    {
        var caps = await _engine.DetectCapabilitiesAsync(ct);
        var s = ToEngineBands(profile, caps);
        var issues = SettingsValidator.Validate(s, caps, !allowOverSafeLimit);
        if (issues.Count > 0) return ApplyResult.Fail(string.Join("; ", issues.Select(i => $"{i.Field}: {i.Message}")));
        var r = await _engine.ApplyAsync(s, ct);
        profile.Driver = caps.Engine.ToString();
        profile.HardwareId = _hardwareId;
        profile.UpdatedUtc = DateTime.UtcNow;
        profile.Sync = !r.Success ? SyncState.Failed : !r.AffectsRealAudio ? SyncState.ManualOnly : r.Verified ? SyncState.Synced : SyncState.Pending;
        await _repo.SaveAsync(profile, r.Success ? "applied" : "apply failed: " + r.Message, ct);
        if (r.Success) await _repo.SetActiveProfileIdAsync(profile.Id, ct);
        return r;
    }

    public async Task SaveEditsAsync(AudioProfile profile, string note, CancellationToken ct = default)
    {
        profile.UpdatedUtc = DateTime.UtcNow;
        await _repo.SaveAsync(profile, note, ct);
    }

    public async Task MarkStableAsync(AudioProfile profile, CancellationToken ct = default)
    {
        await _engine.SaveAsync(ct);
        await _repo.SetLastStableAsync(profile, ct);
    }

    public async Task<ApplyResult> ReturnToLastStableAsync(CancellationToken ct = default)
    {
        var stable = await _repo.GetLastStableAsync(ct);
        if (stable == null) return ApplyResult.Fail("Nenhum ajuste seguro registrado.");
        return await ApplyAsync(stable, true, ct);
    }

    /// <summary>Restores the most recent history snapshot (undo).</summary>
    public async Task<AudioProfile?> UndoAsync(Guid profileId, CancellationToken ct = default)
    {
        var h = (await _repo.GetHistoryAsync(profileId, 1, ct)).FirstOrDefault();
        if (h == null) return null;
        await _repo.SaveAsync(h.Snapshot, "undo", ct);
        return h.Snapshot;
    }

    // ---- Import / export ----
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string Export(AudioProfile p) => JsonSerializer.Serialize(p, Json);

    public static (AudioProfile? Profile, string? Error) Import(string json, AudioCapabilities? caps = null)
    {
        try
        {
            var p = JsonSerializer.Deserialize<AudioProfile>(json, Json);
            if (p == null) return (null, "Empty profile.");
            if (p.SchemaVersion > AudioProfile.CurrentSchemaVersion) return (null, "Unsupported newer schema version.");
            if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80) return (null, "Invalid name.");
            if (p.Settings.BandGainsDb.Count is 0 or > 64) return (null, "Invalid band count.");
            if (p.Settings.BandGainsDb.Any(g => !double.IsFinite(g) || Math.Abs(g) > 24)) return (null, "Gain outside plausible range.");
            if (p.BandCentersHz.Count != p.Settings.BandGainsDb.Count || p.BandCentersHz.Any(f => !(f > 0 && f < 100000)) || p.BandCentersHz.Zip(p.BandCentersHz.Skip(1)).Any(x => x.Second <= x.First))
                return (null, "Invalid band frequencies.");
            p.Id = Guid.NewGuid();
            p.IsBuiltIn = false;
            p.Sync = SyncState.Unknown;
            return (p, null);
        }
        catch (JsonException ex) { return (null, "Invalid JSON: " + ex.Message); }
    }
}
