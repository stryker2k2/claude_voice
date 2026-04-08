using System.Text;
using Anthropic;
using Anthropic.Models.Messages;
using BetaMsgs = Anthropic.Models.Beta.Messages;

namespace claude_voice;

public sealed class ClaudeService
{
    private readonly AnthropicClient _client;
    private readonly List<MessageParam> _history = [];
    private bool _enableWebSearch;

    public string SystemPrompt { get; set; }

    /// <summary>
    /// Optional callback invoked with status strings (e.g. "Searching...") during
    /// server-side tool use so the UI can reflect what's happening.
    /// </summary>
    public Action<string>? OnStatusUpdate { get; set; }

    public ClaudeService(string apiKey, string systemPrompt, bool enableWebSearch = false)
    {
        _client          = new AnthropicClient() { ApiKey = apiKey };
        SystemPrompt     = systemPrompt;
        _enableWebSearch = enableWebSearch;
    }

    public void SetWebSearch(bool enabled) => _enableWebSearch = enabled;

    public async Task StreamResponseAsync(
        string userMessage,
        Action<string> onToken,
        CancellationToken ct = default)
    {
        _history.Add(new MessageParam { Role = Role.User, Content = userMessage });

        if (_enableWebSearch)
            await StreamWithSearchAsync(onToken, ct);
        else
            await StreamDirectAsync(onToken, ct);
    }

    // -------------------------------------------------------------------------
    // Direct streaming (no tools)

    private async Task StreamDirectAsync(Action<string> onToken, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model     = Model.ClaudeOpus4_6,
            MaxTokens = 64000,
            System    = SystemPrompt,
            Messages  = [.. _history],
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
            _history.RemoveAt(_history.Count - 1);
            throw;
        }
        catch (Exception ex)
        {
            _history.RemoveAt(_history.Count - 1);
            ThrowFriendlyIfPolicyBlock(ex);
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Beta streaming with built-in web search (web_search_20250305)

    private async Task StreamWithSearchAsync(Action<string> onToken, CancellationToken ct)
    {
        // Convert non-beta history to BetaMessageParam for the Beta endpoint.
        // All entries in _history use plain string content, so TryPickString always succeeds.
        var betaMessages = _history.Select(m =>
        {
            m.Content.TryPickString(out var str);
            return new BetaMsgs.BetaMessageParam
            {
                Role    = (string)m.Role,
                Content = str ?? "",
            };
        }).ToList();

        var parameters = new BetaMsgs.MessageCreateParams
        {
            Model     = Model.ClaudeOpus4_6,
            MaxTokens = 64000,
            System    = SystemPrompt,
            Messages  = betaMessages,
            Tools     = [new BetaMsgs.BetaWebSearchTool20250305()],
            Betas     = ["web-search-2025-03-05"],
        };

        var fullResponse = new StringBuilder();
        bool isSearching = false;

        try
        {
            await foreach (var ev in _client.Beta.Messages.CreateStreaming(parameters, ct))
            {
                // Track whether we're inside a server-tool-use block (the web search).
                // ContentBlockStart fires once per block before any deltas for that block,
                // so the flag is always set correctly before any delta arrives.
                if (ev.TryPickContentBlockStart(out var blockStart))
                {
                    isSearching = blockStart.ContentBlock.TryPickBetaServerToolUse(out _);
                    if (isSearching) OnStatusUpdate?.Invoke("Searching...");
                }

                // Only forward text deltas to TTS — skip all search metadata
                if (!isSearching &&
                    ev.TryPickContentBlockDelta(out var delta) &&
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
            _history.RemoveAt(_history.Count - 1);
            throw;
        }
        catch (Exception ex)
        {
            _history.RemoveAt(_history.Count - 1);
            ThrowFriendlyIfPolicyBlock(ex);
            throw;
        }
    }

    // -------------------------------------------------------------------------

    private static void ThrowFriendlyIfPolicyBlock(Exception ex)
    {
        if (ex.Message.Contains("content filtering", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("content filter",    StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Output blocked",    StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Claude declined to respond — that request was blocked by the content policy.", ex);
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
