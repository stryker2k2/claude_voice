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

    /// <summary>Returns all voices visible to the WinRT SpeechSynthesizer (informational).</summary>
    public IReadOnlyList<VoiceInformation> GetAvailableVoices() =>
        SpeechSynthesizer.AllVoices;

    /// <summary>
    /// Synthesizes <paramref name="text"/> and plays it synchronously.
    /// </summary>
    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (_usePiper)
            await SpeakWithPiperAsync(text, ct);
        else
            await SpeakWithWinRtAsync(text, ct);
    }

    // -------------------------------------------------------------------------
    // Piper backend

    private async Task SpeakWithPiperAsync(string text, CancellationToken ct)
    {
        text = NormalizeForSpeech(text);
        if (string.IsNullOrWhiteSpace(text)) return;

        var tmpFile = Path.Combine(Path.GetTempPath(), $"claude_voice_tts_{Guid.NewGuid():N}.wav");
        try
        {
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
                throw new InvalidOperationException($"piper.exe exited with code {proc.ExitCode}");

            using var player = new SoundPlayer(tmpFile);
            player.PlaySync();
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { /* best-effort */ }
        }
    }

    // -------------------------------------------------------------------------
    // WinRT backend

    private async Task SpeakWithWinRtAsync(string text, CancellationToken ct)
    {
        var stream = await _synth.SynthesizeTextToStreamAsync(text);

        var inputStream = stream.GetInputStreamAt(0);
        var dataReader  = new DataReader(inputStream);
        await dataReader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        dataReader.ReadBytes(bytes);

        var tmpFile = Path.Combine(Path.GetTempPath(), $"claude_voice_tts_{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(tmpFile, bytes, ct);
            using var player = new SoundPlayer(tmpFile);
            player.PlaySync();
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { /* best-effort */ }
        }
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
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[ \t]*[-*]\s+",  "", ML);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`\r\n]+)`", "$1");
        text = text.Replace("\"", "");
        text = text.Replace("(", ", ").Replace(")", ", ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\.[/\\]", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(\w)\.(\w)", "$1 $2");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\r?\n", ", , ");

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
