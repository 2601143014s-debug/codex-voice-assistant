using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexVoiceAssistant.Domain;
using CodexVoiceAssistant.Infrastructure.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CodexVoiceAssistant.Infrastructure.Speech;

public sealed class KokoroSpeechSynthesizer :
    ISpeechSynthesizer,
    IAudioLevelSource
{
    private static readonly TimeSpan StartupTimeout =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SynthesisTimeout =
        TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly KokoroRuntimePaths _paths;
    private readonly string? _outputDeviceId;
    private readonly double _speed;
    private readonly OneCoreSpeechSynthesizer _fallback;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _worker;
    private StreamWriter? _workerInput;
    private StreamReader? _workerOutput;
    private Task? _stderrPump;
    private WasapiOut? _activeOutput;
    private long _nextRequestId;
    private int _disposed;

    public KokoroSpeechSynthesizer(
        string? outputDeviceId,
        double speed)
    {
        _paths = GetInstalledPaths();
        _outputDeviceId = outputDeviceId;
        _speed = Math.Clamp(speed, 0.5, 2.0);
        _fallback = new OneCoreSpeechSynthesizer(outputDeviceId);
        _fallback.LevelChanged += level => LevelChanged?.Invoke(level);
    }

    public event Action<double>? LevelChanged;

    public bool LastUsedFallback { get; private set; }
    public string? LastError { get; private set; }

    public async Task<bool> WarmUpAsync(
        string voice,
        CancellationToken cancellationToken = default)
    {
        if (!_paths.Exist)
        {
            LastError = _paths.DescribeMissingFiles();
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureWorkerAsync(cancellationToken);
            var response = await SendRequestAsync(
                Interlocked.Increment(ref _nextRequestId),
                new
                {
                    type = "warmup",
                    voice = MaleVoiceSelector.SelectKokoroVoice(voice),
                    speed = _speed,
                },
                SynthesisTimeout,
                cancellationToken);
            if (!response.Ok)
            {
                LastError = response.Error;
                return false;
            }

            LastError = null;
            LastUsedFallback = false;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = exception.ToString();
            await ResetWorkerAsync();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SpeakAsync(
        string text,
        string voice,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_paths.Exist)
            {
                try
                {
                    await EnsureWorkerAsync(cancellationToken);
                    var outputPath = Path.Combine(
                        Path.GetTempPath(),
                        "CodexVoiceAssistant",
                        "tts",
                        $"{Guid.NewGuid():N}.wav");
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(outputPath)!);

                    try
                    {
                        var response = await SendRequestAsync(
                            Interlocked.Increment(
                                ref _nextRequestId),
                            new
                            {
                                type = "synthesize",
                                text,
                                voice = MaleVoiceSelector
                                    .SelectKokoroVoice(voice),
                                speed = _speed,
                                output = outputPath,
                            },
                            SynthesisTimeout,
                            cancellationToken);
                        if (!response.Ok)
                        {
                            throw new InvalidOperationException(
                                response.Error
                                ?? "Kokoro synthesis failed.");
                        }

                        LastUsedFallback = false;
                        LastError = null;
                        await PlayWaveAsync(
                            outputPath,
                            cancellationToken);
                        return;
                    }
                    finally
                    {
                        TryDelete(outputPath);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LastError = exception.ToString();
                    await ResetWorkerAsync();
                }
            }
            else
            {
                LastError = _paths.DescribeMissingFiles();
            }

            LastUsedFallback = true;
            await _fallback.SpeakAsync(
                text,
                MaleVoiceSelector.DefaultSapiVoice,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureWorkerAsync(
        CancellationToken cancellationToken)
    {
        if (_worker is { HasExited: false }
            && _workerInput is not null
            && _workerOutput is not null)
        {
            return;
        }

        await ResetWorkerAsync();
        var startInfo = new ProcessStartInfo
        {
            FileName = _paths.PythonPath,
            WorkingDirectory = Path.GetDirectoryName(_paths.WorkerScript)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(_paths.WorkerScript);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(_paths.ModelPath);
        startInfo.ArgumentList.Add("--voices");
        startInfo.ArgumentList.Add(_paths.VoicesPath);
        startInfo.ArgumentList.Add("--default-voice");
        startInfo.ArgumentList.Add(MaleVoiceSelector.DefaultKokoroVoice);
        startInfo.ArgumentList.Add("--default-speed");
        startInfo.ArgumentList.Add(
            _speed.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["ORT_LOG_SEVERITY_LEVEL"] = "3";

        var worker = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Unable to start the Kokoro worker.");
        _worker = worker;
        _workerInput = worker.StandardInput;
        _workerOutput = worker.StandardOutput;
        _stderrPump = PumpStandardErrorAsync(
            worker.StandardError);

        try
        {
            var line = await ReadLineAsync(
                _workerOutput,
                StartupTimeout,
                cancellationToken);
            var message = Deserialize<WorkerMessage>(line);
            if (message.Type == "fatal")
            {
                throw new InvalidOperationException(
                    message.Error
                    ?? "Kokoro worker failed during startup.");
            }
            if (message.Type != "ready")
            {
                throw new InvalidOperationException(
                    $"Unexpected Kokoro startup message: {line}");
            }
        }
        catch
        {
            await ResetWorkerAsync();
            throw;
        }
    }

    private async Task<WorkerResponse> SendRequestAsync(
        long requestId,
        object request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var input = _workerInput
            ?? throw new InvalidOperationException(
                "Kokoro worker input is unavailable.");
        var output = _workerOutput
            ?? throw new InvalidOperationException(
                "Kokoro worker output is unavailable.");

        var node = JsonSerializer.SerializeToNode(request, JsonOptions)
            ?? throw new InvalidOperationException(
                "Unable to serialize the Kokoro request.");
        node["id"] = requestId;
        var payload = node.ToJsonString(JsonOptions);
        await input.WriteLineAsync(
            payload.AsMemory(),
            cancellationToken);
        await input.FlushAsync(cancellationToken);

        while (true)
        {
            var line = await ReadLineAsync(
                output,
                timeout,
                cancellationToken);
            var response = Deserialize<WorkerResponse>(line);
            if (response.Id != requestId)
            {
                continue;
            }
            return response;
        }
    }

    private static async Task<string> ReadLineAsync(
        StreamReader reader,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeoutSource.CancelAfter(timeout);
        string? line;
        try
        {
            line = await reader.ReadLineAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Kokoro worker response timed out.");
        }

        return !string.IsNullOrWhiteSpace(line)
            ? line
            : throw new IOException("Kokoro worker closed its output stream.");
    }

    private async Task PlayWaveAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var device = AudioDeviceService.GetRenderDevice(
            _outputDeviceId);
        using var reader = new WaveFileReader(path);
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2)
        {
            samples = new StereoToMonoSampleProvider(samples);
        }
        samples = new LevelReportingSampleProvider(
            samples,
            level => LevelChanged?.Invoke(level));
        using var output = new WasapiOut(
            device,
            AudioClientShareMode.Shared,
            true,
            80);
        _activeOutput = output;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, eventArgs) =>
        {
            if (eventArgs.Exception is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(eventArgs.Exception);
            }
        };
        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                output.Stop();
            }
            catch
            {
            }
            completion.TrySetCanceled(cancellationToken);
        });
        output.Init(samples.ToWaveProvider());
        output.Play();
        try
        {
            await completion.Task;
        }
        finally
        {
            LevelChanged?.Invoke(0);
            _activeOutput = null;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            _activeOutput?.Stop();
        }
        catch
        {
        }
        await _fallback.StopAsync();
    }

    private async Task PumpStandardErrorAsync(
        StreamReader reader)
    {
        try
        {
            var text = await reader.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                _ = text.Length <= 4000 ? text : text[^4000..];
            }
        }
        catch
        {
        }
    }

    private async Task ResetWorkerAsync()
    {
        var worker = _worker;
        var input = _workerInput;
        var stderrPump = _stderrPump;
        _worker = null;
        _workerInput = null;
        _workerOutput = null;
        _stderrPump = null;

        try
        {
            input?.Close();
        }
        catch
        {
        }
        if (worker is { HasExited: false })
        {
            try
            {
                worker.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
        if (stderrPump is not null)
        {
            try
            {
                await stderrPump;
            }
            catch
            {
            }
        }
        worker?.Dispose();
    }

    private static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException(
                "Kokoro worker returned an empty message.");
    }

    public static KokoroRuntimePaths GetInstalledPaths()
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "CodexVoiceAssistant");
        var runtimeRoot = Path.Combine(dataRoot, "kokoro-runtime");
        return new KokoroRuntimePaths(
            Path.Combine(runtimeRoot, "python.exe"),
            Path.Combine(dataRoot, "models", "kokoro-v1.1-zh.onnx"),
            Path.Combine(runtimeRoot, "voices-v1.1-zh.bin"),
            Path.Combine(
                AppContext.BaseDirectory,
                "KokoroWorker.py"));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync();
        try
        {
            await _gate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            if (_worker is { HasExited: false })
            {
                try
                {
                    await SendRequestAsync(
                        Interlocked.Increment(
                            ref _nextRequestId),
                        new
                        {
                            type = "shutdown",
                        },
                        TimeSpan.FromSeconds(2),
                        CancellationToken.None);
                }
                catch
                {
                }
            }
            await ResetWorkerAsync();
            await _fallback.DisposeAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    public sealed record KokoroRuntimePaths(
        string PythonPath,
        string ModelPath,
        string VoicesPath,
        string WorkerScript)
    {
        public bool Exist =>
            File.Exists(PythonPath)
            && File.Exists(ModelPath)
            && File.Exists(VoicesPath)
            && File.Exists(WorkerScript);

        public string DescribeMissingFiles()
        {
            var missing = new List<string>();
            if (!File.Exists(PythonPath))
            {
                missing.Add(PythonPath);
            }
            if (!File.Exists(ModelPath))
            {
                missing.Add(ModelPath);
            }
            if (!File.Exists(VoicesPath))
            {
                missing.Add(VoicesPath);
            }
            if (!File.Exists(WorkerScript))
            {
                missing.Add(WorkerScript);
            }
            return "Kokoro runtime files are missing: "
                + string.Join(", ", missing);
        }
    }

    private class WorkerMessage
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }
    }

    private sealed class WorkerResponse : WorkerMessage
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; init; }

        [JsonPropertyName("elapsedMs")]
        public long ElapsedMs { get; init; }
    }
}
