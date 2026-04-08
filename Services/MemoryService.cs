using System.IO;
using System.Text.Json;

namespace claude_voice;

public record MemoryEntry(string Role, string Content);

/// <summary>
/// Persists conversation history to memory.json so Ryan remembers
/// previous sessions when memory is enabled.
/// </summary>
public sealed class MemoryService
{
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    // Cap stored messages so the file doesn't grow unbounded (~100 exchanges)
    private const int MaxMessages = 200;

    private readonly string _filePath;

    public MemoryService()
    {
        _filePath = Path.Combine(AppContext.BaseDirectory, "memory.json");
    }

    public IReadOnlyList<MemoryEntry> Load()
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<MemoryEntry>>(
                File.ReadAllText(_filePath), _opts) ?? [];
        }
        catch { return []; }
    }

    public void Save(IReadOnlyList<MemoryEntry> entries)
    {
        var toSave = entries.Count > MaxMessages
            ? entries.Skip(entries.Count - MaxMessages).ToList()
            : entries;
        File.WriteAllText(_filePath, JsonSerializer.Serialize(toSave, _opts));
    }

    public void Wipe()
    {
        try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { /* best-effort */ }
    }
}
