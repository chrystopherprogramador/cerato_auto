using CeratoSoundAI.Application;
using CeratoSoundAI.Application.Assistant;
using CeratoSoundAI.Application.Audio;
using CeratoSoundAI.Application.Calibration;
using CeratoSoundAI.Application.Diagnostics;
using CeratoSoundAI.Application.Profiles;
using CeratoSoundAI.Domain;
using CeratoSoundAI.Infrastructure.Persistence;
using Xunit;

namespace CeratoSoundAI.Tests;

public class FrequencyAndValidationTests
{
    [Fact] public void MilliHz_and_millibel_convert() 
    {
        Assert.Equal(60.0, FrequencyMath.MilliHzToHz(60000));
        Assert.Equal(-1500, FrequencyMath.DbToMilliBel(-15));
        Assert.Equal(12.5, FrequencyMath.MilliBelToDb(1250));
        Assert.Equal(1000, FrequencyMath.OctavesToHz(FrequencyMath.HzToOctaves(1000)), 6);
    }

    [Fact] public void Remap_interpolates_in_log_frequency_and_holds_edges()
    {
        var r = FrequencyMath.Remap(new[] { 100.0, 400.0 }, new[] { 0.0, 4.0 }, new[] { 50.0, 200.0, 800.0 });
        Assert.Equal(0, r[0]); Assert.Equal(2, r[1], 6); Assert.Equal(4, r[2]);
    }

    private static AudioCapabilities Caps(int bands = 5) => new()
    {
        Engine = EngineKind.AndroidEffect, Level = ControlLevel.Active, AffectsRealAudio = true,
        Bands = Enumerable.Range(0, bands).Select(i => new BandInfo(i, 60 * Math.Pow(4, i), 30, 120)).ToList(),
        MinGainDb = -15, MaxGainDb = 15
    };

    [Fact] public void Validator_rejects_wrong_band_count_range_and_unsupported_controls()
    {
        var s = new EqSettings { BandGainsDb = new() { 0, 0, 20 }, PreampDb = 3, Balance = 0.5 };
        var issues = SettingsValidator.Validate(s, Caps());
        Assert.Contains(issues, i => i.Field == "bands");
        Assert.Contains(issues, i => i.Field == "preamp");
        Assert.Contains(issues, i => i.Field == "balance/fader");
        var s2 = EqSettings.Flat(5); s2.BandGainsDb[1] = 9;
        Assert.Contains(SettingsValidator.Validate(s2, Caps()), i => i.Message.Contains("safe limit"));
        Assert.DoesNotContain(SettingsValidator.Validate(s2, Caps(), false), i => i.Message.Contains("safe limit"));
    }

    [Fact] public void Clamp_handles_nan()
    {
        var s = new EqSettings { BandGainsDb = new() { double.NaN, 99, -99, 0, 0 } };
        var c = SettingsValidator.Clamp(s, Caps());
        Assert.Equal(new[] { 0, 15, -15, 0, 0 }, c.BandGainsDb.Select(x => (int)x));
    }
}

public class EngineTests
{
    [Fact] public async Task Simulation_never_claims_real_audio()
    {
        var e = new SimulationAudioEngine();
        var caps = await e.DetectCapabilitiesAsync();
        Assert.False(caps.AffectsRealAudio);
        var r = await e.ApplyAsync(EqSettings.Flat(caps.BandCount));
        Assert.True(r.Success); Assert.False(r.AffectsRealAudio);
    }

    [Fact] public async Task Rollback_returns_to_previous_settings_and_restore_to_stable()
    {
        var e = new SimulationAudioEngine();
        var n = (await e.DetectCapabilitiesAsync()).BandCount;
        EqSettings S(double g) { var s = EqSettings.Flat(n); s.BandGainsDb[2] = g; return s; }
        await e.ApplyAsync(S(1)); await e.SaveAsync();
        await e.ApplyAsync(S(3));
        await e.RollbackAsync();
        Assert.Equal(1, (await e.ReadCurrentAsync())!.BandGainsDb[2]);
        await e.ApplyAsync(S(5));
        await e.RestoreAsync();
        Assert.Equal(1, (await e.ReadCurrentAsync())!.BandGainsDb[2]);
    }

