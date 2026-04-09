using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.Wave;

namespace claude_voice;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ClaudeService _claude;
    private readonly SttService?   _stt;
    private readonly TtsEngine     _tts;
    private          AppConfig     _config;
    private          CancellationTokenSource? _streamCts;
    private          string?       _initWarning;
    private          WakeWordService? _wakeWord;
    private          CancellationTokenSource? _ttsCts;
    private          CancellationTokenSource? _autoRecordCts;
    private readonly MemoryService _memory = new();
    private readonly List<MemoryEntry> _memoryEntries = [];

    // -------------------------------------------------------------------------
    // Bindable state

    private string _statusText      = "Ready";
    private Brush  _statusBrush     = _readyBrush;
    private bool   _isPttProcessing;
    private bool   _isBusy;
    private bool   _isRecording;
    private bool   _isWakeWordActive;
    private bool   _isSpeaking;
    private string _userInputText = "";
    private double _micLevel;
    private double _micDb;

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public string StatusText        { get => _statusText;        private set => SetField(ref _statusText, value); }
    public Brush  StatusBrush       { get => _statusBrush;       private set => SetField(ref _statusBrush, value); }
    public bool   IsBusy            { get => _isBusy;            private set { SetField(ref _isBusy, value);            RaiseCanExecute(); } }
    public bool   IsRecording       { get => _isRecording;       private set { SetField(ref _isRecording, value);       RaiseCanExecute(); } }
    public bool   IsPttProcessing   { get => _isPttProcessing;   private set { SetField(ref _isPttProcessing, value);   RaiseCanExecute(); } }
    public bool   IsWakeWordActive  { get => _isWakeWordActive;  private set { SetField(ref _isWakeWordActive, value);  RaiseCanExecute(); } }
    public bool   IsSpeaking        { get => _isSpeaking;        private set { SetField(ref _isSpeaking, value);        RaiseCanExecute(); } }
    public bool   CanPtt            => !_isBusy && !_isPttProcessing && _stt is not null;
    public bool   CanUsePtt         => _stt is not null && !_isBusy && !_isPttProcessing;
    public bool   IsPttEnabled      => _stt is not null;
    public bool   IsWakeWordEnabled => _stt is not null;
    public string PttTooltip        => IsPttEnabled ? "Hold to Talk" : "Run download-whisper.ps1 to enable PTT";
    public string WakeWordTooltip   => IsWakeWordEnabled
        ? $"Toggle always-on listening (wake word: \"{_config.WakeWord}\")"
        : "Run download-whisper.ps1 to enable wake word";

    public string UserInputText
    {
        get => _userInputText;
        set => SetField(ref _userInputText, value);
    }

    public double MicLevel { get => _micLevel; private set => SetField(ref _micLevel, value); }
    public double MicDb    { get => _micDb;    private set => SetField(ref _micDb, value); }

    // -------------------------------------------------------------------------
    // PTT key (read by View for keyboard handling)

    public Key PttKey { get; private set; }

    // -------------------------------------------------------------------------
    // Commands

    public ICommand          SendCommand            { get; }
    public ICommand          StartPttCommand        { get; }
    public AsyncRelayCommand StopPttCommand         { get; }
    public ICommand          ToggleWakeWordCommand  { get; }
    public ICommand          StopSpeakingCommand    { get; }

    // -------------------------------------------------------------------------
    // Events raised for the View

    public event EventHandler?        ScrollRequested;
    public event EventHandler<string>? WarningRaised;

    // -------------------------------------------------------------------------

    private static readonly SolidColorBrush _readyBrush = new(Color.FromRgb(0x6A, 0x99, 0x55));
    private static readonly SolidColorBrush _busyBrush  = new(Color.FromRgb(0xDC, 0xDC, 0xAA));
    private static readonly SolidColorBrush _errorBrush = new(Colors.OrangeRed);

    // -------------------------------------------------------------------------

    public MainViewModel()
    {
        _config = AppConfig.Load();
        _claude = new ClaudeService(_config.AnthropicApiKey, _config.SystemPrompt, _config.EnableWebSearch);
        _claude.OnStatusUpdate = status =>
            Application.Current.Dispatcher.Invoke(() => SetStatus(status, StatusKind.Busy));
        _tts    = new TtsEngine(_config);
        PttKey  = Enum.TryParse<Key>(_config.PttKey, ignoreCase: true, out var k) ? k : Key.F5;

        if (_config.EnableMemory)
        {
            _memoryEntries.AddRange(_memory.Load());
            if (_memoryEntries.Count > 0)
                _claude.LoadHistory(_memoryEntries);
        }

        try { _stt = new SttService(ResolveModelPath(_config.WhisperModel)); }
        catch (FileNotFoundException ex) { _initWarning = ex.Message; }

        if (_stt is not null)
        {
            int _meterTick = 0;
            var meterTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(50) };
            meterTimer.Tick += (_, _) =>
            {
                var rms = _stt.CurrentRms;
                const double FloorDb = -60.0;
                var db  = rms > 1.0 ? 20.0 * Math.Log10(rms / 32767.0) : -100.0;
                // Update readable dBFS number at ~300 ms so it's catchable by eye
                if (++_meterTick % 6 == 0) MicDb = Math.Max(db, FloorDb);
                var raw = Math.Clamp((db - FloorDb) / (-FloorDb), 0.0, 1.0);
                // Fast attack, slow decay — bar snaps up instantly, falls gradually
                MicLevel = raw > MicLevel ? raw : MicLevel * 0.75;
            };
            meterTimer.Start();
        }

        SendCommand           = new AsyncRelayCommand(SendAsync,    () => !IsBusy);
        StartPttCommand       = new RelayCommand(StartPtt,          () => CanPtt);
        StopPttCommand        = new AsyncRelayCommand(StopPttAsync);
        ToggleWakeWordCommand = new RelayCommand(ToggleWakeWord,    () => IsWakeWordEnabled);
        StopSpeakingCommand   = new RelayCommand(StopSpeaking,      () => IsSpeaking);
    }

    /// <summary>Call after the View has subscribed to events.</summary>
    public void RaiseInitialWarnings()
    {
        if (_initWarning is not null)
            WarningRaised?.Invoke(this, _initWarning);
    }

    // -------------------------------------------------------------------------
    // PTT

    public void StartPtt()
    {
        if (_stt is null || IsBusy || IsPttProcessing || IsRecording) return;
        IsRecording = true;
        SetStatus("Recording...", StatusKind.Busy);
        _stt.StartRecording();
    }

    public async Task StopPttAsync()
    {
        // Only act on a manual PTT session (IsRecording = true).
        // Auto-record sessions (wake word / conversation mode) do not set IsRecording,
        // so we must not interfere with them.
        if (_stt is null || !_stt.IsRecording || !IsRecording) return;
        IsRecording     = false;
        IsPttProcessing = true;
        SetStatus("Transcribing...", StatusKind.Busy);
        try
        {
            var text = await _stt.StopAndTranscribeAsync();
            if (!string.IsNullOrWhiteSpace(text) && !IsNoiseTranscription(text))
                await SendCoreAsync(text);
            else
                SetStatus("Ready");
        }
        catch (Exception ex)
        {
            SetStatus($"STT error: {ex.Message}", StatusKind.Error);
        }
        finally
        {
            IsPttProcessing = false;
        }
    }

    // -------------------------------------------------------------------------
    // Wake word

    private void ToggleWakeWord()
    {
        if (!IsWakeWordEnabled) return;

        if (IsWakeWordActive)
        {
            StopWakeWord();
        }
        else
        {
            try
            {
                _wakeWord = new WakeWordService(_config.WakeWord);
                _wakeWord.WakeWordDetected += OnWakeWordDetected;
                _wakeWord.StartListening();
                IsWakeWordActive = true;
                SetStatus($"Listening for \"{_config.WakeWord}\"...");
            }
            catch (Exception ex)
            {
                SetStatus($"Wake word error: {ex.Message}", StatusKind.Error);
            }
        }
    }

    private void StopWakeWord()
    {
        // If auto-recording is in progress, cancel it immediately (no transcription)
        _autoRecordCts?.Cancel();

        _wakeWord?.StopListening();
        _wakeWord?.Dispose();
        _wakeWord = null;
        IsWakeWordActive = false;
        SetStatus("Ready");
    }

    private async void OnWakeWordDetected(object? sender, EventArgs e)
    {
        // Ignore if already busy
        if (_stt is null || IsBusy || IsPttProcessing || IsRecording) return;

        PlayBloop();

        // Pause wake word engine so it doesn't double-fire during recording
        _wakeWord?.StopListening();

        // Use only IsPttProcessing to block manual PTT — NOT IsRecording,
        // which would turn the Hold-to-Talk button red and confuse the user
        // into clicking it, which would race with AutoRecordAndTranscribeAsync.
        IsPttProcessing = true;
        SetStatus("Recording...", StatusKind.Busy);

        _autoRecordCts?.Dispose();
        _autoRecordCts = new CancellationTokenSource();

        try
        {
            var (text, hitMax) = await _stt.AutoRecordAndTranscribeAsync(
                silenceTimeout:   TimeSpan.FromSeconds(_config.SilenceTimeout),
                maxDuration:      TimeSpan.FromSeconds(60),
                silenceThreshold: DbFsToRms(_config.VoiceThresholdDb),
                noSpeechTimeout:  TimeSpan.FromSeconds(10),
                ct:               _autoRecordCts.Token);

            SetStatus("Transcribing...", StatusKind.Busy);
            if (hitMax) AddSystemNote("— recording limit reached, message sent —");

            if (!string.IsNullOrWhiteSpace(text) && !IsNoiseTranscription(text))
                await SendCoreAsync(text);
            else
                SetStatus("Ready");
        }
        catch (Exception ex)
        {
            SetStatus($"STT error: {ex.Message}", StatusKind.Error);
        }
        finally
        {
            IsPttProcessing = false;
            // Wake word is resumed by ResumeAfterSpeakingAsync once TTS finishes
        }
    }

    // -------------------------------------------------------------------------
    // Send

    private async Task SendAsync()
    {
        var text = UserInputText.Trim();
        if (string.IsNullOrEmpty(text)) return;
        UserInputText = "";
        await SendCoreAsync(text);
    }

    private async Task SendCoreAsync(string userText)
    {
        IsBusy = true;

        var userMsg      = new ChatMessage { Role = "user",      DisplayName = "You",                    Text = userText };
        var assistantMsg = new ChatMessage { Role = "assistant", DisplayName = _config.AssistantName };

        Application.Current.Dispatcher.Invoke(() =>
        {
            Messages.Add(userMsg);
            Messages.Add(assistantMsg);
            ScrollRequested?.Invoke(this, EventArgs.Empty);
        });

        _streamCts = new CancellationTokenSource();
        var sb             = new StringBuilder();
        var sentenceBuffer = new StringBuilder();
        var sentenceChannel = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true });

        // Start TTS consumer — speaks each sentence as soon as it is enqueued,
        // running concurrently with the streaming below.
        _ttsCts?.Cancel();
        _ttsCts?.Dispose();
        _ttsCts = new CancellationTokenSource();
        var ttsCts = _ttsCts;
        IsSpeaking = true;
        _tts.PrepareForNewResponse();
        SetStatus("Talking...", StatusKind.Busy);
        if (IsWakeWordActive) _wakeWord?.StopListening();

        _ = Task.Run(async () =>
        {
            // Two-stage pipeline: synthesize chunk N+1 while chunk N is playing.
            // A bounded channel of 1 means synthesis stays exactly one chunk ahead.
            var wavChannel = Channel.CreateBounded<string>(
                new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });

            var synthTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var sentence in sentenceChannel.Reader.ReadAllAsync(ttsCts.Token))
                    {
                        var wav = await _tts.SynthesizeAsync(sentence, ttsCts.Token);
                        if (wav != null) await wavChannel.Writer.WriteAsync(wav, ttsCts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                finally { wavChannel.Writer.TryComplete(); }
            }, CancellationToken.None);

            try
            {
                await foreach (var wav in wavChannel.Reader.ReadAllAsync(ttsCts.Token))
                    await _tts.PlayPrerenderedAsync(wav, ttsCts.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                await synthTask;
                await ResumeAfterSpeakingAsync();
            }
        }, CancellationToken.None);

        try
        {
            await _claude.StreamResponseAsync(
                userText,
                token =>
                {
                    sb.Append(token);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        assistantMsg.Text = sb.ToString();
                        ScrollRequested?.Invoke(this, EventArgs.Empty);
                    });

                    // Buffer token and flush any complete sentences to the TTS queue
                    sentenceBuffer.Append(token);
                    FlushSentences(sentenceBuffer, sentenceChannel.Writer);
                },
                _streamCts.Token);

            // Flush whatever is left in the buffer after streaming ends
            var remaining = sentenceBuffer.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(remaining))
                sentenceChannel.Writer.TryWrite(remaining);

            if (_config.EnableMemory)
            {
                _memoryEntries.Add(new MemoryEntry("user",      userText));
                _memoryEntries.Add(new MemoryEntry("assistant", sb.ToString()));
                _memory.Save(_memoryEntries);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled");
        }
        catch (Exception ex)
        {
            // Only overwrite the bubble if nothing was generated yet.
            // If content was already streaming (and TTS is speaking it), leave it intact.
            if (sb.Length == 0)
                Application.Current.Dispatcher.Invoke(() => assistantMsg.Text = FriendlyErrorMessage(ex));
            SetStatus($"Error: {ErrorLabel(ex)}", StatusKind.Error);
        }
        finally
        {
            // Signal TTS consumer that no more sentences are coming
            sentenceChannel.Writer.TryComplete();
            IsBusy = false;
            _streamCts.Dispose();
            _streamCts = null;
        }
    }

    /// <summary>
    /// Scans <paramref name="buffer"/> for complete sentences and writes chunks to
    /// the TTS channel once enough text has accumulated, leaving any incomplete tail
    /// in the buffer.
    ///
    /// Chunks are only emitted when the accumulated text reaches
    /// <see cref="MinTtsChunkChars"/> characters, or on a paragraph break.
    /// This prevents Piper from being invoked for every tiny sentence, which adds
    /// per-process startup overhead and makes short responses slower.
    /// </summary>
    private static void FlushSentences(StringBuilder buffer, ChannelWriter<string> writer)
    {
        const int MinTtsChunkChars = 400;

        var text  = buffer.ToString();
        int start = 0;

        for (int i = 0; i < text.Length - 1; i++)
        {
            char c    = text[i];
            char next = text[i + 1];

            bool isSentenceEnd = (c is '.' or '!' or '?') &&
                                 (char.IsWhiteSpace(next) || next is '\r' or '\n');

            if (!isSentenceEnd) continue;

            // Skip whitespace after the break point
            int skip = i + 1;
            while (skip < text.Length && char.IsWhiteSpace(text[skip])) skip++;

            var candidate = text[start..skip].Trim();

            // Emit only once enough text has accumulated — paragraph breaks no longer
            // force early emission so short paragraphs flow together into one chunk.
            if (candidate.Length >= MinTtsChunkChars)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                    writer.TryWrite(candidate);
                start = skip;
                i     = start - 1; // loop will increment
            }
            // Otherwise keep accumulating sentences
        }

        buffer.Clear();
        if (start < text.Length)
            buffer.Append(text[start..]);
    }

    // -------------------------------------------------------------------------
    // Settings

    public SettingsViewModel CreateSettingsViewModel()
    {
        var voices       = ScanPiperVoices();
        var currentModel = ResolveModelPath(_config.PiperModel ?? "");
        return new SettingsViewModel(
            _config.AnthropicApiKey, _claude.SystemPrompt, voices, currentModel,
            _config.PttKey ?? "F5", _config.WakeWord, _config.AssistantName,
            _config.EnableMemory, _config.EnableWebSearch, _config.SilenceTimeout, _config.VoiceThresholdDb,
            wipeMemoryAction: () =>
            {
                _memory.Wipe();
                _memoryEntries.Clear();
                _claude.ClearHistory();
            });
    }

    public void ApplySettings(SettingsViewModel vm)
    {
        _claude.SystemPrompt = vm.SystemPrompt;
        _claude.SetWebSearch(vm.EnableWebSearch);
        if (!string.IsNullOrWhiteSpace(vm.ApiKey))
            _claude.SetApiKey(vm.ApiKey);

        var newModel = vm.SelectedVoice?.FullPath ?? _config.PiperModel ?? "";
        if (!string.IsNullOrEmpty(newModel))
            _tts.ChangeVoice(newModel);

        PttKey = Enum.TryParse<Key>(vm.PttKey, ignoreCase: true, out var k) ? k : PttKey;

        // If wake word text changed and listening is active, recreate the service
        var wakeWordChanged = !string.Equals(vm.WakeWord, _config.WakeWord,
            StringComparison.OrdinalIgnoreCase);

        _config = new AppConfig
        {
            AnthropicApiKey = string.IsNullOrWhiteSpace(vm.ApiKey) ? _config.AnthropicApiKey : vm.ApiKey,
            WhisperModel    = _config.WhisperModel,
            PiperExe        = _config.PiperExe,
            PiperModel      = ToRelativePath(newModel),
            TtsRate         = _config.TtsRate,
            TtsVolume       = _config.TtsVolume,
            PttKey          = vm.PttKey,
            WakeWord        = vm.WakeWord,
            SystemPrompt    = vm.SystemPrompt,
            AssistantName   = vm.AssistantName,
            EnableMemory    = vm.EnableMemory,
            EnableWebSearch = vm.EnableWebSearch,
            SilenceTimeout    = vm.SilenceTimeout,
            VoiceThresholdDb  = vm.VoiceThresholdDb,
        };
        try
        {
            AppConfig.Save(_config);
        }
        catch (Exception ex)
        {
            SetStatus($"Settings saved in memory, but could not write config file: {ex.Message}", StatusKind.Error);
        }

        if (wakeWordChanged && IsWakeWordActive)
        {
            StopWakeWord();
            ToggleWakeWord(); // restart with new word
        }

        // Refresh tooltip
        OnPropertyChanged(nameof(WakeWordTooltip));
    }

    // -------------------------------------------------------------------------
    // Helpers

    /// <summary>
    /// Synthesises and plays a short upward frequency sweep ("bloop") to confirm
    /// wake-word detection. Runs fire-and-forget on a thread-pool thread.
    /// </summary>
    private static void PlayBloop()
    {
        Task.Run(() =>
        {
            try
            {
                const int    sampleRate = 44100;
                const int    durationMs = 220;
                const int    numSamples = sampleRate * durationMs / 1000;
                const double startFreq  = 220.0;
                const double endFreq    = 480.0;
                const double totalTime  = durationMs / 1000.0;

                var samples = new float[numSamples];
                for (int i = 0; i < numSamples; i++)
                {
                    double t     = (double)i / sampleRate;
                    // Linear chirp: integrate instantaneous frequency to get phase
                    double phase = 2 * Math.PI *
                        (startFreq * t + (endFreq - startFreq) / (2.0 * totalTime) * t * t);
                    // Envelope: 10 ms attack, 70 ms release
                    double attack  = Math.Min(1.0, t / 0.010);
                    double release = Math.Min(1.0, (totalTime - t) / 0.070);
                    samples[i] = (float)(Math.Sin(phase) * 0.40 * attack * release);
                }

                var fmt = new WaveFormat(sampleRate, 16, 1);
                using var ms     = new MemoryStream();
                using var writer = new WaveFileWriter(ms, fmt);
                writer.WriteSamples(samples, 0, samples.Length);
                writer.Flush();

                ms.Position = 0;
                using var reader  = new WaveFileReader(ms);
                using var waveOut = new WaveOutEvent();
                waveOut.Init(reader);
                waveOut.Play();
                while (waveOut.PlaybackState == PlaybackState.Playing)
                    System.Threading.Thread.Sleep(10);
            }
            catch { /* best-effort audio */ }
        });
    }

    /// <summary>
    /// Returns true for Whisper noise annotations like [BLANK_AUDIO], [ Silence ], etc.
    /// These should not be forwarded to Claude.
    /// </summary>
    private static bool IsNoiseTranscription(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), @"^[\[\(].*[\]\)]$");

    /// <summary>Returns a human-friendly message for display in the chat bubble.</summary>
    private static string FriendlyErrorMessage(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("credit balance", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("too low to access", StringComparison.OrdinalIgnoreCase))
            return "I'm not able to respond right now — the API credit balance is too low. Please visit Anthropic's Plans & Billing to add credits.";
        if (msg.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("unauthorized",   StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("api key",        StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("401",            StringComparison.Ordinal))
            return "I'm not able to respond right now — the API key looks invalid. Please check your config.json and make sure the Anthropic API key is correct.";
        return $"Error: {ex.Message}";
    }

    /// <summary>Returns a short label for display in the status bar.</summary>
    private static string ErrorLabel(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("credit balance", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("too low to access", StringComparison.OrdinalIgnoreCase))
            return "Out of Credits";
        if (msg.Contains("Output blocked",    StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("content filter",    StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("content filtering", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("content policy",    StringComparison.OrdinalIgnoreCase))
            return "Content Filter";
        if (msg.Contains("copyright",    StringComparison.OrdinalIgnoreCase))
            return "Copyright Filter";
        if (msg.Contains("rate limit",   StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("rate_limit",   StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("429",          StringComparison.Ordinal))
            return "Rate Limited";
        if (msg.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("api key",      StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("401",          StringComparison.Ordinal))
            return "Auth Failed";
        if (msg.Contains("timeout",      StringComparison.OrdinalIgnoreCase))
            return "Timed Out";
        if (msg.Contains("network",      StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("connection",   StringComparison.OrdinalIgnoreCase))
            return "Network Error";
        // Fallback: first 40 chars of the message
        return msg.Length > 40 ? msg[..40].TrimEnd() + "…" : msg;
    }

    private enum StatusKind { Ready, Busy, Error }

    private static double DbFsToRms(double dBFS) => 32767.0 * Math.Pow(10.0, dBFS / 20.0);

    private void AddSystemNote(string text) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            Messages.Add(new ChatMessage { Role = "system", Text = text });
            ScrollRequested?.Invoke(this, EventArgs.Empty);
        });

    private void SetStatus(string text, StatusKind kind = StatusKind.Ready)
    {
        StatusText  = text;
        StatusBrush = kind switch
        {
            StatusKind.Busy  => _busyBrush,
            StatusKind.Error => _errorBrush,
            _                => _readyBrush,
        };
    }

    private void RaiseCanExecute() => CommandManager.InvalidateRequerySuggested();

    private static IReadOnlyList<VoiceOption> ScanPiperVoices()
    {
        var piperDir = Path.Combine(AppContext.BaseDirectory, "piper");
        if (!Directory.Exists(piperDir))
            piperDir = Path.Combine(Directory.GetCurrentDirectory(), "piper");
        if (!Directory.Exists(piperDir)) return [];

        return Directory.GetFiles(piperDir, "*.onnx")
                        .Where(f => !f.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(Path.GetFileName)
                        .Select(f => new VoiceOption(FriendlyName(f), f))
                        .ToList();
    }

    private static string FriendlyName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.IndexOf('-');
        if (dash >= 0) name = name[(dash + 1)..];
        var parts = name.Split('-');
        return parts.Length >= 2
            ? $"{char.ToUpper(parts[0][0])}{parts[0][1..]} ({char.ToUpper(parts[1][0])}{parts[1][1..]})"
            : char.ToUpper(name[0]) + name[1..];
    }

    private static string ResolveModelPath(string configured)
    {
        if (Path.IsPathRooted(configured) && File.Exists(configured)) return configured;
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory,        configured),
            Path.Combine(Directory.GetCurrentDirectory(), configured),
        };
        return candidates.FirstOrDefault(File.Exists) ?? configured;
    }

    private static string ToRelativePath(string fullPath)
    {
        var baseDir = AppContext.BaseDirectory;
        if (fullPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
            return fullPath[baseDir.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath;
    }

    /// <summary>
    /// Called after TTS finishes. If wake word mode is active, opens a 5-second
    /// listen window so the user can reply without re-saying the wake word.
    /// Falls back to wake word listening if no speech is detected in time.
    /// </summary>
    private async Task ResumeAfterSpeakingAsync()
    {
        Application.Current.Dispatcher.Invoke(() => IsSpeaking = false);

        if (!IsWakeWordActive)
        {
            Application.Current.Dispatcher.Invoke(() => { if (_statusBrush != _errorBrush) SetStatus("Ready"); });
            return;
        }

        // Brief pause so the last word of TTS fully clears the mic
        await Task.Delay(1000);

        if (!IsWakeWordActive || _stt is null)
        {
            Application.Current.Dispatcher.Invoke(() => SetStatus("Ready"));
            return;
        }

        // Open a follow-up listen window — user has 5 s to start speaking.
        // Only set IsPttProcessing (blocks PTT) — NOT IsRecording, which would
        // turn the Hold-to-Talk button red and confuse the user.
        IsPttProcessing = true;
        Application.Current.Dispatcher.Invoke(() => SetStatus("Your turn...", StatusKind.Ready));

        _autoRecordCts?.Dispose();
        _autoRecordCts = new CancellationTokenSource();

        string text = "";
        try
        {
            (text, var hitMax) = await _stt.AutoRecordAndTranscribeAsync(
                silenceTimeout:   TimeSpan.FromSeconds(_config.SilenceTimeout),
                maxDuration:      TimeSpan.FromSeconds(60),
                silenceThreshold: DbFsToRms(_config.VoiceThresholdDb),
                noSpeechTimeout:  TimeSpan.FromSeconds(5),
                ct:               _autoRecordCts.Token);

            if (hitMax) AddSystemNote("— recording limit reached, message sent —");

            if (!string.IsNullOrWhiteSpace(text) && !IsNoiseTranscription(text))
            {
                // Keep conversation going — SendCoreAsync will handle TTS and loop back here
                await SendCoreAsync(text);
                return;
            }
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
                SetStatus($"STT error: {ex.Message}", StatusKind.Error));
        }
        finally
        {
            IsPttProcessing = false;
        }

        // No speech detected — resume wake word listening
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (IsWakeWordActive)
            {
                _wakeWord?.StartListening();
                SetStatus($"Listening for \"{_config.WakeWord}\"...");
            }
            else if (_statusBrush != _errorBrush)
            {
                SetStatus("Ready");
            }
        });
    }

    private void StopSpeaking()
    {
        _ttsCts?.Cancel();
        _ttsCts?.Dispose();
        _ttsCts = null;
        _tts.Stop();
        IsSpeaking = false;
    }

    public void Dispose()
    {
        StopWakeWord();
        StopSpeaking();
        _autoRecordCts?.Dispose();
        _tts.Dispose();
    }
}
