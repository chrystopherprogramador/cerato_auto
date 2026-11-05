using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.Audiofx;
using Android.OS;
using CeratoSoundAI.Application.Diagnostics;
using CeratoSoundAI.Domain;
using Java.IO;

namespace CeratoSoundAI.App.Platforms.AndroidImpl;

public sealed class AndroidHardwareDiagnostics : IHardwareDiagnostics
{
    private static readonly string[] Keywords =
        { "audio", "eq", "dsp", "sound", "equal", "wintech", "mcu", "can", "mtk", "ak7", "tda", "fm", "amp", "dolby", "bose", "harman", "xda", "hct", "px5", "px6", "syu", "ts10", "ac8" };
    private static readonly string[] EqHints = { "equalizer", "eq", "dsp", "sound", "audio", "effect" };

    private readonly AndroidAudioEngine _engine;
    public AndroidHardwareDiagnostics(AndroidAudioEngine engine) => _engine = engine;
    public IReadOnlyList<string> ReadOnlyAdbCommands => ReadOnlyAdb.Commands;

    public Task<HardwareReport> CollectAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var r = new HardwareReport();
        var ctx = Android.App.Application.Context;

        void D(string k, string? v) => r.Device[k] = v ?? "";
        D("manufacturer", Build.Manufacturer); D("brand", Build.Brand); D("model", Build.Model); D("device", Build.Device);
        D("product", Build.Product); D("board", Build.Board); D("hardware", Build.Hardware);
        D("fingerprint", Build.Fingerprint); D("display", Build.Display); D("bootloader", Build.Bootloader);
        D("release", Build.VERSION.Release); D("sdk_int", ((int)Build.VERSION.SdkInt).ToString());
        D("security_patch", (int)Build.VERSION.SdkInt >= 23 ? Build.VERSION.SecurityPatch : "n/d");
        D("abis", string.Join(",", Build.SupportedAbis ?? Array.Empty<string>()));
        D("cpu_count", System.Environment.ProcessorCount.ToString());
        D("radio_version", Build.RadioVersion);
        D("mcu_hint", ReadProp("ro.mcu.version") ?? ReadProp("ro.build.mcu") ?? "não exposta (ro.mcu.version/ro.build.mcu vazias)");
        D("soc_platform_prop", ReadProp("ro.board.platform") ?? "n/d");
        D("cpuinfo_hardware", ReadCpuInfoField("Hardware") ?? "n/d");
        D("cpuinfo_model_name", ReadCpuInfoField("model name") ?? ReadCpuInfoField("Processor") ?? "n/d");

        try
        {
            var am = (ActivityManager?)ctx.GetSystemService(Context.ActivityService);
            var mi = new ActivityManager.MemoryInfo();
            am?.GetMemoryInfo(mi);
            r.Memory["total_mb"] = (mi.TotalMem / 1048576).ToString();
            r.Memory["avail_mb"] = (mi.AvailMem / 1048576).ToString();
            r.Memory["low_memory"] = mi.LowMemory.ToString();
        }
        catch (System.Exception ex) { r.Memory["error"] = ex.Message; }

        try
        {
            var pm = ctx.PackageManager!;
            foreach (var p in pm.GetInstalledPackages(0))
            {
                var n = p.PackageName ?? "";
                if (Keywords.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    r.AudioPackages.Add(n);
                    if (EqHints.Any(h => n.Contains(h, StringComparison.OrdinalIgnoreCase))) r.OriginalEqCandidates.Add(n);
                }
            }
            r.AudioPackages.Sort(); r.OriginalEqCandidates.Sort();
        }
        catch (System.Exception ex) { r.Findings.Add("Lista de pacotes indisponível: " + ex.Message); }

        try
        {
            foreach (var d in AudioEffect.QueryEffects() ?? Array.Empty<AudioEffect.Descriptor>())
                r.Effects.Add(new EffectInfo(d.Name ?? "", d.Type?.ToString() ?? "", d.Uuid?.ToString() ?? "", d.Implementor ?? ""));
        }
        catch (System.Exception ex) { r.Findings.Add("QueryEffects falhou: " + ex.Message); }

        try
        {
            var am = (AudioManager?)ctx.GetSystemService(Context.AudioService);
            if (am != null)
            {
                r.AudioManager["mode"] = am.Mode.ToString();
                r.AudioManager["music_active"] = am.IsMusicActive.ToString();
                r.AudioManager["speakerphone"] = am.SpeakerphoneOn.ToString();
                r.AudioManager["a2dp_on"] = am.BluetoothA2dpOn.ToString();
                r.AudioManager["music_volume"] = $"{am.GetStreamVolume(Android.Media.Stream.Music)}/{am.GetStreamMaxVolume(Android.Media.Stream.Music)}";
                r.AudioManager["output_sample_rate"] = am.GetProperty(AudioManager.PropertyOutputSampleRate) ?? "n/d";
                r.AudioManager["output_frames_per_buffer"] = am.GetProperty(AudioManager.PropertyOutputFramesPerBuffer) ?? "n/d";
                if ((int)Build.VERSION.SdkInt >= 23)
                    foreach (var dev in am.GetDevices(GetDevicesTargets.Outputs) ?? Array.Empty<AudioDeviceInfo>())
                        r.OutputDevices.Add($"{dev.Type} (id {dev.Id}) {dev.ProductName}");
            }
        }
        catch (System.Exception ex) { r.Findings.Add("AudioManager falhou: " + ex.Message); }

        r.Probes.Add(_engine.WriteProbe());
        r.Probes.Add(new ProbeResult("MCU / DSP proprietário", false, false, false, "Não testado: nenhuma interface documentada. Requer identificação da MCU e testes no veículo."));
        r.Probes.Add(new ProbeResult("Equalizador original (estado)", false, false, false, "Estado não legível por APIs públicas."));

        r.Findings.Add(r.OriginalEqCandidates.Count > 0
            ? $"Pacotes candidatos ao equalizador/áudio original: {string.Join(", ", r.OriginalEqCandidates)} (HIPÓTESE; confirmar)."
            : "Nenhum pacote candidato ao equalizador original foi identificado por palavra-chave.");
        var ok = r.Probes[0];
        r.Findings.Add(ok.ReadOk && ok.WriteOk
            ? "O Equalizer Android da sessão global aceita leitura/escrita no objeto de efeito. Isso NÃO prova mudança audível; teste de ouvido/medição necessário."
            : "O Equalizer Android da sessão global não ficou acessível: " + ok.Detail);
        return r;
    }, ct);

    private static string? ReadProp(string name)
    {
        try
        {
            var cls = Java.Lang.Class.ForName("android.os.SystemProperties");
            var m = cls.GetMethod("get", Java.Lang.Class.FromType(typeof(Java.Lang.String)));
            var v = m?.Invoke(null, new Java.Lang.String(name))?.ToString();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    private static string? ReadCpuInfoField(string field)
    {
        try
        {
            using var rd = new BufferedReader(new FileReader("/proc/cpuinfo"));
            string? line;
            while ((line = rd.ReadLine()) != null)
                if (line.StartsWith(field, StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                    return line[(line.IndexOf(':') + 1)..].Trim();
        }
        catch { }
        return null;
    }
}