    [Fact] public async Task Rollback_without_history_fails_safely()
        => Assert.False((await new SimulationAudioEngine().RollbackAsync()).Success);

    [Fact] public async Task Engine_rejects_out_of_range_without_changing_state()
    {
        var e = new SimulationAudioEngine();
        var n = (await e.DetectCapabilitiesAsync()).BandCount;
        var bad = EqSettings.Flat(n); bad.BandGainsDb[0] = 40;
        Assert.False((await e.ApplyAsync(bad)).Success);
        Assert.Null(await e.ReadCurrentAsync());
    }

    [Fact] public async Task NativeDsp_is_unavailable_and_refuses()
    {
        var e = new NativeDspAudioEngine();
        Assert.Equal(ControlLevel.Unavailable, (await e.DetectCapabilitiesAsync()).Level);
        Assert.False((await e.ApplyAsync(EqSettings.Flat(0))).Success);
    }

    [Fact] public async Task Manual_engine_renders_instructions_and_is_not_real()
    {
        var m = new ManualDspAudioEngine(new[] { 100.0, 1000.0, 10000.0 });
        var caps = await m.DetectCapabilitiesAsync();
        Assert.False(caps.AffectsRealAudio);
        var r = await m.ApplyAsync(new EqSettings { BandGainsDb = new() { 1.5, 0, -2 } });
        Assert.False(r.AffectsRealAudio);
        Assert.Contains("1k Hz: 0 dB", m.RenderInstructions(m.LastInstructions!));
    }
}

