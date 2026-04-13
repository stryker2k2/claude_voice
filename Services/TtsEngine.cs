using System.Diagnostics;
using System.IO;
using System.Media;
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
    private SoundPlayer? _player;
    private readonly object _playerLock = new();

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

    /// <summary>No-op — kept for call-site compatibility. Device stays warm via persistent WaveOutEvent.</summary>
    public void PrepareForNewResponse() { }

    /// <summary>Immediately stops any audio currently playing.</summary>
    public void Stop()
    {
        lock (_playerLock) { _player?.Stop(); }
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
        PrependSilence(wavFile, silenceMs: 200);

        using var player = new SoundPlayer(wavFile);
        lock (_playerLock) { _player = player; }

        // SoundPlayer.PlaySync() is truly blocking — it doesn't return during
        // punctuation pauses, so there's no spurious "playback done" signal
        // mid-chunk. Run it on a background thread so we can cancel via Stop().
        using var reg = ct.Register(() => { lock (_playerLock) { _player?.Stop(); } });
        await Task.Run(() => player.PlaySync(), CancellationToken.None);

        lock (_playerLock) { if (_player == player) _player = null; }
    }

    /// <summary>
    /// Prepends <paramref name="silenceMs"/> ms of zero-filled PCM silence to the WAV
    /// file in-place. Handles the audio-device cold-start problem where Windows takes
    /// ~150-200 ms to initialise the endpoint, which otherwise clips the first syllable.
    /// </summary>
    private static void PrependSilence(string wavFile, int silenceMs)
    {
        var wav = File.ReadAllBytes(wavFile);
        if (wav.Length < 44) return;

        ushort channels      = BitConverter.ToUInt16(wav, 22);
        uint   sampleRate    = BitConverter.ToUInt32(wav, 24);
        ushort bitsPerSample = BitConverter.ToUInt16(wav, 34);
        int    blockAlign    = channels * bitsPerSample / 8;

        int silenceBytes = (int)(sampleRate * silenceMs / 1000) * blockAlign;

        uint newDataSize = BitConverter.ToUInt32(wav, 40) + (uint)silenceBytes;
        uint newRiffSize = (uint)(wav.Length - 8 + silenceBytes);

        var result = new byte[wav.Length + silenceBytes];
        Array.Copy(wav, result, 44);
        BitConverter.GetBytes(newRiffSize).CopyTo(result, 4);
        BitConverter.GetBytes(newDataSize).CopyTo(result, 40);
        // bytes 44..(44+silenceBytes-1) are already zero — silence
        Array.Copy(wav, 44, result, 44 + silenceBytes, wav.Length - 44);

        File.WriteAllBytes(wavFile, result);
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
        // Expand acronyms so Piper spells them out: XML → X M L, UI → U I
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\b([A-Z]{2,})\b",
            m => string.Join(" ", m.Value.ToCharArray()));
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
