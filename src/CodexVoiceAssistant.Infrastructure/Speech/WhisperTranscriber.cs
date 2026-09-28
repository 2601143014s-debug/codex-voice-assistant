using System.IO;
using CodexVoiceAssistant.Domain;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace CodexVoiceAssistant.Infrastructure.Speech;

public sealed class WhisperTranscriber : ITranscriber
{
    private const string ChineseCommandPrompt =
        "以下是用户对 Windows 电脑的中文语音指令。"
        + "常见词包括：贾维斯、Codex、打开、关闭、网易云音乐、"
        + "微信、浏览器、文件、程序、电脑、声音、麦克风、"
        + "请只回复、不要执行。";

    private readonly string _smallModelPath;
    private readonly string _largeModelPath;
    private readonly int _largeThresholdSeconds;
    private readonly WhisperWorkerClient? _worker;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _smallFactory;
    private WhisperFactory? _largeFactory;

    public WhisperTranscriber(
        string smallModelPath,
        string largeModelPath,
        int largeThresholdSeconds,
        string? workerExecutable = null)
    {
        if (!string.IsNullOrWhiteSpace(workerExecutable))
        {
            _worker = new WhisperWorkerClient(
                workerExecutable,
                smallModelPath,
                largeModelPath,
                largeThresholdSeconds);
        }
        else
        {
            RuntimeOptions.RuntimeLibraryOrder =
                string.Equals(
                    Environment.GetEnvironmentVariable(
                        "CODEX_VOICE_FORCE_CPU"),
                    "1",
                    StringComparison.Ordinal)
                    ? [RuntimeLibrary.Cpu]
                    : [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];
        }
        _smallModelPath = smallModelPath;
        _largeModelPath = largeModelPath;
        _largeThresholdSeconds = largeThresholdSeconds;
    }

    public async Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        if (_worker is not null)
        {
            await _worker.WarmupAsync(cancellationToken);
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_smallModelPath))
            {
                _smallFactory ??= WhisperFactory.FromPath(_smallModelPath);
                await ProcessSilenceAsync(
                    _smallFactory,
                    TimeSpan.FromSeconds(1),
                    cancellationToken);
            }

            if (File.Exists(_largeModelPath))
            {
                _largeFactory ??= WhisperFactory.FromPath(_largeModelPath);
                await ProcessSilenceAsync(
                    _largeFactory,
                    TimeSpan.FromSeconds(2),
                    cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Transcript> TranscribeAsync(
        ReadOnlyMemory<float> audio,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        if (_worker is not null)
        {
            return await _worker.TranscribeAsync(
                audio,
                sampleRate,
                cancellationToken);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var duration = TimeSpan.FromSeconds(
                audio.Length / (double)sampleRate);
            var useLarge = duration.TotalSeconds >= _largeThresholdSeconds
                           && File.Exists(_largeModelPath);
            var modelPath = useLarge ? _largeModelPath : _smallModelPath;
            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException(
                    "Whisper 模型文件不存在。",
                    modelPath);
            }

            var factory = useLarge
                ? _largeFactory ??= WhisperFactory.FromPath(modelPath)
                : _smallFactory ??= WhisperFactory.FromPath(modelPath);
            await using var processor = factory
                .CreateBuilder()
                .WithLanguage("zh")
                .WithPrompt(ChineseCommandPrompt)
                .WithCarryInitialPrompt(true)
                .WithThreads(Math.Max(2, Environment.ProcessorCount - 1))
                .WithNoContext()
                .WithTemperature(0.0f)
                .Build();

            await using var wave = CreateWaveStream(audio.Span, sampleRate);
            var parts = new List<string>();
            var probabilities = new List<float>();
            await foreach (var segment in processor.ProcessAsync(
                               wave,
                               cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    parts.Add(segment.Text.Trim());
                    probabilities.Add(segment.Probability);
                }
            }

            var text = string.Join("", parts).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException(
                    "没有识别到清晰的语音。");
            }

            var averageProbability = probabilities.Count == 0
                ? 0f
                : probabilities.Average();
            return new Transcript(
                text,
                averageProbability > 0f
                    ? averageProbability
                    : 0.8f,
                DateTimeOffset.Now,
                duration);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static MemoryStream CreateWaveStream(
        ReadOnlySpan<float> samples,
        int sampleRate)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal
            .AsBytes(samples)
            .ToArray();
        using var source = new RawSourceWaveStream(
            new MemoryStream(bytes),
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        ISampleProvider provider = source.ToSampleProvider();
        if (sampleRate != 16000)
        {
            provider = new WdlResamplingSampleProvider(provider, 16000);
        }

        var output = new MemoryStream();
        WaveFileWriter.WriteWavFileToStream(
            output,
            new SampleToWaveProvider16(provider));
        output.Position = 0;
        return output;
    }

    private static async Task ProcessSilenceAsync(
        WhisperFactory factory,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await using var processor = factory
            .CreateBuilder()
            .WithLanguage("zh")
            .WithNoContext()
            .WithTemperature(0.0f)
            .Build();
        var sampleCount = (int)(duration.TotalSeconds * 16000);
        await using var wave = CreateWaveStream(
            new float[sampleCount],
            16000);
        await foreach (var _ in processor.ProcessAsync(
                           wave,
                           cancellationToken))
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_worker is not null)
        {
            return DisposeWorkerAsync();
        }

        _smallFactory?.Dispose();
        _largeFactory?.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask DisposeWorkerAsync()
    {
        await _worker!.DisposeAsync();
        _gate.Dispose();
    }
}