public class PersistenceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cerato_{Guid.NewGuid():N}.db");
    public void Dispose() { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_path) + "*")) try { File.Delete(f); } catch { } }

    private async Task<(ProfileService, SqliteProfileRepository, SimulationAudioEngine)> Setup()
    {
        var repo = new SqliteProfileRepository(_path);
        var eng = new SimulationAudioEngine();
        var svc = new ProfileService(repo, eng, "test-hw");
        await svc.InitializeAsync();
        return (svc, repo, eng);
    }

    [Fact] public async Task Seeds_eight_profiles_once_and_survives_reopen()
    {
        var (_, repo, _) = await Setup();
        Assert.Equal(8, (await repo.GetAllAsync()).Count);
        var (_, repo2, _) = await Setup(); // simulated restart
        Assert.Equal(8, (await repo2.GetAllAsync()).Count);
        Assert.All(await repo2.GetAllAsync(), p => Assert.False(p.IsCalibrated));
    }

    [Fact] public async Task Save_keeps_history_and_undo_restores_previous()
    {
        var (svc, repo, _) = await Setup();
        var p = (await repo.GetAllAsync()).First(x => x.Name == "Personalizado");
        p.Settings.BandGainsDb[3] = 2; await svc.SaveEditsAsync(p, "edit1");
        p.Settings.BandGainsDb[3] = 4; await svc.SaveEditsAsync(p, "edit2");
        Assert.Equal(2, (await repo.GetHistoryAsync(p.Id)).Count);
        var undone = await svc.UndoAsync(p.Id);
        Assert.Equal(2, undone!.Settings.BandGainsDb[3]);
        Assert.Equal(2, (await repo.GetAsync(p.Id))!.Settings.BandGainsDb[3]);
    }

    [Fact] public async Task Apply_persists_active_and_marks_simulation_as_manual_only()
    {
        var (svc, repo, _) = await Setup();
        var p = (await repo.GetAllAsync()).First(x => x.Name == "Rock Clássico");
        var r = await svc.ApplyAsync(p);
        Assert.True(r.Success);
        var (_, repo2, _) = await Setup();
        Assert.Equal(p.Id, await repo2.GetActiveProfileIdAsync());
        Assert.Equal(SyncState.ManualOnly, (await repo2.GetAsync(p.Id))!.Sync);
    }

    [Fact] public async Task Startup_does_not_claim_application_without_real_control()
    {
        var (svc, repo, _) = await Setup();
        await svc.ApplyAsync((await repo.GetAllAsync()).First());
        var res = await svc.StartupAsync();
        Assert.False(res.Applied);
        Assert.Contains("Sem controle real", res.Summary);
    }

    [Fact] public async Task Startup_reapplies_with_fake_real_engine_and_verifies()
    {
        var repo = new SqliteProfileRepository(_path);
        var eng = new FakeRealEngine();
        var svc = new ProfileService(repo, eng, "hw");
        await svc.InitializeAsync();
        var p = (await repo.GetAllAsync()).First(x => x.Name == "Heavy Metal");
        await svc.ApplyAsync(p);
        eng.Reset(); // simulate reboot: real state lost
        var res = await svc.StartupAsync();
        Assert.True(res.Applied && res.Verified);
        Assert.Equal(SyncState.Synced, (await repo.GetAsync(p.Id))!.Sync);
    }

    [Fact] public async Task Stable_profile_roundtrip()
    {
        var (svc, repo, _) = await Setup();
        var p = (await repo.GetAllAsync()).First();
        await svc.MarkStableAsync(p);
        Assert.Equal(p.Id, (await repo.GetLastStableAsync())!.Id);
    }

    [Fact] public async Task Concurrent_saves_do_not_corrupt()
    {
        var (svc, repo, _) = await Setup();
        var p = (await repo.GetAllAsync()).First();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => svc.SaveEditsAsync(p, "c" + i)));
        Assert.NotNull(await repo.GetAsync(p.Id));
    }

    [Fact] public void Export_import_validates()
    {
        var p = BuiltInProfiles.Create()[1];
        var (ok, err) = ProfileService.Import(ProfileService.Export(p));
        Assert.Null(err); Assert.NotEqual(p.Id, ok!.Id); Assert.False(ok.IsBuiltIn);
        Assert.NotNull(ProfileService.Import("{ not json").Error);
        p.Settings.BandGainsDb[0] = 99;
        Assert.NotNull(ProfileService.Import(ProfileService.Export(p)).Error);
        var q = BuiltInProfiles.Create()[1]; q.BandCentersHz.RemoveAt(0);
        Assert.NotNull(ProfileService.Import(ProfileService.Export(q)).Error);
    }
}

/// <summary>Test double standing in for a real, readable engine (only used in tests).</summary>
internal sealed class FakeRealEngine : AudioEngineBase
{
    private EqSettings? _real;
    public override EngineKind Kind => EngineKind.AndroidEffect;
    public void Reset() => _real = null;
    protected override Task<AudioCapabilities> ProbeAsync(CancellationToken ct) => Task.FromResult(new AudioCapabilities
    {
        Engine = Kind, Level = ControlLevel.Active, AffectsRealAudio = true, SupportsReadback = true, MinGainDb = -15, MaxGainDb = 15,
        Bands = new[] { 60.0, 230, 910, 3600, 14000 }.Select((f, i) => new BandInfo(i, f, f / 2, f * 2)).ToList()
    });
    protected override Task<ApplyResult> ApplyCoreAsync(EqSettings s, AudioCapabilities caps, CancellationToken ct)
    { _real = s.Clone(); return Task.FromResult(new ApplyResult(true, true, true, "ok", _real)); }
    public override Task<EqSettings?> ReadCurrentAsync(CancellationToken ct = default) => Task.FromResult(_real?.Clone());
}

public class AssistantTests
{
    private static AudioCapabilities Caps() => new()
    {
        Engine = EngineKind.AndroidEffect, Level = ControlLevel.Active, AffectsRealAudio = true, MinGainDb = -10, MaxGainDb = 10,
        Bands = new[] { 60.0, 230, 910, 3600, 14000 }.Select((f, i) => new BandInfo(i, f, f / 2, f * 2)).ToList()
    };

