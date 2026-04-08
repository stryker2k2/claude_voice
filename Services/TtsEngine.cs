using System.Diagnostics;
using System.IO;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace claude_voice;

/// <summary>
/// Wraps TTS synthesis. Uses Piper TTS (local neural model) when piperExe/piperModel
/// are configured and the files exist; falls back to WinRT SpeechSynthesizer otherwise.
/// </summary>
public sealed class TtsEngine : IDisposable
{
    // -------------------------------------------------------------------------
    // Piper backend fields
    private readonly bool   _usePiper;
    private readonly string _piperExe = "";
    private          string _piperModel = "";
    private readonly double _piperLengthScale;

    // -------------------------------------------------------------------------
    // WinRT backend fields
    private readonly SpeechSynthesizer _synth;

    // -------------------------------------------------------------------------
    // Active playback — held so Stop() can cancel mid-sentence
    private WaveOutEvent? _waveOut;
    private readonly object _waveOutLock = new();
    private bool _isFirstChunk = true;

    // -------------------------------------------------------------------------
    // Shared
    public string ActiveVoiceName =>
        _usePiper
            ? $"Piper: {Path.GetFileNameWithoutExtension(_piperModel)}"
            : _synth.Voice.DisplayName;

    public TtsEngine(AppConfig config)
    {
        _synth = new SpeechSynthesizer();

        var baseDir = AppContext.BaseDirectory;

        string ResolvePath(string? p) =>
            string.IsNullOrWhiteSpace(p) ? "" :
            Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(baseDir, p));

        var piperExe   = ResolvePath(config.PiperExe);
        var piperModel = ResolvePath(config.PiperModel);

