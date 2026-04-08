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

    public bool IsRecording { get; private set; }

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

        _waveIn.DataAvailable += (_, e) => _waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
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

    public void Dispose()
    {
        _waveIn?.Dispose();
        _waveWriter?.Dispose();
        _factory.Dispose();
    }
}
