using Android.Media;
using CeratoSoundAI.Application.Calibration;

namespace CeratoSoundAI.App.Platforms.AndroidImpl;

public sealed record CaptureCapability(int SampleRate, bool Supported, int MinBuffer);

public interface ICalibrationPlatform
{
    IReadOnlyList<CaptureCapability> ProbeCapture();
    IReadOnlyList<string> InputDevices();
    Task PlayTestSweepAsync(double requestedDbfs, CancellationToken ct);
}

public sealed class AndroidCalibrationPlatform : ICalibrationPlatform
{
    public IReadOnlyList<CaptureCapability> ProbeCapture() =>
        new[] { 16000, 44100, 48000 }.Select(sr =>
        {
            int min = AudioRecord.GetMinBufferSize(sr, ChannelIn.Mono, Encoding.Pcm16bit);
            return new CaptureCapability(sr, min > 0, min);
        }).ToList();

    public IReadOnlyList<string> InputDevices()
    {
        var am = (AudioManager?)Android.App.Application.Context.GetSystemService(Android.Content.Context.AudioService);
        if (am == null || (int)Android.OS.Build.VERSION.SdkInt < 23) return new[] { "Lista de entradas indisponível nesta versão do Android." };
        return (am.GetDevices(GetDevicesTargets.Inputs) ?? Array.Empty<AudioDeviceInfo>()).Select(d => $"{d.Type} {d.ProductName}").ToList();
    }

    /// <summary>Plays a capped logarithmic sweep through the real output. Level is hard-limited by CalibrationSafety.</summary>
    public async Task PlayTestSweepAsync(double requestedDbfs, CancellationToken ct)
    {
        const int sr = 48000;
        var data = CalibrationSafety.GenerateSweep(40, 16000, 6, sr, requestedDbfs);
        var track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!)!
            .SetAudioFormat(new AudioFormat.Builder()!.SetEncoding(Encoding.Pcm16bit)!.SetSampleRate(sr)!.SetChannelMask(ChannelOut.Mono)!.Build()!)!
            .SetBufferSizeInBytes(data.Length * 2)!
            .SetTransferMode(AudioTrackMode.Static)!
            .Build();
        try
        {
            track.Write(data, 0, data.Length);
            track.Play();
            await Task.Delay(TimeSpan.FromSeconds(6.3), ct);
        }
        finally { try { track.Stop(); } catch { } track.Release(); }
    }
}
