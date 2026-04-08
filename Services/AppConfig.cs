using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace claude_voice;

public sealed class AppConfig
{
    public string AnthropicApiKey { get; init; } = "";
    public string WhisperModel    { get; init; } = "whisper\\ggml-base.en.bin";

    // TTS settings (mirrors claude_tts config.json)
    public string? PiperExe   { get; init; }
    public string? PiperModel { get; init; }
    public double  TtsRate    { get; init; } = 0;
    public int     TtsVolume  { get; init; } = 100;

    // PTT keyboard shortcut — any System.Windows.Input.Key name, e.g. "F5", "LeftCtrl"
    public string PttKey { get; init; } = "F5";

    // Wake word for always-on listening mode (e.g. "hey claude")
    public string WakeWord { get; init; } = "hey claude";

    // Claude system prompt
    public string SystemPrompt { get; init; } =
        "You are a helpful voice assistant. Keep responses conversational and concise — they will be spoken aloud.";

    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions _readOptions  = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    private static string? _loadedPath;

    public static AppConfig Load()
    {
        var locations = new[]
        {
            Path.Combine(AppContext.BaseDirectory,        "config.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "config.json"),
        };

        foreach (var path in locations)
        {
            if (!File.Exists(path)) continue;

            var json = File.ReadAllText(path);
            var cfg  = JsonSerializer.Deserialize<AppConfig>(json, _readOptions);
            if (cfg is not null)
            {
                _loadedPath = path;
                return cfg;
            }
        }

        throw new FileNotFoundException(
            "config.json not found. Copy config.example.json to config.json and fill in your API key.");
    }

    /// <summary>Saves the supplied config over the file that was originally loaded.</summary>
    public static void Save(AppConfig updated)
    {
        if (_loadedPath is null) return;
        File.WriteAllText(_loadedPath, JsonSerializer.Serialize(updated, _writeOptions));
    }
}
