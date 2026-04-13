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
    private readonly string _wakeWord;

    public event EventHandler? WakeWordDetected;

    private readonly float _confidenceThreshold;

    public WakeWordService(string wakeWord, float confidenceThreshold = 0.75f)
    {
        _wakeWord             = wakeWord.Trim();
        _confidenceThreshold  = Math.Clamp(confidenceThreshold, 0f, 1f);
        _engine = new SpeechRecognitionEngine(new CultureInfo("en-US"));

        var builder = new GrammarBuilder(wakeWord.Trim()) { Culture = new CultureInfo("en-US") };
        _engine.LoadGrammar(new Grammar(builder));
        _engine.SpeechRecognized += OnSpeechRecognized;
        _engine.SetInputToDefaultAudioDevice();
    }

    private bool     _isListening;
    private DateTime _lastTrigger = DateTime.MinValue;

    // Minimum gap between two wake-word events. Prevents a single noisy audio
    // burst from firing multiple back-to-back triggers.
    private static readonly TimeSpan CooldownPeriod = TimeSpan.FromSeconds(3);

    public void StartListening()
    {
        if (_isListening) return;
        _isListening = true;
        _engine.RecognizeAsync(RecognizeMode.Multiple);
    }

    public void StopListening()
    {
        if (!_isListening) return;
        _isListening = false;
        _engine.RecognizeAsyncStop();
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        // Ignore events that arrive after StopListening was called (SAPI5 buffers audio)
        if (!_isListening) return;

        // Higher confidence threshold to reduce false positives from background audio
        // (TV, YouTube, etc.). SAPI5 reports 0.9+ for clearly spoken phrases up close;
        // coincidental audio matches rarely exceed 0.75.
        if (e.Result.Confidence < _confidenceThreshold) return;
        if (!string.Equals(e.Result.Text, _wakeWord, StringComparison.OrdinalIgnoreCase)) return;

        // Cooldown — one trigger per window to absorb SAPI5 buffer echoes
        var now = DateTime.UtcNow;
        if (now - _lastTrigger < CooldownPeriod) return;
        _lastTrigger = now;

        WakeWordDetected?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        try { _engine.RecognizeAsyncCancel(); } catch { /* best-effort */ }
        _engine.Dispose();
    }
}
