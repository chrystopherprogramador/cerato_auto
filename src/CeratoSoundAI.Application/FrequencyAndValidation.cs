using CeratoSoundAI.Domain;

namespace CeratoSoundAI.Application;

public static class FrequencyMath
{
    /// <summary>ISO 1/3-octave... reference 10-band octave centers used only to describe built-in profile shapes.</summary>
    public static readonly double[] OctaveCenters = { 31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public static double HzToOctaves(double hz, double refHz = 1000) => Math.Log2(hz / refHz);
    public static double OctavesToHz(double oct, double refHz = 1000) => refHz * Math.Pow(2, oct);

    /// <summary>Android reports center frequencies in milliHertz.</summary>
    public static double MilliHzToHz(int milliHz) => milliHz / 1000.0;
    public static int HzToMilliHz(double hz) => (int)Math.Round(hz * 1000.0);
    /// <summary>Android reports gains in millibels (1/100 dB).</summary>
    public static double MilliBelToDb(int mB) => mB / 100.0;
    public static short DbToMilliBel(double db) => (short)Math.Round(db * 100.0);

    public static string Format(double hz) => hz >= 1000 ? $"{hz / 1000:0.##}k" : $"{hz:0.#}";

    /// <summary>Log-frequency interpolation of a gain curve onto other centers. No extrapolation: edges are held.</summary>
    public static List<double> Remap(IReadOnlyList<double> srcCenters, IReadOnlyList<double> srcGains, IReadOnlyList<double> dstCenters)
    {
        if (srcCenters.Count != srcGains.Count || srcCenters.Count == 0)
            throw new ArgumentException("Invalid source curve.");
        var result = new List<double>(dstCenters.Count);
        foreach (var f in dstCenters)
        {
            if (f <= srcCenters[0]) { result.Add(srcGains[0]); continue; }
            if (f >= srcCenters[^1]) { result.Add(srcGains[^1]); continue; }
            int i = 0;
            while (srcCenters[i + 1] < f) i++;
            double t = (Math.Log(f) - Math.Log(srcCenters[i])) / (Math.Log(srcCenters[i + 1]) - Math.Log(srcCenters[i]));
            result.Add(srcGains[i] + (srcGains[i + 1] - srcGains[i]) * t);
        }
        return result;
    }

    /// <summary>Approximate smooth response (dB) at a frequency for drawing the curve; interpolation only, not a measurement.</summary>
    public static double CurveAt(IReadOnlyList<double> centers, IReadOnlyList<double> gains, double hz)
        => centers.Count == 0 ? 0 : Remap(centers, gains, new[] { hz })[0];
}

public sealed record ValidationIssue(string Field, string Message);

public static class SettingsValidator
{
    /// <summary>Default conservative ceiling for any boost, regardless of hardware range.</summary>
    public const double SafeBoostLimitDb = 6.0;

    public static IReadOnlyList<ValidationIssue> Validate(EqSettings s, AudioCapabilities caps, bool enforceSafeBoost = true)
    {
        var issues = new List<ValidationIssue>();
        if (caps.Level == ControlLevel.Unavailable && caps.Engine != EngineKind.Manual && caps.Engine != EngineKind.Simulation)
            issues.Add(new("engine", "No control available."));
        if (s.BandGainsDb.Count != caps.BandCount)
            issues.Add(new("bands", $"Expected {caps.BandCount} bands but got {s.BandGainsDb.Count}."));
        for (int i = 0; i < s.BandGainsDb.Count; i++)
        {
            var g = s.BandGainsDb[i];
            if (double.IsNaN(g) || double.IsInfinity(g)) { issues.Add(new($"band[{i}]", "Not a number.")); continue; }
            if (g < caps.MinGainDb || g > caps.MaxGainDb)
                issues.Add(new($"band[{i}]", $"{g:0.#} dB outside hardware range {caps.MinGainDb:0.#}..{caps.MaxGainDb:0.#} dB."));
            if (enforceSafeBoost && g > SafeBoostLimitDb)
                issues.Add(new($"band[{i}]", $"Boost above safe limit of +{SafeBoostLimitDb} dB needs explicit override."));
        }
        if (s.PreampDb != 0 && !caps.SupportsPreamp) issues.Add(new("preamp", "Preamp not supported by this engine."));
        if (caps.SupportsPreamp && (s.PreampDb > caps.MaxPreampDb || s.PreampDb < -24)) issues.Add(new("preamp", "Preamp out of range."));
        if ((s.Balance != 0 || s.Fader != 0) && !caps.SupportsBalanceFader) issues.Add(new("balance/fader", "Balance/fader not supported by this engine."));
        if (Math.Abs(s.Balance) > 1 || Math.Abs(s.Fader) > 1) issues.Add(new("balance/fader", "Must be within -1..1."));
        return issues;
    }

    public static EqSettings Clamp(EqSettings s, AudioCapabilities caps)
    {
        var c = s.Clone();
        for (int i = 0; i < c.BandGainsDb.Count; i++)
            c.BandGainsDb[i] = Math.Clamp(double.IsFinite(c.BandGainsDb[i]) ? c.BandGainsDb[i] : 0, caps.MinGainDb, caps.MaxGainDb);
        return c;
    }
}