    [Fact] public void Valid_proposal_maps_to_nearest_band_and_builds_comparison()
    {
        var reply = "Vou realçar.\n```json\n{\"explanation\":\"x\",\"operations\":[{\"op\":\"set_band\",\"frequencyHz\":3500,\"gainDb\":2.5}]}\n```";
        var p = ProposalParser.Parse(reply, Caps(), EqSettings.Flat(5));
        Assert.True(p.IsApplicable);
        Assert.Equal(2.5, p.Proposed!.BandGainsDb[3]);
        Assert.Equal(5, p.Comparison.Count);
    }

    [Theory]
    [InlineData("{\"operations\":[{\"op\":\"run_shell\",\"cmd\":\"rm -rf /\"}]}")]
    [InlineData("{\"operations\":[{\"op\":\"set_band\",\"frequencyHz\":3500,\"gainDb\":30}]}")]
    [InlineData("{\"operations\":[{\"op\":\"set_band\",\"frequencyHz\":20,\"gainDb\":1}]}")]
    [InlineData("{\"operations\":[{\"op\":\"set_preamp\",\"gainDb\":2}]}")]
    [InlineData("{\"operations\":[{\"op\":\"set_band\",\"frequencyHz\":910,\"gainDb\":8}]}")]
    public void Invalid_proposals_are_blocked(string json)
    {
        var p = ProposalParser.Parse("```json\n" + json + "\n```", Caps(), EqSettings.Flat(5));
        Assert.False(p.IsApplicable);
        Assert.NotEmpty(p.Issues);
    }

    [Fact] public void Plain_conversation_has_no_proposal_and_garbage_json_is_ignored()
    {
        Assert.False(ProposalParser.Parse("Só uma explicação.", Caps(), EqSettings.Flat(5)).IsApplicable);
        Assert.False(ProposalParser.Parse("```json\n{oops\n```", Caps(), EqSettings.Flat(5)).IsApplicable);
    }

    [Fact] public async Task Unconfigured_provider_degrades_gracefully()
    {
        var svc = new AssistantService(new OfflineLlm());
        var r = await svc.AskAsync("m", "oi", Caps(), EqSettings.Flat(5), false);
        Assert.Contains("offline", r.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class OfflineLlm : ILlmProvider
    {
        public bool IsConfigured => false;
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<string> ChatAsync(string m, IReadOnlyList<ChatMessage> x, CancellationToken ct = default) => throw new InvalidOperationException();
    }
}

public class CalibrationAndSanitizerTests
{
    [Fact] public void Test_signal_level_is_capped()
    {
        var s = CalibrationSafety.GenerateSweep(20, 20000, 2, 48000, 0); // request 0 dBFS
        var max = s.Max(x => Math.Abs((int)x));
        Assert.True(max <= 32767 * Math.Pow(10, CalibrationSafety.MaxTestLevelDbfs / 20) + 1);
        Assert.Equal(0, CalibrationSafety.ClippingRatio(s));
    }

    [Fact] public void Calibration_blocked_when_moving_or_unknown()
    {
        Assert.False(CalibrationSafety.CanRun(new V(true)).Allowed);
        Assert.False(CalibrationSafety.CanRun(new V(null)).Allowed);
        Assert.True(CalibrationSafety.CanRun(new V(false)).Allowed);
    }
    private sealed record V(bool? IsMoving) : IVehicleState;

    [Fact] public void Sanitizer_removes_sensitive_values()
    {
        var r = new HardwareReport();
        r.Device["serial"] = "ABC"; r.Device["model"] = "X"; r.Device["note"] = "aa:bb:cc:dd:ee:ff at 192.168.0.5";
        var s = ReportSanitizer.Sanitize(r);
        Assert.False(s.Device.ContainsKey("serial"));
        Assert.Equal("[mac] at [ip]", s.Device["note"]);
        Assert.True(s.Sanitized);
    }
}
