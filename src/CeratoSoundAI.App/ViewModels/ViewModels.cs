using System.Collections.ObjectModel;
using System.Text.Json;
using CeratoSoundAI.App.Platforms.AndroidImpl;
using CeratoSoundAI.App.Services;
using CeratoSoundAI.Application;
using CeratoSoundAI.Application.Assistant;
using CeratoSoundAI.Application.Calibration;
using CeratoSoundAI.Application.Diagnostics;
using CeratoSoundAI.Application.Profiles;
using CeratoSoundAI.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CeratoSoundAI.App.ViewModels;

public sealed record ChatItem(string Who, string Text, bool IsUser);
public sealed record CapabilityRow(string Name, string Status, string Color);

// ───────────────────────── Assistant ─────────────────────────
public sealed partial class AssistantViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly AssistantService _assistant;
    private readonly IVoiceAssistant _voice;
    private readonly ILlmProvider _llm;
    private readonly AppOptions _opts;

    public AssistantViewModel(AppState state, AssistantService assistant, IVoiceAssistant voice, ILlmProvider llm, AppOptions opts)
    { _state = state; _assistant = assistant; _voice = voice; _llm = llm; _opts = opts; }

    public AppState State => _state;
    public ObservableCollection<ChatItem> Messages { get; } = new();
    public ObservableCollection<ComparisonRow> Comparison { get; } = new();
    public ObservableCollection<string> Models { get; } = new();

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private string _issuesText = "";
    [ObservableProperty] private bool _hasPending;
    [ObservableProperty] private string _lastReply = "Sem conversa ainda.";
    private AssistantProposal? _pending;

    partial void OnSelectedModelChanged(string? value) { if (value != null) _opts.Model = value; }

    [RelayCommand]
    public async Task LoadModelsAsync()
    {
        if (!_llm.IsConfigured) { Messages.Add(new("Sistema", "IA não configurada. Defina URL/credenciais em Configurações.", false)); return; }
        Busy = true;
        try
        {
            var list = await _llm.ListModelsAsync();
            Models.Clear();
            foreach (var m in list) Models.Add(m.Name);
            SelectedModel = Models.Contains(_opts.Model ?? "") ? _opts.Model : Models.FirstOrDefault();
            if (Models.Count == 0) Messages.Add(new("Sistema", "A conta não retornou modelos autorizados.", false));
        }
        catch (Exception ex) { Messages.Add(new("Sistema", "Falha ao listar modelos: " + ex.Message, false)); }
        finally { Busy = false; }
    }

    [RelayCommand]
    public async Task SendAsync(string? text = null)
    {
        var msg = (text ?? Input)?.Trim();
        if (string.IsNullOrEmpty(msg) || Busy) return;
        Input = "";
        Messages.Add(new("Você", msg, true));
        ClearPending();
        if (string.IsNullOrEmpty(SelectedModel)) SelectedModel = _opts.Model;
        if (string.IsNullOrEmpty(SelectedModel) && _llm.IsConfigured) await LoadModelsAsync();
        if (string.IsNullOrEmpty(SelectedModel) && _llm.IsConfigured) return;

        Busy = true;
        try
        {
            var p = await _assistant.AskAsync(SelectedModel ?? "", msg, _state.Caps, _state.Current, _state.HasMeasurements);
            var reply = p.Explanation;
            if (!string.IsNullOrWhiteSpace(p.Limitations)) reply += "\n\nLimitação: " + p.Limitations;
            if (p.Issues.Count > 0) reply += "\n\n⚠ Proposta bloqueada pela validação.";
            Messages.Add(new("Assistente", string.IsNullOrWhiteSpace(reply) ? "(sem texto)" : reply, false));
            LastReply = reply;
            if (p.IsApplicable)
            {
                _pending = p;
                Comparison.Clear();
                foreach (var c in p.Comparison) Comparison.Add(c);
                HasPending = true;
            }
            IssuesText = string.Join("\n", p.Issues);
            if (_opts.SpeakReplies) await _voice.SpeakAsync(p.Explanation);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    public async Task ListenAsync()
    {
        if (!_voice.IsRecognitionAvailable) { Messages.Add(new("Sistema", "Reconhecimento de voz indisponível neste aparelho.", false)); return; }
        Busy = true;
        string? heard;
        try { heard = await _voice.ListenOnceAsync(); } finally { Busy = false; }
        if (string.IsNullOrWhiteSpace(heard)) { Messages.Add(new("Sistema", "Nada foi reconhecido (sem serviço de voz/internet ou silêncio).", false)); return; }
        await SendAsync(heard);
        if (HasPending && _voice.IsSpeechAvailable)
        {
            await _voice.SpeakAsync("Posso aplicar essa alteração? Diga sim ou cancelar.");
            await Task.Delay(2500);
            var answer = (await _voice.ListenOnceAsync())?.ToLowerInvariant() ?? "";
            if (answer.Contains("sim") || answer.Contains("confirm") || answer.Contains("aplic")) await ConfirmAsync();
            else CancelPending();
        }
    }

    [RelayCommand]
    public async Task ConfirmAsync()
    {
        var p = _pending;
        if (p == null) return;
        if (p.RevertRequested && p.Proposed == null) { await RevertAsync(); ClearPending(); return; }
        if (p.Proposed == null) return;
        var caps = _state.Caps;
        // The original profile is never overwritten: the suggestion becomes its own derived profile.
        var baseName = _state.ActiveProfile?.Name ?? "Sugestão";
        var derived = new AudioProfile
        {
            Name = $"{baseName} (IA {DateTime.Now:HH:mm})",
            Description = "Derivado de proposta da IA, confirmado pelo usuário. Não calibrado.",
            BandCentersHz = caps.Bands.Select(b => b.CenterHz).ToList(),
            Settings = p.Proposed.Clone()
        };
        var r = await _state.ApplyProfileAsync(derived);
        Messages.Add(new("Sistema", r.Success ? $"Aplicado como perfil '{derived.Name}'. {r.Message}" : "Não aplicado: " + r.Message, false));
        ClearPending();
    }

    [RelayCommand] public void CancelPending() { ClearPending(); Messages.Add(new("Sistema", "Proposta cancelada. Nada foi alterado.", false)); }

    [RelayCommand]
    public async Task RevertAsync()
    {
        var r = await _state.RevertAsync();
        Messages.Add(new("Sistema", r.Success ? "Voltou à configuração anterior." : "Não foi possível voltar: " + r.Message, false));
    }

    private void ClearPending() { _pending = null; HasPending = false; Comparison.Clear(); IssuesText = ""; }
}

// ───────────────────────── Main ─────────────────────────
public sealed partial class MainViewModel : ObservableObject
{
    public AppState State { get; }
    public AssistantViewModel Assistant { get; }
    public ObservableCollection<AudioProfile> Profiles { get; } = new();
    [ObservableProperty] private string _miniInput = "";
    [ObservableProperty] private string _status = "";

    public MainViewModel(AppState state, AssistantViewModel assistant) { State = state; Assistant = assistant; }

    public async Task LoadAsync()
    {
        var all = await State.Repo.GetAllAsync();
        Profiles.Clear();
        foreach (var p in all.OrderByDescending(p => p.IsFavorite)) Profiles.Add(p);
    }

    [RelayCommand] private Task GoEq() => AppShell.GoAsync("eq");
    [RelayCommand] private Task GoCalibration() => AppShell.GoAsync("calibration");
    [RelayCommand] private Task GoAssistant() => AppShell.GoAsync("assistant");
    [RelayCommand] private Task GoDiagnostics() => AppShell.GoAsync("diagnostics");

    [RelayCommand]
    private async Task ApplyQuick(AudioProfile p)
    {
        var r = await State.ApplyProfileAsync(p);
        Status = r.Success ? r.Message : "Falha: " + r.Message;
    }

    [RelayCommand]
    private async Task SafeAsync()
    {
        var r = await State.RevertAsync();
        Status = r.Success ? "Retornou ao último ajuste seguro/anterior." : "Sem ajuste seguro: " + r.Message;
    }

    [RelayCommand]
    private async Task MiniSend()
    {
        var t = MiniInput; MiniInput = "";
        await Assistant.SendAsync(t);
        Status = Assistant.HasPending ? "Há uma proposta aguardando confirmação no Assistente." : "";
    }
}

// ───────────────────────── Equalizer ─────────────────────────
public sealed partial class EqualizerViewModel : ObservableObject
{
    public AppState State { get; }
    private readonly IVehicleState _vehicle;
    private readonly AppOptions _opts;

    public EqualizerViewModel(AppState state, IVehicleState vehicle, AppOptions opts) { State = state; _vehicle = vehicle; _opts = opts; }

    public ObservableCollection<CapabilityRow> Capabilities { get; } = new();
    [ObservableProperty] private EqSettings _draft = new();
    [ObservableProperty] private int _selected = 0;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _selectedText = "";
    private EqSettings? _a, _b;

    public double UpperLimit => _opts.AllowHighBoost ? State.Caps.MaxGainDb : Math.Min(State.Caps.MaxGainDb, SettingsValidator.SafeBoostLimitDb);
    public bool MovingWarning => _vehicle.IsMoving == true;

    public void Load()
    {
        Draft = State.Current.Clone();
        if (Draft.BandGainsDb.Count != State.Caps.BandCount) Draft = EqSettings.Flat(State.Caps.BandCount);
        Selected = Math.Clamp(Selected, 0, Math.Max(0, State.Caps.BandCount - 1));
        var c = State.Caps;
        string Ok = "#5FD38D", Lim = "#E8B04A", No = "#E06C75";
        Capabilities.Clear();
        Capabilities.Add(new("Equalizador gráfico", c.BandCount > 0 ? $"{c.BandCount} bandas reais" : "indisponível", c.BandCount > 0 ? (c.AffectsRealAudio ? Lim : Lim) : No));
        Capabilities.Add(new("Faixa de ganho", c.BandCount > 0 ? $"{c.MinGainDb:0.#} a {c.MaxGainDb:0.#} dB" : "—", c.BandCount > 0 ? Ok : No));
        Capabilities.Add(new("Pré-amplificação", c.SupportsPreamp ? "disponível" : "indisponível", c.SupportsPreamp ? Ok : No));
        Capabilities.Add(new("Balance / Fader", c.SupportsBalanceFader ? "disponível" : "indisponível", c.SupportsBalanceFader ? Ok : No));
        Capabilities.Add(new("Paramétrico (f/Q)", c.SupportsParametric ? "disponível" : "indisponível", c.SupportsParametric ? Ok : No));
        Capabilities.Add(new("Crossover / Time alignment", c.SupportsCrossover || c.SupportsTimeAlignment ? "disponível" : "indisponível", c.SupportsCrossover ? Ok : No));
        Capabilities.Add(new("Leitura de retorno", c.SupportsReadback ? "sim" : "não", c.SupportsReadback ? Ok : Lim));
        Capabilities.Add(new("Compensação de volume A/B", "indisponível", No));
        UpdateSelectedText();
    }

    private void UpdateSelectedText()
    {
        var c = State.Caps;
        SelectedText = c.BandCount == 0 ? "—" : $"{FrequencyMath.Format(c.Bands[Math.Clamp(Selected, 0, c.BandCount - 1)].CenterHz)} Hz: {Draft.BandGainsDb[Math.Clamp(Selected, 0, c.BandCount - 1)]:+0.0;-0.0;0.0} dB";
    }

    public void SetBand(int band, double db)
    {
        if (band < 0 || band >= Draft.BandGainsDb.Count) return;
        var d = Draft.Clone();
        d.BandGainsDb[band] = Math.Clamp(db, State.Caps.MinGainDb, UpperLimit);
        Draft = d; Selected = band; UpdateSelectedText();
    }

    [RelayCommand] private void Up() => SetBand(Selected, Draft.BandGainsDb[Selected] + 0.5);
    [RelayCommand] private void Down() => SetBand(Selected, Draft.BandGainsDb[Selected] - 0.5);
    [RelayCommand] private void Prev() { Selected = Math.Max(0, Selected - 1); UpdateSelectedText(); Draft = Draft.Clone(); }
    [RelayCommand] private void Next() { Selected = Math.Min(State.Caps.BandCount - 1, Selected + 1); UpdateSelectedText(); Draft = Draft.Clone(); }
    [RelayCommand] private void Flat() { Draft = EqSettings.Flat(State.Caps.BandCount); UpdateSelectedText(); }

    // Bass/mid/treble are macros over the REAL bands (no separate hardware control is implied).
    private void Shelf(double lo, double hi, double delta)
    {
        var d = Draft.Clone();
        for (int i = 0; i < State.Caps.BandCount; i++)
        {
            var f = State.Caps.Bands[i].CenterHz;
            if (f >= lo && f < hi) d.BandGainsDb[i] = Math.Clamp(d.BandGainsDb[i] + delta, State.Caps.MinGainDb, UpperLimit);
        }
        Draft = d; UpdateSelectedText();
    }
    [RelayCommand] private void BassUp() => Shelf(0, 250, 0.5);
    [RelayCommand] private void BassDown() => Shelf(0, 250, -0.5);
    [RelayCommand] private void MidUp() => Shelf(250, 4000, 0.5);
    [RelayCommand] private void MidDown() => Shelf(250, 4000, -0.5);
    [RelayCommand] private void TrebleUp() => Shelf(4000, 1e9, 0.5);
    [RelayCommand] private void TrebleDown() => Shelf(4000, 1e9, -0.5);

    [RelayCommand] private void StoreA() { _a = Draft.Clone(); Message = "Slot A guardado."; }
    [RelayCommand] private void StoreB() { _b = Draft.Clone(); Message = "Slot B guardado."; }
    [RelayCommand] private Task ShowA() => Preview(_a, "A");
    [RelayCommand] private Task ShowB() => Preview(_b, "B");

    private async Task Preview(EqSettings? s, string name)
    {
        if (s == null) { Message = $"Slot {name} vazio."; return; }
        Draft = s.Clone();
        var r = await State.Engine.ApplyAsync(Draft);
        Message = r.Success ? $"{name}: {r.Message} (sem compensação de volume)" : "Falha: " + r.Message;
    }

    [RelayCommand]
    private async Task ApplySave()
    {
        if (MovingWarning) { Message = "Veículo em movimento: ajuste bloqueado."; return; }
        var profiles = await State.Repo.GetAllAsync();
        var p = State.ActiveProfile ?? profiles.FirstOrDefault(x => x.Name == "Personalizado") ?? new AudioProfile { Name = "Personalizado" };
        p.BandCentersHz = State.Caps.Bands.Select(b => b.CenterHz).ToList();
        p.Settings = Draft.Clone();
        var r = await State.ApplyProfileAsync(p);
        Message = r.Success ? $"Salvo em '{p.Name}'. {r.Message}" : "Não aplicado: " + r.Message;
        Load();
    }

    [RelayCommand]
    private async Task MarkStable()
    {
        if (State.ActiveProfile == null) { Message = "Aplique e salve um perfil antes."; return; }
        await State.Profiles.MarkStableAsync(State.ActiveProfile);
        Message = "Marcado como último ajuste seguro.";
    }

    [RelayCommand]
    private async Task Rollback()
    {
        var r = await State.Engine.RollbackAsync();
        await State.RefreshAsync(); Load();
        Message = r.Success ? "Rollback executado no motor (perfil salvo não foi alterado)." : r.Message;
    }
}

// ───────────────────────── Profiles ─────────────────────────
public sealed partial class ProfilesViewModel : ObservableObject
{
    public AppState State { get; }
    public ObservableCollection<AudioProfile> Items { get; } = new();
    [ObservableProperty] private AudioProfile? _selected;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _importJson = "";
    [ObservableProperty] private string _newName = "";

    public ProfilesViewModel(AppState state) => State = state;

    public async Task LoadAsync()
    {
        Items.Clear();
        foreach (var p in await State.Repo.GetAllAsync()) Items.Add(p);
    }

    [RelayCommand]
    private async Task Apply(AudioProfile? p)
    {
        p ??= Selected; if (p == null) return;
        var r = await State.ApplyProfileAsync(p);
        Message = r.Success ? $"'{p.Name}': {r.Message}" : "Falha: " + r.Message;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task Favorite(AudioProfile? p)
    {
        p ??= Selected; if (p == null) return;
        p.IsFavorite = !p.IsFavorite;
        await State.Profiles.SaveEditsAsync(p, p.IsFavorite ? "favoritado" : "desfavoritado");
        await LoadAsync();
    }

    [RelayCommand]
    private async Task Duplicate(AudioProfile? p)
    {
        p ??= Selected; if (p == null) return;
        var copy = new AudioProfile
        {
            Name = string.IsNullOrWhiteSpace(NewName) ? p.Name + " (cópia)" : NewName.Trim(),
            Description = p.Description, BandCentersHz = p.BandCentersHz.ToList(), Settings = p.Settings.Clone()
        };
        await State.Profiles.SaveEditsAsync(copy, "duplicado de " + p.Name);
        NewName = ""; await LoadAsync();
    }

    [RelayCommand]
    private async Task Delete(AudioProfile? p)
    {
        p ??= Selected; if (p == null) return;
        if (p.IsBuiltIn) { Message = "Perfis iniciais não são removidos (edite ou duplique)."; return; }
        await State.Repo.DeleteAsync(p.Id); await LoadAsync();
    }

    [RelayCommand]
    private async Task Export(AudioProfile? p)
    {
        p ??= Selected; if (p == null) return;
        await Share.Default.RequestAsync(new ShareTextRequest { Text = ProfileService.Export(p), Title = "Perfil " + p.Name });
    }

    [RelayCommand]
    private async Task Import()
    {
        var (p, err) = ProfileService.Import(ImportJson);
        if (p == null) { Message = "Importação recusada: " + err; return; }
        await State.Profiles.SaveEditsAsync(p, "importado");
        ImportJson = ""; Message = $"Importado '{p.Name}'."; await LoadAsync();
    }
}

// ───────────────────────── Calibration ─────────────────────────
public sealed partial class CalibrationViewModel : ObservableObject
{
    private readonly ICalibrationPlatform _platform;
    private readonly IVehicleState _vehicle;
    public CalibrationViewModel(ICalibrationPlatform platform, IVehicleState vehicle) { _platform = platform; _vehicle = vehicle; }

    public ObservableCollection<string> Capture { get; } = new();
    [ObservableProperty] private bool _parkedConfirmed;
    [ObservableProperty] private string _gate = "";
    [ObservableProperty] private string _micPolicy = MicrophonePolicy.Describe(new("interno", false, false));
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _playing;

    public string Limits => $"Nível máximo do sinal: {CalibrationSafety.MaxTestLevelDbfs} dBFS · duração ≤ {CalibrationSafety.MaxDurationSeconds:0}s. " +
        "A medição de resposta em frequência AINDA NÃO está implementada: depende de microfone externo calibrado e testes no veículo.";

    public void Load()
    {
        Capture.Clear();
        foreach (var c in _platform.ProbeCapture()) Capture.Add($"Captura mono 16-bit @ {c.SampleRate} Hz: {(c.Supported ? "suportada" : "não suportada")} (buffer mín. {c.MinBuffer})");
        foreach (var d in _platform.InputDevices()) Capture.Add("Entrada: " + d);
        UpdateGate();
    }

    partial void OnParkedConfirmedChanged(bool value) => UpdateGate();

    private void UpdateGate()
    {
        var g = CalibrationSafety.CanRun(_vehicle);
        // With no motion source, the user's explicit "parked" confirmation is the only gate.
        Gate = _vehicle.IsMoving == true ? g.Reason : ParkedConfirmed ? "Liberado pela confirmação do usuário (sem sensor de movimento)." : g.Reason;
    }

    [RelayCommand]
    private async Task PlaySweep()
    {
        if (_vehicle.IsMoving == true || !ParkedConfirmed) { Status = "Bloqueado: confirme que o veículo está estacionado."; return; }
        var ok = await Shell.Current.DisplayAlert("Sinal de teste", "Reduza o volume ao mínimo antes. O sinal é limitado, mas pode soar alto. Continuar?", "Reproduzir", "Cancelar");
        if (!ok) return;
        Playing = true; Status = "Reproduzindo varredura (6 s)…";
        try { await _platform.PlayTestSweepAsync(CalibrationSafety.MaxTestLevelDbfs, CancellationToken.None); Status = "Concluído. Nenhuma medição foi gravada."; }
        catch (Exception ex) { Status = "Falha ao reproduzir: " + ex.Message; }
        finally { Playing = false; }
    }
}

// ───────────────────────── Diagnostics ─────────────────────────
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IHardwareDiagnostics _diag;
    private readonly AppState _state;
    public DiagnosticsViewModel(IHardwareDiagnostics diag, AppState state) { _diag = diag; _state = state; }

    [ObservableProperty] private string _summary = "Toque em Executar diagnóstico.";
    [ObservableProperty] private string _json = "";
    [ObservableProperty] private bool _sanitize = true;
    [ObservableProperty] private bool _busy;
    private HardwareReport? _report;

    public string Adb => string.Join("\n", _diag.ReadOnlyAdbCommands);
    public string Checklist => string.Join("\n", new[]
    {
        "• Balance/Fader: centralize na central original e no app (hipótese: ajuste residual).",
        "• Polaridade: tom/faixa mono; graves some ao aproximar alto-falantes? (hipótese de fase invertida).",
        "• Cancelamento de graves: compare mono vs estéreo; queda de graves em mono sugere polaridade.",
        "• Fontes: compare rádio, USB e Bluetooth no MESMO volume (hipótese de fonte/codec).",
        "• Clipping: aumente o volume gradualmente; distorção cedo sugere saturação de entrada/DSP.",
        "• Processamento duplo: desative EQ do app original e compare com a mesma música.",
        "• Volume equivalente: use medidor SPL externo (celular não é calibrado).",
        "• Ruído: compare motor ligado/desligado, sem sinal, volume 0 e 50%.",
        "• Chicotes/aterramento/amplificação: inspeção física do chicote, massa, remap de canais (hipótese até medição)."
    });

    [RelayCommand]
    private async Task Run()
    {
        Busy = true;
        try
        {
            _report = await _diag.CollectAsync();
            await _state.RefreshAsync();
            Render();
        }
        catch (Exception ex) { Summary = "Falha no diagnóstico: " + ex.Message; }
        finally { Busy = false; }
    }

    partial void OnSanitizeChanged(bool value) => Render();

    private void Render()
    {
        if (_report == null) return;
        var r = Sanitize ? ReportSanitizer.Sanitize(_report) : _report;
        Json = ReportSanitizer.ToJson(r);
        Summary = $"{r.Device.GetValueOrDefault("manufacturer")} {r.Device.GetValueOrDefault("model")} · Android {r.Device.GetValueOrDefault("release")} (API {r.Device.GetValueOrDefault("sdk_int")})\n" +
                  $"SoC: {r.Device.GetValueOrDefault("cpuinfo_hardware")} / {r.Device.GetValueOrDefault("soc_platform_prop")} · ABI {r.Device.GetValueOrDefault("abis")}\n" +
                  $"RAM: {r.Memory.GetValueOrDefault("avail_mb")}/{r.Memory.GetValueOrDefault("total_mb")} MB · Efeitos: {r.Effects.Count} · Pacotes de áudio: {r.AudioPackages.Count}\n\n" +
                  string.Join("\n", r.Probes.Select(p => $"[{(p.WriteOk ? "OK" : p.Attempted ? "FALHA" : "NÃO TESTADO")}] {p.Name}: {p.Detail}")) + "\n\n" +
                  string.Join("\n", r.Findings.Select(f => "• " + f));
    }

    [RelayCommand]
    private async Task ShareJson()
    {
        if (string.IsNullOrEmpty(Json)) return;
        var path = Path.Combine(FileSystem.CacheDirectory, $"cerato-diagnostico-{DateTime.Now:yyyyMMdd-HHmm}.json");
        await File.WriteAllTextAsync(path, Json);
        await Share.Default.RequestAsync(new ShareFileRequest("Diagnóstico Cerato Sound AI", new ShareFile(path)));
    }
}

// ───────────────────────── History ─────────────────────────
public sealed partial class HistoryViewModel : ObservableObject
{
    public AppState State { get; }
    public ObservableCollection<ProfileHistoryEntry> Entries { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();
    [ObservableProperty] private string _message = "";
    public HistoryViewModel(AppState state) => State = state;

    public async Task LoadAsync()
    {
        Entries.Clear(); Logs.Clear();
        foreach (var e in await State.Repo.GetAllHistoryAsync(60)) Entries.Add(e);
        foreach (var l in await State.Repo.GetLogsAsync(40)) Logs.Add(l);
    }

    [RelayCommand]
    private async Task Restore(ProfileHistoryEntry? e)
    {
        if (e == null) return;
        var r = await State.ApplyProfileAsync(e.Snapshot);
        Message = r.Success ? $"Restaurado '{e.Snapshot.Name}'. {r.Message}" : "Falha: " + r.Message;
        await LoadAsync();
    }
}

// ───────────────────────── Settings ─────────────────────────
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppOptions _opts;
    private readonly AppState _state;
    public SettingsViewModel(AppOptions opts, AppState state)
    {
        _opts = opts; _state = state;
        _url = _opts.Ollama().BaseUrl; _bands = _opts.ManualBands ?? ""; _allowHigh = _opts.AllowHighBoost; _speak = _opts.SpeakReplies;
        _modeIndex = (int)state.Engine.Mode;
    }

    public string[] Modes { get; } =
    {
        "Processamento Android compatível", "Controle nativo do DSP (não comprovado)", "Ajuste manual (equalizador original)", "Diagnóstico (sem aplicar)", "Simulação (desenvolvimento – não altera o som)"
    };

    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private string _url;
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _bands;
    [ObservableProperty] private bool _allowHigh;
    [ObservableProperty] private bool _speak;
    [ObservableProperty] private string _message = "";

    [RelayCommand]
    private async Task Save()
    {
        _state.Engine.SetMode((EngineMode)Math.Clamp(ModeIndex, 0, 4));
        _opts.ManualBands = string.IsNullOrWhiteSpace(Bands) ? null : Bands.Trim();
        _opts.AllowHighBoost = AllowHigh; _opts.SpeakReplies = Speak;
        if (Uri.TryCreate(Url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Host is "localhost" or "127.0.0.1")) new PreferencesSettingsStore().Set("llm_url", Url.Trim());
        else { Message = "URL inválida (use https)."; return; }
        if (!string.IsNullOrWhiteSpace(ApiKey) && !await AppOptions.SaveKeyAsync(ApiKey)) { Message = "Falha ao guardar a chave no armazenamento seguro."; return; }
        ApiKey = "";
        await _state.RefreshAsync();
        Message = "Salvo. Modo: " + Modes[ModeIndex];
    }

    [RelayCommand]
    private async Task ClearKey() { await AppOptions.SaveKeyAsync(null); Message = "Chave removida."; }
}
