using System.Text.Json;
using System.Text.RegularExpressions;
using CeratoSoundAI.Domain;

namespace CeratoSoundAI.Application.Diagnostics;

public static class ReportSanitizer
{
    private static readonly string[] SensitiveKeys = { "serial", "android_id", "imei", "mac", "bluetooth_name", "device_name", "account", "email", "wifi", "ssid" };
    private static readonly Regex Mac = new("([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}", RegexOptions.Compiled);
    private static readonly Regex Ipv4 = new(@"\b\d{1,3}(\.\d{1,3}){3}\b", RegexOptions.Compiled);

    public static HardwareReport Sanitize(HardwareReport r)
    {
        string Clean(string v) => Ipv4.Replace(Mac.Replace(v, "[mac]"), "[ip]");
        Dictionary<string, string> Dict(Dictionary<string, string> d) => d
            .Where(kv => !SensitiveKeys.Any(k => kv.Key.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(kv => kv.Key, kv => Clean(kv.Value));
        return new HardwareReport
        {
            GeneratedUtc = r.GeneratedUtc, Sanitized = true,
            Device = Dict(r.Device), Memory = Dict(r.Memory), AudioManager = Dict(r.AudioManager),
            AudioPackages = r.AudioPackages.ToList(), OriginalEqCandidates = r.OriginalEqCandidates.ToList(),
            Effects = r.Effects.ToList(), OutputDevices = r.OutputDevices.Select(Clean).ToList(),
            Probes = r.Probes.Select(p => p with { Detail = Clean(p.Detail) }).ToList(),
            Findings = r.Findings.Select(Clean).ToList()
        };
    }

    public static string ToJson(HardwareReport r) =>
        JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true });
}

public static class ReadOnlyAdb
{
    public static readonly IReadOnlyList<string> Commands = new[]
    {
        "adb shell getprop ro.product.model",
        "adb shell getprop ro.build.fingerprint",
        "adb shell getprop ro.board.platform",
        "adb shell getprop ro.hardware",
        "adb shell getprop ro.product.cpu.abi",
        "adb shell cat /proc/cpuinfo",
        "adb shell cat /proc/meminfo",
        "adb shell dumpsys audio",
        "adb shell dumpsys media.audio_flinger",
        "adb shell dumpsys media.audio_policy",
        "adb shell pm list packages | findstr /i \"audio eq dsp sound equal wintech mcu\"",
        "adb shell dumpsys package <pacote-eq-original>",
        "adb shell dumpsys activity services | findstr /i \"audio mcu\"",
        "adb shell ls /dev | findstr /i \"ttyS mcu snd\"",
        "adb logcat -d -s AudioEffect:V AudioFlinger:V"
    };
}
