using CeratoSoundAI.Domain;

namespace CeratoSoundAI.Application.Calibration;

public sealed record CalibrationGate(bool Allowed, string Reason);

public static class CalibrationSafety
{
    /// <summary>Hard ceiling for test signal level (dBFS). Prevents speaker/hearing damage.</summary>
    public const double MaxTestLevelDbfs = -20;
    public const double MaxDurationSeconds = 10;

    public static CalibrationGate CanRun(IVehicleState vehicle) => vehicle.IsMoving switch
    {
        true => new(false, "Veículo em movimento. Calibração só com o veículo estacionado."),
        null => new(false, "Não há informação confiável de movimento. Confirme manualmente que o veículo está estacionado."),
        _ => new(true, "")
    };

    public static double ClampLevel(double dbfs) => Math.Min(dbfs, MaxTestLevelDbfs);

    /// <summary>Logarithmic sine sweep, mono, 16-bit PCM samples. Amplitude is capped by MaxTestLevelDbfs.</summary>
    public static short[] GenerateSweep(double f1, double f2, double seconds, int sampleRate, double levelDbfs)
    {
        if (f1 <= 0 || f2 <= f1 || f2 >= sampleRate / 2.0) throw new ArgumentException("Invalid sweep range.");
        seconds = Math.Clamp(seconds, 0.5, MaxDurationSeconds);
        double amp = Math.Pow(10, ClampLevel(levelDbfs) / 20.0) * short.MaxValue;
        int n = (int)(seconds * sampleRate);
        var buf = new short[n];
        double k = Math.Log(f2 / f1);
        int fade = Math.Min(n / 10, sampleRate / 20);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            double phase = 2 * Math.PI * f1 * seconds / k * (Math.Exp(t / seconds * k) - 1);
            double env = i < fade ? i / (double)fade : i > n - fade ? (n - i) / (double)fade : 1;
            buf[i] = (short)Math.Round(Math.Sin(phase) * amp * env);
        }
        return buf;
    }

    /// <summary>Fraction of samples at/near full scale.</summary>
    public static double ClippingRatio(IReadOnlyList<short> samples, int threshold = 32700) =>
        samples.Count == 0 ? 0 : samples.Count(s => Math.Abs((int)s) >= threshold) / (double)samples.Count;
}

public sealed record MicrophoneInfo(string Description, bool IsExternal, bool IsCalibrated);

public static class MicrophonePolicy
{
    /// <summary>No microphone is treated as calibrated unless the user supplies a calibration file (future work).</summary>
    public static string Describe(MicrophoneInfo m) => m.IsCalibrated
        ? "Microfone com arquivo de calibração."
        : m.IsExternal
            ? "Microfone externo NÃO calibrado: medições servem para comparação relativa, não resposta absoluta."
            : "Microfone interno/celular NÃO é calibrado: não use como medição de resposta em frequência.";
}