        if (!string.IsNullOrEmpty(piperExe)  && File.Exists(piperExe) &&
            !string.IsNullOrEmpty(piperModel) && File.Exists(piperModel))
        {
            _usePiper         = true;
            _piperExe         = piperExe;
            _piperModel       = piperModel;
            _piperLengthScale = 1.0 / MapWinRtRate(config.TtsRate);
        }
        else
        {
            _synth.Options.SpeakingRate = MapWinRtRate(config.TtsRate);
            _synth.Options.AudioVolume  = Math.Clamp(config.TtsVolume / 100.0, 0.0, 1.0);
        }
    }

    /// <summary>Swaps the Piper voice model at runtime.</summary>
    public void ChangeVoice(string modelPath) => _piperModel = modelPath;

    /// <summary>Call before each new response so the first chunk gets the device warm-up silence.</summary>
    public void PrepareForNewResponse() => _isFirstChunk = true;

    /// <summary>Immediately stops any audio currently playing.</summary>
    public void Stop()
    {
        lock (_waveOutLock) { _waveOut?.Stop(); }
    }

    /// <summary>Returns all voices visible to the WinRT SpeechSynthesizer (informational).</summary>
    public IReadOnlyList<VoiceInformation> GetAvailableVoices() =>
        SpeechSynthesizer.AllVoices;

    /// <summary>
    /// Synthesizes <paramref name="text"/> to a temporary WAV file and returns its path.
    /// Returns null if the text is empty after normalization. Caller must delete the file.
    /// </summary>
    public async Task<string?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        if (_usePiper)
            return await SynthesizePiperAsync(text, ct);
        else
            return await SynthesizeWinRtAsync(text, ct);
    }

    /// <summary>Plays a pre-synthesized WAV file and deletes it when done.</summary>
    public async Task PlayPrerenderedAsync(string wavFile, CancellationToken ct = default)
    {
        try   { await PlayWavAsync(wavFile, ct); }
        finally { try { File.Delete(wavFile); } catch { /* best-effort */ } }
    }

    /// <summary>Synthesizes and plays in one call (used by WinRT path and as fallback).</summary>
    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        var wav = await SynthesizeAsync(text, ct);
        if (wav != null) await PlayPrerenderedAsync(wav, ct);
    }

    // -------------------------------------------------------------------------
    // Piper backend

    private async Task<string?> SynthesizePiperAsync(string text, CancellationToken ct)
    {
        text = NormalizeForSpeech(text);
        if (string.IsNullOrWhiteSpace(text)) return null;

        var tmpFile = Path.Combine(Path.GetTempPath(), $"claude_voice_tts_{Guid.NewGuid():N}.wav");
        var psi = new ProcessStartInfo
        {
            FileName               = _piperExe,
            Arguments              = $"--model \"{_piperModel}\" --output_file \"{tmpFile}\" --length-scale {_piperLengthScale:F4}",
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start piper.exe");

        await proc.StandardInput.WriteAsync(text);
        proc.StandardInput.Close();

        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        await stderrTask;

        if (proc.ExitCode != 0)
        {
            try { File.Delete(tmpFile); } catch { /* best-effort */ }
            throw new InvalidOperationException($"piper.exe exited with code {proc.ExitCode}");
        }

        return tmpFile;
    }

    // -------------------------------------------------------------------------
    // WinRT backend

    private async Task<string?> SynthesizeWinRtAsync(string text, CancellationToken ct)
    {
        var stream = await _synth.SynthesizeTextToStreamAsync(text);

        var inputStream = stream.GetInputStreamAt(0);
        var dataReader  = new DataReader(inputStream);
        await dataReader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        dataReader.ReadBytes(bytes);

        var tmpFile = Path.Combine(Path.GetTempPath(), $"claude_voice_tts_{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(tmpFile, bytes, ct);
        return tmpFile;
    }

    // -------------------------------------------------------------------------
    // Shared playback helper

    private async Task PlayWavAsync(string wavFile, CancellationToken ct)
    {
        // Prepend silence only on the first chunk of each response so the audio
        // device warms up without adding gaps between subsequent chunks.
        if (_isFirstChunk) { PrependSilence(wavFile, milliseconds: 150); _isFirstChunk = false; }

        using var reader  = new AudioFileReader(wavFile);
        using var waveOut = new WaveOutEvent();

        lock (_waveOutLock) { _waveOut = waveOut; }

        // Cancel immediately when token fires
        ct.Register(() => { lock (_waveOutLock) { _waveOut?.Stop(); } });

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waveOut.PlaybackStopped += (_, _) => tcs.TrySetResult();

        waveOut.Init(reader);
        waveOut.Play();

        // Wait for the PlaybackStopped event rather than polling state,
        // which can briefly flicker between buffer fills at punctuation pauses.
        await tcs.Task;

        lock (_waveOutLock) { if (_waveOut == waveOut) _waveOut = null; }
    }

    /// <summary>
    /// Prepends <paramref name="milliseconds"/> ms of zero-filled PCM silence
    /// to a WAV file by rewriting it in-place.
    /// </summary>
    private static void PrependSilence(string wavFile, int milliseconds)
    {
        var original = File.ReadAllBytes(wavFile);
        if (original.Length < 44) return; // invalid WAV

        // Parse sample rate, channels, bits-per-sample from the WAV header
        int sampleRate    = BitConverter.ToInt32(original, 24);
        short channels    = BitConverter.ToInt16(original, 22);
        short bitsPerSamp = BitConverter.ToInt16(original, 34);

        int silenceSamples = (int)(sampleRate * milliseconds / 1000.0);
        int silenceBytes   = silenceSamples * channels * (bitsPerSamp / 8);

        // Build new WAV: header (44 bytes) + silence + original audio data
        var audioData    = original[44..];
        var newAudioSize = audioData.Length + silenceBytes;
        var output       = new byte[44 + newAudioSize];

        System.Buffer.BlockCopy(original, 0, output, 0, 44); // copy header
        // silence bytes are already zero
        System.Buffer.BlockCopy(audioData, 0, output, 44 + silenceBytes, audioData.Length);

        // Update the chunk size fields in the header
        BitConverter.TryWriteBytes(output.AsSpan(4),  (uint)(output.Length - 8));
        BitConverter.TryWriteBytes(output.AsSpan(40), (uint)newAudioSize);

        File.WriteAllBytes(wavFile, output);
    }

    // -------------------------------------------------------------------------
    // Helpers

    private static double MapWinRtRate(double rate)
    {
        rate = Math.Clamp(rate, -10.0, 10.0);
        return rate >= 0
            ? 1.0 + rate * 0.5
            : 1.0 + rate * 0.05;
    }

    private static string NormalizeForSpeech(string text)
    {
        text = text.Normalize(System.Text.NormalizationForm.FormC);

        const System.Text.RegularExpressions.RegexOptions ML =
            System.Text.RegularExpressions.RegexOptions.Multiline;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*{3}(.+?)\*{3}", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"_{3}(.+?)_{3}",   "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*{2}(.+?)\*{2}", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"_{2}(.+?)_{2}",   "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*(.+?)\*",        "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"_(.+?)_",          "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^#{1,6}\s+",       "", ML);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[(.+?)\]\(.+?\)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^>\s*",            "", ML);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[-*]{3,}\s*$",    "", ML);
        // Bullet items: strip the marker and append a comma so Piper pauses between items.
        // The comma is dropped if the item already ends in terminal punctuation.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[ \t]*[-*]\s+(.+?)[ \t]*$", "$1,", ML);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"([.!?:;]),", "$1"); // fix double-punct
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`\r\n]+)`", "$1");
        text = text.Replace("\"", "");
        text = text.Replace("(", ", ").Replace(")", ", ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\.[/\\]", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(\w)\.(\w)", "$1 $2");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(\r?\n\s*)+", " ");        // all newlines → single space

        // Insert commas before conjunctions when a clause runs 70+ chars without
        // punctuation. This gives Piper natural prosodic pause points in long sentences
        // (e.g. quoting speeches), preventing speed-up or garbling.
        {
            var clauseBreak = new System.Text.RegularExpressions.Regex(
                @"(?<=[^.,;!?:]{70,}) \b(where|which|who|because|when|while|though|although|and|but|or)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string prev;
            do { prev = text; text = clauseBreak.Replace(text, m => ", " + m.Value.TrimStart()); }
            while (text != prev);
        }

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            var replacement = c switch
            {
                '→' or '➜' or '➡' or '⇒' or '⟶' => " to ",
                '←' or '⬅' or '⇐' or '⟵'       => " from ",
                '↑' or '⬆' or '⇑'               => " up ",
                '↓' or '⬇' or '⇓'               => " down ",
                '↔' or '⇔'                       => " to and from ",
                '—' or '–'   => ", ",
                '\u2026'      => "...",
                '\u2018' or '\u2019' => "'",
                '\u201C' or '\u201D' => "\"",
                '•' or '◦' or '▪' or '▸' or '►' or '‣' => "-",
                '×'  => " times ",
                '÷'  => " divided by ",
                '≈'  => " approximately ",
                '≠'  => " not equal to ",
                '≤'  => " less than or equal to ",
                '≥'  => " greater than or equal to ",
                '±'  => " plus or minus ",
                '√'  => " square root of ",
                '∞'  => " infinity ",
                '∑'  => " sum ",
                '∏'  => " product ",
                '∂'  => " delta ",
                'π'  => " pi ",
                'µ' or 'μ' => " micro ",
                '°'  => " degrees",
                '©'  => " copyright ",
                '®'  => " registered ",
                '™'  => " trademark ",
                '£'  => " pounds ",
                '€'  => " euros ",
                '¥'  => " yen ",
                '¢'  => " cents ",
                '§'  => " section ",
                '¶'  => " paragraph ",
                '\u00AD' or '\u200B' or '\u200C' or '\u200D' or '\uFEFF' => "",
                '`'  => "",
                '\\' => " ",
                '<' or '>' => " ",
                _    => c <= 127 ? c.ToString() : (char.IsLetter(c) ? c.ToString() : " ")
            };
            sb.Append(replacement);
        }

        var result = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();
        if (result.Length > 0 && !".!?".Contains(result[^1]))
            result += ".";

        return result;
    }

    public void Dispose() => _synth.Dispose();
}
