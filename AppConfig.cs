using System.IO;
using System.Text.Json;

namespace claude_voice;

public sealed class AppConfig
{
    public string AnthropicApiKey { get; init; } = "";
    public string WhisperModel    { get; init; } = "whisper\\ggml-base.en.bin";

    public static AppConfig Load()
    {
        // Look next to the exe first, then fall back to the working directory (dotnet run)
        var locations = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "config.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "config.json"),
        };

        foreach (var path in locations)
        {
            if (!File.Exists(path)) continue;

            var json = File.ReadAllText(path);
            var cfg  = JsonSerializer.Deserialize<AppConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (cfg is not null) return cfg;
        }

        throw new FileNotFoundException(
            "config.json not found. Copy config.example.json to config.json and fill in your API key.");
    }
}
