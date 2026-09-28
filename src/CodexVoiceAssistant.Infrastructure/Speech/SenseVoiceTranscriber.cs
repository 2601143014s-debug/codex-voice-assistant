using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CodexVoiceAssistant.Domain;

namespace CodexVoiceAssistant.Infrastructure.Speech;

public sealed class SenseVoiceTranscriber : ITranscriber
{
    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SenseVoicePaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private int _disposed;

    public SenseVoiceTranscriber()
    {
        _paths = GetInstalledPaths();
    }

    public static bool IsAvailable => GetInstalledPaths().Exist;

    public async Task WarmupAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
                new WorkerRequest
                {
                    Command = "warmup",
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.Ok)
        {
            throw new InvalidOperationException(
                response.Error
                ?? "SenseVoice warmup failed.");
        }
    }

    public async Task<Transcript> TranscribeAsync(
        ReadOnlyMemory<float> audio,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(
            audio.Length / (double)sampleRate);
        var response = await SendAsync(
                new WorkerRequest
                {
                    Command = "transcribe",
                    SampleRate = sampleRate,
                    AudioBase64 = Convert.ToBase64String(
                        System.Runtime.InteropServices.MemoryMarshal
                            .AsBytes(audio.Span)),
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.Ok)
        {
            throw new InvalidOperationException(
                response.Error
                ?? "SenseVoice recognition failed.");
        }

        return new Transcript(
            response.Text ?? "",
            0.95,
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
                    var line = JsonSerializer.Serialize(
                        request,
                        JsonOptions);
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
                            "SenseVoice worker has exited.");
                    }

                    var response =
                        JsonSerializer.Deserialize<WorkerResponse>(
                            responseLine,
                            JsonOptions)
                        ?? throw new InvalidOperationException(
                            "SenseVoice worker returned an empty "
                            + "response.");
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
                "SenseVoice worker could not be started.");
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
        foreach (var path in _paths.RequiredFiles)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "SenseVoice runtime file is missing.",
                    path);
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _paths.PythonPath,
            WorkingDirectory = Path.GetDirectoryName(
                _paths.WorkerScript)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add(_paths.WorkerScript);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(_paths.ModelPath);
        startInfo.ArgumentList.Add("--tokens");
        startInfo.ArgumentList.Add(_paths.TokensPath);
        startInfo.ArgumentList.Add("--runtime");
        startInfo.ArgumentList.Add(_paths.RuntimePath);
        startInfo.ArgumentList.Add("--language");
        startInfo.ArgumentList.Add("auto");
        startInfo.ArgumentList.Add("--threads");
        startInfo.ArgumentList.Add("4");
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Unable to start the SenseVoice worker.");
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
                        Trace.WriteLine($"SENSEVOICE {line}");
                    }
                }
                catch
                {
                }
            },
            CancellationToken.None);

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var readyLine = await _reader.ReadLineAsync(timeout.Token)
            .ConfigureAwait(false);
        var ready = readyLine is null
            ? null
            : JsonSerializer.Deserialize<ReadyMessage>(
                readyLine,
                JsonOptions);
        if (ready?.Type == "fatal")
        {
            throw new InvalidOperationException(
                ready.Error
                ?? "SenseVoice worker failed to start.");
        }
        if (ready?.Type != "ready")
        {
            throw new InvalidOperationException(
                $"Unexpected SenseVoice startup message: {readyLine}");
        }
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
                    using var timeout = new CancellationTokenSource(
                        TimeSpan.FromSeconds(2));
                    await _process.WaitForExitAsync(timeout.Token)
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

    private static SenseVoicePaths GetInstalledPaths()
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "CodexVoiceAssistant");
        return new SenseVoicePaths(
            Path.Combine(dataRoot, "kokoro-runtime", "python.exe"),
            Path.Combine(dataRoot, "sensevoice-runtime"),
            Path.Combine(
                dataRoot,
                "models",
                "sensevoice-model.int8.onnx"),
            Path.Combine(
                dataRoot,
                "models",
                "sensevoice-tokens.txt"),
            Path.Combine(
                AppContext.BaseDirectory,
                "SenseVoiceWorker.py"));
    }

    private sealed record SenseVoicePaths(
        string PythonPath,
        string RuntimePath,
        string ModelPath,
        string TokensPath,
        string WorkerScript)
    {
        public bool Exist => RequiredFiles.All(File.Exists);

        public string[] RequiredFiles =>
        [
            PythonPath,
            Path.Combine(RuntimePath, "sherpa_onnx", "__init__.py"),
            ModelPath,
            TokensPath,
            WorkerScript,
        ];
    }

    private sealed class ReadyMessage
    {
        public string? Type { get; init; }
        public string? Error { get; init; }
    }

    private sealed class WorkerRequest
    {
        public string Command { get; init; } = "";
        public int SampleRate { get; init; }
        public string? AudioBase64 { get; init; }
    }

    private sealed class WorkerResponse
    {
        public bool Ok { get; init; }
        public string? Text { get; init; }
        public string? Error { get; init; }
    }
}
