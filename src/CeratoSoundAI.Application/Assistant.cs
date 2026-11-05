using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CeratoSoundAI.Domain;

namespace CeratoSoundAI.Application.Assistant;

public sealed record ComparisonRow(string Band, double CurrentDb, double ProposedDb);

public sealed class AssistantProposal
{
    public string Explanation { get; set; } = "";
    public string? Limitations { get; set; }
    public bool RevertRequested { get; set; }
    public EqSettings? Proposed { get; set; }
    public List<string> Issues { get; } = new();
    public List<ComparisonRow> Comparison { get; } = new();
    /// <summary>True when nothing blocks showing the confirmation dialog. Application is ALWAYS user-confirmed.</summary>
    public bool IsApplicable => (Proposed != null || RevertRequested) && Issues.Count == 0;
    public bool IsSignificant => Proposed != null && Comparison.Any(c => Math.Abs(c.ProposedDb - c.CurrentDb) >= 3);
}

public static class AssistantPrompt
{
    public static string Build(AudioCapabilities caps, EqSettings current, bool hasMeasurements)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é uma assistente especialista em engenharia de áudio automotivo, integrada a um app de equalização para um Kia Cerato EX3 2011 com central multimídia Wintech.");
        sb.AppendLine("Responda em português. Distinga hipótese de problema comprovado. Se faltarem medições, diga isso. Se a mudança não for possível com os controles disponíveis, diga que a limitação parece ser do hardware/controle.");
        sb.AppendLine("Nunca afirme correção acústica global; o equalizador original pode estar processando o áudio.");
        sb.AppendLine($"Medições acústicas disponíveis: {(hasMeasurements ? "sim" : "NÃO")}.");
        sb.AppendLine($"Motor: {caps.Engine}; controle: {caps.Level}; afeta áudio real: {caps.AffectsRealAudio}.");
        sb.AppendLine($"Faixa de ganho permitida: {caps.MinGainDb:0.#} a {caps.MaxGainDb:0.#} dB. Evite reforços acima de +{SettingsValidator.SafeBoostLimitDb} dB.");
        sb.AppendLine("Bandas (Hz => ganho atual dB): " + string.Join(", ", caps.Bands.Select((b, i) => $"{b.CenterHz:0.#}=>{(i < current.BandGainsDb.Count ? current.BandGainsDb[i] : 0):0.#}")));
        sb.AppendLine($"Pré-amp suportado: {caps.SupportsPreamp}. Balance/fader suportado: {caps.SupportsBalanceFader}. Paramétrico: {caps.SupportsParametric}.");
        sb.AppendLine("Ao propor alterações, termine com UM bloco ```json com exatamente este formato e nenhuma outra operação:");
        sb.AppendLine("{\"explanation\":\"...\",\"limitations\":\"...ou null\",\"operations\":[{\"op\":\"set_band\",\"frequencyHz\":125,\"gainDb\":1.5},{\"op\":\"set_preamp\",\"gainDb\":-1},{\"op\":\"revert\"}]}");
        sb.AppendLine("Use apenas set_band, set_preamp e revert. Para apenas conversar, não inclua o bloco.");
        return sb.ToString();
    }
}

