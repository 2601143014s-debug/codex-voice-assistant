using System.IO;
using System.Text.Json;
using CodexVoiceAssistant.Domain;
using CodexVoiceAssistant.Infrastructure.Audio;
using CodexVoiceAssistant.Infrastructure.Codex;
using CodexVoiceAssistant.Infrastructure.Logging;
using CodexVoiceAssistant.Infrastructure.Settings;
using CodexVoiceAssistant.Infrastructure.Speech;
using CodexVoiceAssistant.Protocol;

namespace CodexVoiceAssistant.App;

public sealed class AssistantHost : IAsyncDisposable
{
    private readonly JsonSettingsStore _settingsStore = new();
    private readonly FileAppLogger _logger = new();
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private readonly object _transcriptionTasksSync = new();
    private readonly object _healthSync = new();
    private readonly HashSet<Task> _transcriptionTasks = new();
    private readonly string _healthPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "CodexVoiceAssistant",
        "runtime-health.json");
    private AppSettings _settings;
    private ContinuousAudioCapture? _capture;
    private VoiceActivitySegmenter? _segmenter;
    private ITranscriber? _transcriber;
    private ISpeechSynthesizer? _speech;
    private TurnSupervisor? _supervisor;
    private CancellationTokenSource? _transcriptionCancellation;
    private CancellationTokenSource? _warmupCancellation;
    private Task? _warmupTask;
    private CancellationTokenSource? _speechWarmupCancellation;
    private Task? _speechWarmupTask;
    private System.Threading.Timer? _healthTimer;
    private DateTimeOffset _healthStartedAt;
    private string _healthMicrophone = "";
    private string _healthSpeaker = "";
    private double _healthCurrentLevel;
    private double _healthPeakLevel;
    private long _lastLevelTick;
    private bool _running;
    private int _disposed;

    public AssistantHost()
    {
        _settings = _settingsStore.Load();
    }

    public event Action<AssistantState>? StateChanged;
    public event Action<double>? LevelChanged;
    public event Action<double>? ReplyLevelChanged;
    public event Action<string, string>? Exchange;
    public event Action<string>? Error;

    public AssistantState State =>
        _supervisor?.State ?? AssistantState.Stopped;

    public async Task StartAsync()
    {
        if (_running)
        {
            return;
        }

        StateChanged?.Invoke(AssistantState.Starting);
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "CodexVoiceAssistant");
        var model = ResolveModel(
            baseDirectory,
            "ggml-large-v3-turbo-q8_0.bin");
        var sileroModel = ResolveModel(
            baseDirectory,
            "silero_vad.onnx");

        _transcriber = string.Equals(
                _settings.TranscriptionProvider,
                "sensevoice",
                StringComparison.OrdinalIgnoreCase)
                ? new SenseVoiceTranscriber()
                : new WhisperTranscriber(
                    model,
                    model,
                    0,
                    ResolveWhisperWorker());
        _speech = _settings.TtsProvider.ToLowerInvariant() switch
        {
            "kokoro" => new KokoroSpeechSynthesizer(
                _settings.OutputDeviceId,
                _settings.KokoroSpeed),
            "edge" => new EdgeTtsSpeechSynthesizer(
                _settings.OutputDeviceId,
                _settings.Voice,
                _settings.VoiceRatePercent,
                _settings.VoicePitchPercent,
                _settings.EdgeTtsRate,
                _settings.EdgeTtsPitch),
            "sapi" => new SapiSpeechSynthesizer(
                _settings.OutputDeviceId,
                _settings.VoiceRatePercent,
                _settings.VoicePitchPercent),
            _ => new OneCoreSpeechSynthesizer(
                _settings.OutputDeviceId),
        };
        if (_speech is IAudioLevelSource levelSource)
        {
            levelSource.LevelChanged += level =>
                ReplyLevelChanged?.Invoke(level);
        }
        var codex = new CodexSessionAdapter(
            new CodexAppServerOptions
            {
                WorkingDirectory = _settings.Workspace,
                ModelProvider = _settings.ModelProvider,
                Model = _settings.Model,
                Effort = _settings.Effort,
            });
        _supervisor = new TurnSupervisor(
            codex,
            _speech,
            _logger,
            _settings.Voice);
        _supervisor.StateChanged += state => StateChanged?.Invoke(state);
        _supervisor.Error += exception =>
            Error?.Invoke(exception.Message);
        _supervisor.UpdateReceived += update =>
        {
            if (!string.IsNullOrWhiteSpace(update.Delta))
            {
                Exchange?.Invoke("Codex", update.Delta);
            }
        };

        _capture = new ContinuousAudioCapture();
        _segmenter = new VoiceActivitySegmenter(
            _settings.VadThreshold,
            _settings.SilenceMilliseconds,
            _settings.MaxUtteranceSeconds,
            sileroModelPath: sileroModel);
        _capture.FrameAvailable += frame =>
        {
            try
            {
                _segmenter?.Process(frame);
            }
            catch (Exception exception)
            {
                _logger.Error(
                    "Voice activity processing failed.",
                    exception);
            }
        };
        _capture.Faulted += exception =>
        {
            _logger.Error("Audio capture failed.", exception);
            Error?.Invoke(exception.Message);
        };
        _segmenter.LevelChanged += ReportLevel;
        _segmenter.SpeechStarted += () =>
        {
            _logger.Info("Speech started.");
            if (_supervisor.State is AssistantState.Thinking
                or AssistantState.Speaking)
            {
                _ = _supervisor.InterruptAsync();
            }
            if (_recognitionGate.CurrentCount == 0)
            {
                _transcriptionCancellation?.Cancel();
            }
        };
        _segmenter.UtteranceReady += (samples, duration) =>
        {
            _logger.Info(
                $"Utterance captured: duration_ms={duration.TotalMilliseconds:F0} "
                + $"samples={samples.Length}");
            QueueTranscription(samples, duration);
        };

        await _supervisor.StartAsync();
        _speechWarmupCancellation = new CancellationTokenSource();
        _speechWarmupTask = Task.Run(
            () => WarmUpSpeechAsync(_speechWarmupCancellation.Token),
            CancellationToken.None);
        using var captureDevice = AudioDeviceService.GetCaptureDevice(
            _settings.CaptureDeviceId);
        using var renderDevice = AudioDeviceService.GetRenderDevice(
            _settings.OutputDeviceId);
        AudioDeviceService.EnsureCaptureReady(captureDevice);
        _logger.Info(
            $"Opening audio devices: microphone='{captureDevice.FriendlyName}' "
            + $"speaker='{renderDevice.FriendlyName}'.");
        _healthMicrophone = captureDevice.FriendlyName;
        _healthSpeaker = renderDevice.FriendlyName;
        var captureStart = Task.Run(
            () => _capture.Start(
                _settings.CaptureDeviceId,
                _settings.OutputDeviceId));
        try
        {
            await captureStart.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            _logger.Warning(
                "Audio capture startup did not return within 3 seconds; "
                + "continuing because the capture threads are active.");
            _ = captureStart.ContinueWith(
                task =>
                {
                    if (task.Exception is not null)
                    {
                        _logger.Error(
                            "Deferred audio capture startup failed.",
                            task.Exception.GetBaseException());
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        _logger.Info("Audio capture started.");
        _running = true;
        _healthStartedAt = DateTimeOffset.UtcNow;
        _healthTimer = new System.Threading.Timer(
            _ => WriteHealth(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2));
        StateChanged?.Invoke(AssistantState.Starting);
        _warmupCancellation = new CancellationTokenSource();
        _warmupTask = Task.Run(
            () => WarmUpRecognitionAsync(_warmupCancellation.Token),
            CancellationToken.None);
        _logger.Info("Voice assistant started.");
    }

    private void QueueTranscription(
        float[] samples,
        TimeSpan duration)
    {
        Task task;
        lock (_transcriptionTasksSync)
        {
            if (!_running || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            task = TranscribeAndSubmitAsync(samples, duration);
            _transcriptionTasks.Add(task);
        }
        _ = task.ContinueWith(
            completed =>
            {
                lock (_transcriptionTasksSync)
                {
                    _transcriptionTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WarmUpRecognitionAsync(
        CancellationToken cancellationToken)
    {
        if (_transcriber is null)
        {
            return;
        }

        try
        {
            var started = DateTimeOffset.UtcNow;
            await _transcriber.WarmupAsync(cancellationToken);
            _logger.Stage(
                new TimedStage(
                    "transcription-warmup",
                    started,
                    DateTimeOffset.UtcNow - started,
                    Succeeded: true));
            if (_supervisor?.State == AssistantState.Listening)
            {
                StateChanged?.Invoke(AssistantState.Listening);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("Recognition warmup failed.", exception);
        }
    }

    private async Task WarmUpSpeechAsync(
        CancellationToken cancellationToken)
    {
        if (_speech is not KokoroSpeechSynthesizer kokoro)
        {
            return;
        }

        var started = DateTimeOffset.UtcNow;
        try
        {
            var succeeded = await kokoro.WarmUpAsync(
                _settings.Voice,
                cancellationToken);
            _logger.Stage(
                new TimedStage(
                    "kokoro-warmup",
                    started,
                    DateTimeOffset.UtcNow - started,
                    succeeded,
                    Error: succeeded
                        ? null
                        : kokoro.LastError));
            if (!succeeded)
            {
                _logger.Warning(
                    "Kokoro warmup failed; OneCore male fallback remains "
                    + "available for speech playback.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("Speech warmup failed.", exception);
        }
    }

    private async Task TranscribeAndSubmitAsync(
        float[] samples,
        TimeSpan duration)
    {
        await _recognitionGate.WaitAsync();
        using var transcriptionCancellation =
            new CancellationTokenSource();
        _transcriptionCancellation = transcriptionCancellation;
        try
        {
            if (_transcriber is null || _supervisor is null)
            {
                return;
            }

            StateChanged?.Invoke(AssistantState.Transcribing);
            var recognitionStarted = DateTimeOffset.UtcNow;
            var transcript = await _transcriber.TranscribeAsync(
                samples,
                ContinuousAudioCapture.SampleRate,
                transcriptionCancellation.Token);
            _logger.Info(
                $"Transcription completed: chars={transcript.Text.Length} "
                + $"duration_ms="
                + $"{(DateTimeOffset.UtcNow - recognitionStarted).TotalMilliseconds:F0}");
            if (string.IsNullOrWhiteSpace(transcript.Text))
            {
                _logger.Warning(
                    "Transcription returned no text; ignoring segment.");
                StateChanged?.Invoke(AssistantState.Listening);
                return;
            }
            Exchange?.Invoke("你", transcript.Text);
            await _supervisor.SubmitAsync(transcript);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("Transcription failed.", exception);
            Error?.Invoke(exception.Message);
            StateChanged?.Invoke(AssistantState.Listening);
        }
        finally
        {
            if (ReferenceEquals(
                    _transcriptionCancellation,
                    transcriptionCancellation))
            {
                _transcriptionCancellation = null;
            }
            _recognitionGate.Release();
        }
    }

    private void ReportLevel(double level)
    {
        lock (_healthSync)
        {
            _healthCurrentLevel = level;
            _healthPeakLevel = Math.Max(_healthPeakLevel, level);
        }
        var now = Environment.TickCount64;
        if (now - _lastLevelTick < 80)
        {
            return;
        }
        _lastLevelTick = now;
        LevelChanged?.Invoke(level);
    }

    private void WriteHealth()
    {
        if (!_running || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(
                new
                {
                    processId = Environment.ProcessId,
                    startedUtc = _healthStartedAt,
                    updatedUtc = DateTimeOffset.UtcNow,
                    state = State.ToString(),
                    microphone = _healthMicrophone,
                    speaker = _healthSpeaker,
                    currentLevel = Math.Round(_healthCurrentLevel, 6),
                    peakLevel = Math.Round(_healthPeakLevel, 6),
                    vadThreshold = _settings.VadThreshold,
                    transcriptionProvider =
                        _settings.TranscriptionProvider,
                    transcriptionEngine =
                        _transcriber?.GetType().Name ?? "none",
                });
            lock (_healthSync)
            {
                var temporary = $"{_healthPath}.{Environment.ProcessId}.tmp";
                File.WriteAllText(temporary, payload);
                File.Move(temporary, _healthPath, overwrite: true);
                _healthPeakLevel = _healthCurrentLevel;
            }
        }
        catch
        {
        }
    }

    private static string ResolveModel(
        string baseDirectory,
        string fileName)
    {
        var local = Path.Combine(baseDirectory, "models", fileName);
        if (File.Exists(local))
        {
            return local;
        }

        var legacy = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Jarvis",
            "models",
            fileName);
        return File.Exists(legacy) ? legacy : local;
    }

    private static string? ResolveWhisperWorker()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "CodexVoiceAssistant.WhisperWorker.exe");
        return File.Exists(path) ? path : null;
    }

    public Task InterruptAsync() =>
        _supervisor?.InterruptAsync() ?? Task.CompletedTask;

    public void ReportStartupFailure(Exception exception)
    {
        _logger.Error("Startup failed.", exception);
        Error?.Invoke(exception.Message);
        StateChanged?.Invoke(AssistantState.Faulted);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_transcriptionTasksSync)
        {
            _running = false;
        }
        _healthTimer?.Dispose();
        _healthTimer = null;
        TryDeleteHealth();
        _transcriptionCancellation?.Cancel();
        if (_capture is not null)
        {
            await _capture.DisposeAsync();
        }
        while (true)
        {
            Task[] pending;
            lock (_transcriptionTasksSync)
            {
                pending = _transcriptionTasks.ToArray();
            }
            if (pending.Length == 0)
            {
                break;
            }
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        if (_warmupCancellation is not null)
        {
            _warmupCancellation.Cancel();
        }
        if (_speechWarmupCancellation is not null)
        {
            _speechWarmupCancellation.Cancel();
        }
        if (_warmupTask is not null)
        {
            try
            {
                await _warmupTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }
        if (_speechWarmupTask is not null)
        {
            try
            {
                await _speechWarmupTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync();
        }
        else if (_speech is not null)
        {
            await _speech.DisposeAsync();
        }
        if (_transcriber is not null)
        {
            await _transcriber.DisposeAsync();
        }
        _segmenter?.Dispose();
        _warmupCancellation?.Dispose();
        _speechWarmupCancellation?.Dispose();
        _recognitionGate.Dispose();
        _logger.Dispose();
    }

    private void TryDeleteHealth()
    {
        try
        {
            lock (_healthSync)
            {
                if (File.Exists(_healthPath))
                {
                    File.Delete(_healthPath);
                }
            }
        }
        catch
        {
        }
    }
}
