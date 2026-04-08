using System.Text;
using Anthropic;
using Anthropic.Models.Messages;

namespace claude_voice;

public sealed class ClaudeService
{
    private readonly AnthropicClient _client;
    private readonly List<MessageParam> _history = [];

    public string SystemPrompt { get; set; }

    public ClaudeService(string apiKey, string systemPrompt)
    {
        _client       = new AnthropicClient() { ApiKey = apiKey };
        SystemPrompt  = systemPrompt;
    }

    public async Task StreamResponseAsync(
        string userMessage,
        Action<string> onToken,
        CancellationToken ct = default)
    {
        _history.Add(new MessageParam { Role = Role.User, Content = userMessage });

        var parameters = new MessageCreateParams
        {
            Model = Model.ClaudeOpus4_6,
            MaxTokens = 64000,
            System = SystemPrompt,
            Messages = [.. _history],
        };

        var fullResponse = new StringBuilder();

        try
        {
            await foreach (var streamEvent in _client.Messages.CreateStreaming(parameters).WithCancellation(ct))
            {
                if (streamEvent.TryPickContentBlockDelta(out var delta) &&
                    delta.Delta.TryPickText(out var text))
                {
                    fullResponse.Append(text.Text);
                    onToken(text.Text);
                }
            }

            _history.Add(new MessageParam { Role = Role.Assistant, Content = fullResponse.ToString() });
        }
        catch (OperationCanceledException)
        {
            // Remove the dangling user message so history stays consistent
            _history.RemoveAt(_history.Count - 1);
            throw;
        }
        catch (Exception ex)
        {
            // Remove the dangling user message so history stays consistent
            _history.RemoveAt(_history.Count - 1);

            // Surface a friendly message for content-policy blocks
            if (ex.Message.Contains("content filtering", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("content filter",    StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Output blocked",    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Claude declined to respond — that request was blocked by the content policy.", ex);
            }

            throw;
        }
    }

    public void ClearHistory() => _history.Clear();

    public void LoadHistory(IEnumerable<MemoryEntry> entries)
    {
        _history.Clear();
        foreach (var e in entries)
            _history.Add(new MessageParam
            {
                Role    = string.Equals(e.Role, "user", StringComparison.OrdinalIgnoreCase)
                              ? Role.User : Role.Assistant,
                Content = e.Content,
            });
    }
}