public static class ProposalParser
{
    private static readonly Regex Block = new("```(?:json)?\\s*(\\{.*?\\})\\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static AssistantProposal Parse(string reply, AudioCapabilities caps, EqSettings current)
    {
        var p = new AssistantProposal { Explanation = Block.Replace(reply, "").Trim() };
        var m = Block.Match(reply);
        if (!m.Success) return p; // plain conversation, no operations

        JsonDocument doc;
        try { doc = JsonDocument.Parse(m.Groups[1].Value); }
        catch (JsonException) { p.Issues.Add("Proposta em JSON inválido; ignorada."); return p; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("explanation", out var ex) && ex.ValueKind == JsonValueKind.String && p.Explanation.Length == 0) p.Explanation = ex.GetString()!;
            if (root.TryGetProperty("limitations", out var lim) && lim.ValueKind == JsonValueKind.String) p.Limitations = lim.GetString();
            if (!root.TryGetProperty("operations", out var ops) || ops.ValueKind != JsonValueKind.Array) { p.Issues.Add("Sem lista de operações."); return p; }
            if (ops.GetArrayLength() > 64) { p.Issues.Add("Operações demais."); return p; }

            var work = current.Clone();
            if (work.BandGainsDb.Count != caps.BandCount) work = EqSettings.Flat(caps.BandCount);
            bool changed = false;
            foreach (var op in ops.EnumerateArray())
            {
                var name = op.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
                switch (name)
                {
                    case "revert": p.RevertRequested = true; break;
                    case "set_band":
                        if (!Num(op, "frequencyHz", out var f) || !Num(op, "gainDb", out var g)) { p.Issues.Add("set_band inválido."); break; }
                        if (caps.BandCount == 0) { p.Issues.Add("Nenhuma banda disponível."); break; }
                        var band = caps.Bands.OrderBy(b => Math.Abs(Math.Log2(b.CenterHz / Math.Max(f, 1)))).First();
                        if (Math.Abs(Math.Log2(band.CenterHz / Math.Max(f, 1))) > 0.75)
                        { p.Issues.Add($"{f:0.#} Hz está longe de qualquer banda real (mais próxima: {band.CenterHz:0.#} Hz)."); break; }
                        if (g < caps.MinGainDb || g > caps.MaxGainDb) { p.Issues.Add($"{g:0.#} dB fora dos limites {caps.MinGainDb:0.#}..{caps.MaxGainDb:0.#} dB."); break; }
                        work.BandGainsDb[band.Index] = g; changed = true; break;
                    case "set_preamp":
                        if (!caps.SupportsPreamp) { p.Issues.Add("Pré-amp não suportado."); break; }
                        if (!Num(op, "gainDb", out var pg) || pg > caps.MaxPreampDb || pg < -24) { p.Issues.Add("Pré-amp inválido."); break; }
                        work.PreampDb = pg; changed = true; break;
                    default: p.Issues.Add($"Operação não permitida: '{name}'."); break;
                }
            }
            if (changed)
            {
                foreach (var i in SettingsValidator.Validate(work, caps)) p.Issues.Add($"{i.Field}: {i.Message}");
                p.Proposed = work;
                for (int i = 0; i < caps.BandCount; i++)
                    p.Comparison.Add(new(FrequencyMath.Format(caps.Bands[i].CenterHz), i < current.BandGainsDb.Count ? current.BandGainsDb[i] : 0, work.BandGainsDb[i]));
            }
        }
        return p;
    }

    private static bool Num(JsonElement e, string n, out double v)
    {
        v = 0;
        return e.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out v) && double.IsFinite(v);
    }
}

public sealed class AssistantService
{
    private readonly ILlmProvider _llm;
    private readonly List<ChatMessage> _history = new();
    public AssistantService(ILlmProvider llm) => _llm = llm;
    public bool IsConfigured => _llm.IsConfigured;

    public async Task<AssistantProposal> AskAsync(string model, string userText, AudioCapabilities caps, EqSettings current, bool hasMeasurements, CancellationToken ct = default)
    {
        if (!_llm.IsConfigured) return new AssistantProposal { Explanation = "Assistente offline: configure o backend/credenciais nas Configurações. O app segue funcionando sem internet." };
        var messages = new List<ChatMessage> { new("system", AssistantPrompt.Build(caps, current, hasMeasurements)) };
        messages.AddRange(_history.TakeLast(10));
        messages.Add(new("user", userText));
        try
        {
            var reply = await _llm.ChatAsync(model, messages, ct);
            _history.Add(new("user", userText));
            _history.Add(new("assistant", reply));
            return ProposalParser.Parse(reply, caps, current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return new AssistantProposal { Explanation = "Não foi possível falar com o serviço de IA (sem internet ou tempo esgotado). Nenhum ajuste foi alterado." };
        }
    }
}
