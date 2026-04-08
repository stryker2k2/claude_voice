using System.IO;
using System.Text;
using NAudio.Wave;
using Whisper.net;

namespace claude_voice;

public sealed class SttService : IDisposable
{
    private readonly WhisperFactory _factory;
    private WaveInEvent?   _waveIn;
    private WaveFileWriter? _waveWriter;
    private string          _tempFile = "";
    private long _lastRmsBits; // stores double bits via Interlocked for thread safety

    public bool   IsRecording { get; private set; }
    public double CurrentRms  =>
        _waveIn is not null
            ? BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastRmsBits))
            : 0.0;

    public SttService(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"Whisper model not found at: {modelPath}\nRun download-whisper.ps1 to download it.");

        _factory = WhisperFactory.FromPath(modelPath);
    }

    public void StartRecording()
    {
        if (IsRecording) return;

        _tempFile   = Path.Combine(Path.GetTempPath(), $"claude_voice_{Guid.NewGuid():N}.wav");
        _waveIn     = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        _waveWriter = new WaveFileWriter(_tempFile, _waveIn.WaveFormat);

        _waveIn.DataAvailable += (_, e) =>
        {
            _waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
            // Track RMS for silence detection (used by AutoRecordAndTranscribeAsync)
            if (e.BytesRecorded >= 2)
            {
                double sumSq = 0;
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                    sumSq += Math.Pow(BitConverter.ToInt16(e.Buffer, i), 2);
                var rms = Math.Sqrt(sumSq / (e.BytesRecorded / 2));
                Interlocked.Exchange(ref _lastRmsBits, BitConverter.DoubleToInt64Bits(rms));
            }
        };
        _waveIn.StartRecording();
        IsRecording = true;
    }

    public async Task<string> StopAndTranscribeAsync(CancellationToken ct = default)
    {
        if (!IsRecording) return "";
        IsRecording = false;

        // Wait for RecordingStopped so DataAvailable fully drains before we dispose the writer.
        // Timeout guards against NAudio never firing the event (e.g. recording never fully started).
        var tcs = new TaskCompletionSource();
        _waveIn!.RecordingStopped += (_, _) => tcs.TrySetResult();
        _waveIn.StopRecording();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

        _waveIn.Dispose();
        _waveIn = null;

        _waveWriter!.Dispose();
        _waveWriter = null;

        try
        {
            // Require at least ~500 ms of audio (16 kHz × 2 bytes × 0.5 s = 16 000 bytes + 44 header).
            // Shorter clips confuse Whisper and are almost certainly accidental taps.
            const long MinAudioBytes = 44 + 16_000;
            if (new FileInfo(_tempFile).Length < MinAudioBytes) return "";

            using var processor = _factory.CreateBuilder()
                .WithLanguage("en")
                .Build();

            await using var stream = File.OpenRead(_tempFile);
            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(stream, ct))
                sb.Append(segment.Text);

            return sb.ToString().Trim();
        }
        finally
        {
            try { File.Delete(_tempFile); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Starts recording, waits for speech followed by silence, then transcribes.
    /// Used by the wake-word flow so the user doesn't need to press a button to stop.
    /// <paramref name="noSpeechTimeout"/>: if no speech starts within this duration, stop early (returns "").
    /// Pass <see cref="TimeSpan.Zero"/> to disable the no-speech timeout.
    /// </summary>
    /// <summary>
    /// Returns the transcribed text and whether recording was cut off by
    /// <paramref name="maxDuration"/> (as opposed to natural silence or no-speech timeout).
    /// </summary>
    /// <summary>
    /// Immediately stops recording and discards all audio — no transcription is performed.
    /// Used when the user cancels a wake-word recording session.
    /// </summary>
    public void StopRecordingOnly()
    {
        if (!IsRecording) return;
        IsRecording = false;

        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;

        _waveWriter?.Dispose();
        _waveWriter = null;

        try { if (!string.IsNullOrEmpty(_tempFile)) File.Delete(_tempFile); } catch { /* best-effort */ }
        _tempFile = "";

        Interlocked.Exchange(ref _lastRmsBits, 0);
    }

    public async Task<(string Text, bool HitMaxDuration)> AutoRecordAndTranscribeAsync(
        TimeSpan silenceTimeout,
        TimeSpan maxDuration,
        double   silenceThreshold  = 150.0,
        TimeSpan noSpeechTimeout   = default,
        CancellationToken ct       = default)
    {
        StartRecording();

        double SpeechThreshold  = silenceThreshold;
        double SilenceThreshold = silenceThreshold;

        var started         = DateTime.UtcNow;
        var lastSpeech      = DateTime.UtcNow;
        bool hadSpeech      = false;
        bool hitMaxDuration = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);

                if (DateTime.UtcNow - started > maxDuration)
                {
                    hitMaxDuration = true;
                    break;
                }

                var currentRms = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastRmsBits));

                if (currentRms > SpeechThreshold)
                    hadSpeech = true;

                // Keep lastSpeech alive as long as we're above the silence floor —
                // not just on loud spikes. This prevents cutting off mid-sentence
                // when a low-gain mic stays between the two thresholds.
                if (hadSpeech && currentRms > SilenceThreshold)
                    lastSpeech = DateTime.UtcNow;

                // Give up if user hasn't started speaking within noSpeechTimeout
                if (!hadSpeech && noSpeechTimeout > TimeSpan.Zero &&
                    DateTime.UtcNow - started > noSpeechTimeout)
                    break;

                if (hadSpeech && currentRms < SilenceThreshold &&
                    DateTime.UtcNow - lastSpeech > silenceTimeout)
                {
                    // System.IO.File.AppendAllText("stt_debug.log",
                    //     $"{DateTime.Now:HH:mm:ss} [STT] Silence detected — RMS: {currentRms:F1}, silent for: {(DateTime.UtcNow - lastSpeech).TotalSeconds:F1}s{Environment.NewLine}");
                    break;
                }
            }

            // Normal exit — transcribe what was recorded
            return (await StopAndTranscribeAsync(CancellationToken.None), hitMaxDuration);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user (e.g. wake-word toggle off) — discard audio
            StopRecordingOnly();
            return ("", false);
        }
    }

    public void Dispose()
    {
        _waveIn?.Dispose();
        _waveWriter?.Dispose();
        _factory.Dispose();
    }
}
