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

    public void ClearHistory() => _history.Clear();
}
