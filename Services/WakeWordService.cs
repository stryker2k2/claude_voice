using System.Globalization;
using System.Speech.Recognition;

namespace claude_voice;

/// <summary>
/// Listens continuously for a configurable wake word/phrase using Windows SAPI5.
/// Much lighter than Whisper — suited for always-on keyword spotting.
/// Raises <see cref="WakeWordDetected"/> when the phrase is recognised with
/// sufficient confidence.
/// </summary>
public sealed class WakeWordService : IDisposable
{
    private readonly SpeechRecognitionEngine _engine;

    public event EventHandler? WakeWordDetected;

    public WakeWordService(string wakeWord)
    {
        _engine = new SpeechRecognitionEngine(new CultureInfo("en-US"));

        var builder = new GrammarBuilder(wakeWord.Trim()) { Culture = new CultureInfo("en-US") };
        _engine.LoadGrammar(new Grammar(builder));
        _engine.SpeechRecognized += OnSpeechRecognized;
        _engine.SetInputToDefaultAudioDevice();
    }

    public void StartListening() => _engine.RecognizeAsync(RecognizeMode.Multiple);

    public void StopListening() => _engine.RecognizeAsyncStop();

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (e.Result.Confidence >= 0.5f)
            WakeWordDetected?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        try { _engine.RecognizeAsyncCancel(); } catch { /* best-effort */ }
        _engine.Dispose();
    }
}
