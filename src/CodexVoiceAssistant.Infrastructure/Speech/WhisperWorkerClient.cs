using System.Diagnostics;
using System.Text.Json;
using CodexVoiceAssistant.Domain;

namespace CodexVoiceAssistant.Infrastructure.Speech;

internal sealed class WhisperWorkerClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(45);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _executable;
    private readonly string _smallModelPath;
    private readonly string _largeModelPath;
    private readonly int _largeThresholdSeconds;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private int _disposed;

    public WhisperWorkerClient(
        string executable,
        string smallModelPath,
        string largeModelPath,
        int largeThresholdSeconds)
    {
        _executable = executable;
        _smallModelPath = smallModelPath;
        _largeModelPath = largeModelPath;
        _largeThresholdSeconds = largeThresholdSeconds;
    }

    public async Task WarmupAsync(CancellationToken cancellationToken)
    {
        await SendAsync(
                new WorkerRequest
                {
                    Command = "warmup",
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Transcript> TranscribeAsync(
        ReadOnlyMemory<float> audio,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(
            audio.Length / (double)sampleRate);
        var useLarge = duration.TotalSeconds >= _largeThresholdSeconds
                       && File.Exists(_largeModelPath);
        var audioBytes = System.Runtime.InteropServices.MemoryMarshal
            .AsBytes(audio.Span)
            .ToArray();
        var response = await SendAsync(
                new WorkerRequest
                {
                    Command = "transcribe",
                    Model = useLarge ? "large" : "small",
                    SampleRate = sampleRate,
                    AudioBase64 = Convert.ToBase64String(audioBytes),
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            throw new InvalidOperationException(
                response.Error ?? "Whisper worker 执行失败。");
        }

        return new Transcript(
            response.Text ?? "",
            response.Confidence ?? 0.8f,
            DateTimeOffset.Now,
            duration);
    }

    private async Task<WorkerResponse> SendAsync(
        WorkerRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await EnsureStartedAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var line = JsonSerializer.Serialize(request, JsonOptions);
                    await _writer!.WriteLineAsync(
                            line.AsMemory(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await _writer.FlushAsync(cancellationToken)
                        .ConfigureAwait(false);

                    using var timeout =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);
                    timeout.CancelAfter(RequestTimeout);
                    var responseLine = await _reader!
                        .ReadLineAsync(timeout.Token)
                        .ConfigureAwait(false);
                    if (responseLine is null)
                    {
                        throw new IOException(
                            "Whisper worker 已退出。");
                    }

                    var response =
                        JsonSerializer.Deserialize<WorkerResponse>(
                            responseLine,
                            JsonOptions)
                        ?? throw new InvalidOperationException(
                            "Whisper worker 返回了空响应。");
                    if (!response.Ok && attempt == 0)
                    {
                        KillWorker();
                        continue;
                    }
                    return response;
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    KillWorker();
                    throw;
                }
                catch when (attempt == 0)
                {
                    KillWorker();
                }
            }

            throw new InvalidOperationException(
                "Whisper worker 无法启动。");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureStartedAsync(
        CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        KillWorker();
        if (!File.Exists(_executable))
        {
            throw new FileNotFoundException(
                "Whisper worker 可执行文件不存在。",
                _executable);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add("--worker");
        startInfo.ArgumentList.Add(_smallModelPath);
        startInfo.ArgumentList.Add(_largeModelPath);
        startInfo.ArgumentList.Add(
            _largeThresholdSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException(
                          "无法启动 Whisper worker。");
        _process = process;
        _writer = process.StandardInput;
        _reader = process.StandardOutput;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (await process.StandardError
                               .ReadLineAsync(cancellationToken)
                               .ConfigureAwait(false) is { } line)
                    {
                        Trace.WriteLine($"WHISPER {line}");
                    }
                }
                catch
                {
                }
            },
            CancellationToken.None);
        await Task.Yield();
    }

    private void KillWorker()
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
        }
        try
        {
            _reader?.Dispose();
        }
        catch
        {
        }
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        _process?.Dispose();
        _process = null;
        _writer = null;
        _reader = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _writer is not null)
            {
                try
                {
                    await _writer.WriteLineAsync(
                            """{"command":"exit"}""")
                        .ConfigureAwait(false);
                    await _writer.FlushAsync().ConfigureAwait(false);
                    await _process.WaitForExitAsync(
                            new CancellationTokenSource(
                                TimeSpan.FromSeconds(2)).Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }
            KillWorker();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private sealed class WorkerRequest
    {
        public string Command { get; init; } = "";
        public string? Model { get; init; }
        public int SampleRate { get; init; }
        public string? AudioBase64 { get; init; }
    }

    private sealed class WorkerResponse
    {
        public bool Ok { get; init; }
        public string? Text { get; init; }
        public float? Confidence { get; init; }
        public string? Error { get; init; }
    }
}
