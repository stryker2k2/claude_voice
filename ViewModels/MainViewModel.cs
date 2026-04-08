using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

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

    // -------------------------------------------------------------------------
    // Bindable state

    private string _statusText      = "Ready";
    private Brush  _statusBrush     = _readyBrush;
    private bool   _isPttProcessing;
    private bool   _isBusy;
    private bool   _isRecording;
    private bool   _isWakeWordActive;
    private string _userInputText = "";

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public string StatusText        { get => _statusText;        private set => SetField(ref _statusText, value); }
    public Brush  StatusBrush       { get => _statusBrush;       private set => SetField(ref _statusBrush, value); }
    public bool   IsBusy            { get => _isBusy;            private set { SetField(ref _isBusy, value);            RaiseCanExecute(); } }
    public bool   IsRecording       { get => _isRecording;       private set { SetField(ref _isRecording, value);       RaiseCanExecute(); } }
    public bool   IsPttProcessing   { get => _isPttProcessing;   private set { SetField(ref _isPttProcessing, value);   RaiseCanExecute(); } }
    public bool   IsWakeWordActive  { get => _isWakeWordActive;  private set { SetField(ref _isWakeWordActive, value);  RaiseCanExecute(); } }
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

    // -------------------------------------------------------------------------
    // PTT key (read by View for keyboard handling)

    public Key PttKey { get; private set; }

    // -------------------------------------------------------------------------
    // Commands

    public ICommand          SendCommand            { get; }
    public ICommand          StartPttCommand        { get; }
    public AsyncRelayCommand StopPttCommand         { get; }
    public ICommand          ToggleWakeWordCommand  { get; }

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
        _claude = new ClaudeService(_config.AnthropicApiKey, _config.SystemPrompt);
        _tts    = new TtsEngine(_config);
        PttKey  = Enum.TryParse<Key>(_config.PttKey, ignoreCase: true, out var k) ? k : Key.F5;

        try { _stt = new SttService(ResolveModelPath(_config.WhisperModel)); }
        catch (FileNotFoundException ex) { _initWarning = ex.Message; }

        SendCommand           = new AsyncRelayCommand(SendAsync,    () => !IsBusy);
        StartPttCommand       = new RelayCommand(StartPtt,          () => CanPtt);
        StopPttCommand        = new AsyncRelayCommand(StopPttAsync);
        ToggleWakeWordCommand = new RelayCommand(ToggleWakeWord,    () => IsWakeWordEnabled);
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
        if (_stt is null || !_stt.IsRecording) return;
        IsRecording     = false;
        IsPttProcessing = true;
        SetStatus("Transcribing...", StatusKind.Busy);
        try
        {
            var text = await _stt.StopAndTranscribeAsync();
            if (!string.IsNullOrWhiteSpace(text))
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

        // Pause wake word engine so it doesn't double-fire during recording
        _wakeWord?.StopListening();

        IsRecording     = true;
        IsPttProcessing = true;
        SetStatus("Recording...", StatusKind.Busy);

        try
        {
            var text = await _stt.AutoRecordAndTranscribeAsync(
                silenceTimeout: TimeSpan.FromSeconds(1.5),
                maxDuration:    TimeSpan.FromSeconds(10));

            IsRecording = false;
            SetStatus("Transcribing...", StatusKind.Busy);

            if (!string.IsNullOrWhiteSpace(text))
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
            IsRecording     = false;
            IsPttProcessing = false;

            // Resume wake word listening if still toggled on
            if (IsWakeWordActive)
                _wakeWord?.StartListening();
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

        var userMsg      = new ChatMessage { Role = "user",      Text = userText };
        var assistantMsg = new ChatMessage { Role = "assistant"             };

        Application.Current.Dispatcher.Invoke(() =>
        {
            Messages.Add(userMsg);
            Messages.Add(assistantMsg);
            ScrollRequested?.Invoke(this, EventArgs.Empty);
        });

        _streamCts = new CancellationTokenSource();
        var sb = new StringBuilder();

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
                },
                _streamCts.Token);

            _ = Task.Run(() => _tts.SpeakAsync(sb.ToString()), CancellationToken.None);
            SetStatus("Ready");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled");
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() => assistantMsg.Text = $"Error: {ex.Message}");
            SetStatus("Error", StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
            _streamCts.Dispose();
            _streamCts = null;
        }
    }

    // -------------------------------------------------------------------------
    // Settings

    public SettingsViewModel CreateSettingsViewModel()
    {
        var voices       = ScanPiperVoices();
        var currentModel = ResolveModelPath(_config.PiperModel ?? "");
        return new SettingsViewModel(
            _claude.SystemPrompt, voices, currentModel,
            _config.PttKey ?? "F5", _config.WakeWord);
    }

    public void ApplySettings(SettingsViewModel vm)
    {
        _claude.SystemPrompt = vm.SystemPrompt;

        var newModel = vm.SelectedVoice?.FullPath ?? _config.PiperModel ?? "";
        if (!string.IsNullOrEmpty(newModel))
            _tts.ChangeVoice(newModel);

        PttKey = Enum.TryParse<Key>(vm.PttKey, ignoreCase: true, out var k) ? k : PttKey;

        // If wake word text changed and listening is active, recreate the service
        var wakeWordChanged = !string.Equals(vm.WakeWord, _config.WakeWord,
            StringComparison.OrdinalIgnoreCase);

        _config = new AppConfig
        {
            AnthropicApiKey = _config.AnthropicApiKey,
            WhisperModel    = _config.WhisperModel,
            PiperExe        = _config.PiperExe,
            PiperModel      = ToRelativePath(newModel),
            TtsRate         = _config.TtsRate,
            TtsVolume       = _config.TtsVolume,
            PttKey          = vm.PttKey,
            WakeWord        = vm.WakeWord,
            SystemPrompt    = vm.SystemPrompt,
        };
        AppConfig.Save(_config);

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

    private enum StatusKind { Ready, Busy, Error }

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

    public void Dispose()
    {
        StopWakeWord();
        _tts.Dispose();
    }
}
