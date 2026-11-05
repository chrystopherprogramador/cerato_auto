using Android.Media.Audiofx;
using CeratoSoundAI.Application;
using CeratoSoundAI.Application.Audio;
using CeratoSoundAI.Domain;

namespace CeratoSoundAI.App.Platforms.AndroidImpl;

/// <summary>
/// Uses the public android.media.audiofx.Equalizer attached to the global output mix (session 0).
/// Whether the head unit's audio chain honors session-0 effects is NOT assumed: it is probed (create, hasControl,
/// read bands, no-op write + readback) and must still be confirmed by ear/measurement in the vehicle.
/// </summary>
public sealed class AndroidAudioEngine : AudioEngineBase
{
    private Equalizer? _eq;
    private string? _openError;
    private readonly object _lock = new();

    public override EngineKind Kind => EngineKind.AndroidEffect;

    private Equalizer? Open()
    {
        lock (_lock)
        {
            if (_eq != null) return _eq;
            try { _eq = new Equalizer(0, 0); _openError = null; }
            catch (Exception ex) { _openError = $"{ex.GetType().Name}: {ex.Message}"; _eq = null; }
            return _eq;
        }
    }

    public void Release()
    {
        lock (_lock) { try { _eq?.Release(); } catch { } _eq = null; }
    }

    protected override Task<AudioCapabilities> ProbeAsync(CancellationToken ct) => Task.Run(() =>
    {
        var eq = Open();
        if (eq == null)
            return AudioCapabilities.None(Kind, $"Não foi possível criar Equalizer na sessão global (0). {_openError}");

        var notes = new List<string>();
        try
        {
            bool control = eq.HasControl;
            int n = eq.NumberOfBands;
            var range = eq.GetBandLevelRange();
            var bands = new List<BandInfo>();
            for (short i = 0; i < n; i++)
            {
                var fr = eq.GetBandFreqRange(i);
                bands.Add(new BandInfo(i, FrequencyMath.MilliHzToHz(eq.GetCenterFreq(i)),
                    FrequencyMath.MilliHzToHz(fr[0]), FrequencyMath.MilliHzToHz(fr[1])));
            }
            if (!control) notes.Add("Outro processo detém o controle do efeito (conflito): ajustes podem não ter efeito.");
            notes.Add("O Android aceitar o efeito NÃO prova que o som da central mudou. Confirme no veículo.");
            return new AudioCapabilities
            {
                Engine = Kind,
                Level = control && n > 0 ? ControlLevel.Limited : ControlLevel.Unavailable,
                AffectsRealAudio = control && n > 0,
                Bands = bands,
                MinGainDb = FrequencyMath.MilliBelToDb(range[0]),
                MaxGainDb = FrequencyMath.MilliBelToDb(range[1]),
                SupportsReadback = true,
                SupportsPreamp = false, SupportsBalanceFader = false, SupportsParametric = false,
                Notes = notes
            };
        }
        catch (Exception ex)
        {
            Release();
            return AudioCapabilities.None(Kind, $"Falha ao consultar o Equalizer: {ex.GetType().Name}: {ex.Message}");
        }
    }, ct);

    protected override Task<ApplyResult> ApplyCoreAsync(EqSettings s, AudioCapabilities caps, CancellationToken ct) => Task.Run(() =>
    {
        var eq = Open();
        if (eq == null) return ApplyResult.Fail($"Equalizer indisponível. {_openError}");
        try
        {
            if (!eq.HasControl) return ApplyResult.Fail("Sem controle do efeito (outro app o detém).");
            eq.SetEnabled(true);
            for (short i = 0; i < s.BandGainsDb.Count; i++)
                eq.SetBandLevel(i, FrequencyMath.DbToMilliBel(s.BandGainsDb[i]));
            var back = ReadSync(eq);
            bool verified = eq.Enabled && back != null && back.BandGainsDb.Zip(s.BandGainsDb).All(p => Math.Abs(p.First - p.Second) <= 0.6);
            return new ApplyResult(true, verified, true,
                verified ? "Efeito aceitou e confirmou os valores (leitura de volta). Efeito audível ainda deve ser conferido."
                         : "Valores enviados, porém a leitura de volta não confirmou.", back);
        }
        catch (Exception ex)
        {
            Release();
            return ApplyResult.Fail($"Erro ao aplicar: {ex.GetType().Name}: {ex.Message}");
        }
    }, ct);

    private static EqSettings? ReadSync(Equalizer eq)
    {
        try
        {
            var s = new EqSettings();
            for (short i = 0; i < eq.NumberOfBands; i++) s.BandGainsDb.Add(FrequencyMath.MilliBelToDb(eq.GetBandLevel(i)));
            return s;
        }
        catch { return null; }
    }

    public override Task<EqSettings?> ReadCurrentAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var eq = Open();
        return eq == null ? null : ReadSync(eq);
    }, ct);

    /// <summary>Safe write probe: writes the band's own current value back (no audible change) and reads it again.</summary>
    public ProbeResult WriteProbe()
    {
        var eq = Open();
        if (eq == null) return new("Equalizer(session 0)", true, false, false, _openError ?? "não criado");
        try
        {
            if (eq.NumberOfBands == 0) return new("Equalizer(session 0)", true, true, false, "0 bandas");
            var before = eq.GetBandLevel(0);
            eq.SetBandLevel(0, before);
            var after = eq.GetBandLevel(0);
            return new("Equalizer(session 0)", true, true, after == before,
                $"hasControl={eq.HasControl}, enabled={eq.Enabled}, bandas={eq.NumberOfBands}, no-op write/readback {(after == before ? "OK" : "divergiu")}");
        }
        catch (Exception ex) { return new("Equalizer(session 0)", true, false, false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    public override async Task<IReadOnlyList<AudioConflict>> DetectConflictsAsync(CancellationToken ct = default)
    {
        var list = new List<AudioConflict>();
        await DetectCapabilitiesAsync(ct);
        var eq = Open();
        try { if (eq != null && !eq.HasControl) list.Add(new("AudioEffect", "Outro processo controla o equalizador da sessão global.", true)); } catch { }
        list.Add(new("Equalizador original da central", "Não é possível ler ou desativar o equalizador/DSP original. Processamento duplo é possível e NÃO pode ser descartado pelo app.", false));
        return list;
    }
}
