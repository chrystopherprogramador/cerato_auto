using Android.Content;
using Android.OS;
using Android.Speech;
using Android.Speech.Tts;
using CeratoSoundAI.Domain;

namespace CeratoSoundAI.App.Platforms.AndroidImpl;

public sealed class AndroidVoiceAssistant : Java.Lang.Object, IVoiceAssistant, Android.Speech.Tts.TextToSpeech.IOnInitListener
{
    private Android.Speech.Tts.TextToSpeech? _tts;
    private bool _ttsReady;
    private SpeechRecognizer? _recognizer;
    private TaskCompletionSource<string?>? _listen;

    public bool IsRecognitionAvailable => SpeechRecognizer.IsRecognitionAvailable(Android.App.Application.Context);
    public bool IsSpeechAvailable => _ttsReady;

    public AndroidVoiceAssistant()
    {
        try { _tts = new Android.Speech.Tts.TextToSpeech(Android.App.Application.Context, this); } catch { _tts = null; }
    }

    public void OnInit(OperationResult status)
    {
        _ttsReady = status == OperationResult.Success;
        if (_ttsReady) { try { _tts?.SetLanguage(new Java.Util.Locale("pt", "BR")); } catch { } }
    }

    /// <summary>One explicit listening session; the microphone is released as soon as it ends.</summary>
    public async Task<string?> ListenOnceAsync(CancellationToken ct = default)
    {
        if (!IsRecognitionAvailable) return null;
        var status = await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<Permissions.Microphone>());
        if (status != PermissionStatus.Granted) return null;

        _listen = new TaskCompletionSource<string?>();
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _recognizer?.Destroy();
            _recognizer = SpeechRecognizer.CreateSpeechRecognizer(Android.App.Application.Context);
            _recognizer.SetRecognitionListener(new Listener(_listen));
            var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
            intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
            intent.PutExtra(RecognizerIntent.ExtraLanguage, "pt-BR");
            intent.PutExtra(RecognizerIntent.ExtraMaxResults, 1);
            _recognizer.StartListening(intent);
        });
        using var reg = ct.Register(() => CancelAll());
        var winner = await Task.WhenAny(_listen.Task, Task.Delay(TimeSpan.FromSeconds(20), ct).ContinueWith(_ => (string?)null));
        var text = winner == _listen.Task ? await _listen.Task : null;
        await MainThread.InvokeOnMainThreadAsync(() => { _recognizer?.Destroy(); _recognizer = null; });
        return text;
    }

    public Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (!_ttsReady || _tts == null || string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        var t = text.Length > 600 ? text[..600] : text;
        if ((int)Build.VERSION.SdkInt >= 21) _tts.Speak(t, QueueMode.Flush, null, "cerato");
        return Task.CompletedTask;
    }

    public void CancelAll()
    {
        try { _tts?.Stop(); } catch { }
        MainThread.BeginInvokeOnMainThread(() => { try { _recognizer?.Cancel(); _recognizer?.Destroy(); } catch { } _recognizer = null; });
        _listen?.TrySetResult(null);
    }

    private sealed class Listener : Java.Lang.Object, IRecognitionListener
    {
        private readonly TaskCompletionSource<string?> _tcs;
        public Listener(TaskCompletionSource<string?> tcs) => _tcs = tcs;
        public void OnResults(Bundle? results) =>
            _tcs.TrySetResult(results?.GetStringArrayList(SpeechRecognizer.ResultsRecognition)?.FirstOrDefault());
        public void OnError(SpeechRecognizerError error) => _tcs.TrySetResult(null);
        public void OnBeginningOfSpeech() { }
        public void OnBufferReceived(byte[]? buffer) { }
        public void OnEndOfSpeech() { }
        public void OnEvent(int eventType, Bundle? @params) { }
        public void OnPartialResults(Bundle? partialResults) { }
        public void OnReadyForSpeech(Bundle? @params) { }
        public void OnRmsChanged(float rmsdB) { }
    }
}
