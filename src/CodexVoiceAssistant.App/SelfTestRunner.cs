using System.IO;
using System.Speech.Synthesis;
using System.Text.Json;
using CodexVoiceAssistant.Domain;
using CodexVoiceAssistant.Infrastructure.Audio;
using CodexVoiceAssistant.Infrastructure.Settings;
using CodexVoiceAssistant.Infrastructure.Speech;
using CodexVoiceAssistant.Protocol;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CodexVoiceAssistant.App;

internal static class SelfTestRunner
{
    public static async Task<int> RunAsync()
    {
        var report = new Dictionary<string, object?>();
        var exitCode = 0;
        try
        {
            var settings = new JsonSettingsStore().Load();
            await using var capture = new ContinuousAudioCapture();
            using var captureDevice = AudioDeviceService.GetCaptureDevice(
                settings.CaptureDeviceId);
            using var renderDevice = AudioDeviceService.GetRenderDevice(
                settings.OutputDeviceId);
            var frames = 0;
            var maxLevel = 0d;
            capture.FrameAvailable += frame =>
            {
                Interlocked.Increment(ref frames);
                maxLevel = Math.Max(maxLevel, frame.Level);
            };
            capture.Start(
                settings.CaptureDeviceId,
                settings.OutputDeviceId);
            await Task.Delay(500);
            var framesBeforeRecovery = Volatile.Read(ref frames);
            capture.SimulateDeviceFailureForDiagnostics();
            await Task.Delay(2500);
            var framesAfterRecovery = Volatile.Read(ref frames);
            if (framesAfterRecovery <= framesBeforeRecovery + 20)
            {
                throw new InvalidOperationException(
                    "音频设备故障注入后没有恢复采集。");
            }
            var useEdgeTts = string.Equals(
                settings.TtsProvider,
                "edge",
                StringComparison.OrdinalIgnoreCase);
            var useSapi = string.Equals(
                settings.TtsProvider,
                "sapi",
                StringComparison.OrdinalIgnoreCase);
            var useKokoro = string.Equals(
                settings.TtsProvider,
                "kokoro",
                StringComparison.OrdinalIgnoreCase);
            await using ISpeechSynthesizer speech = useKokoro
                ? new KokoroSpeechSynthesizer(
                    settings.OutputDeviceId,
                    settings.KokoroSpeed)
                : useEdgeTts
                    ? new EdgeTtsSpeechSynthesizer(
                    settings.OutputDeviceId,
                    settings.Voice,
                    settings.VoiceRatePercent,
                    settings.VoicePitchPercent,
                    settings.EdgeTtsRate,
                    settings.EdgeTtsPitch)
                    : useSapi
                        ? new SapiSpeechSynthesizer(
                            settings.OutputDeviceId,
                            settings.VoiceRatePercent,
                            settings.VoicePitchPercent)
                        : new OneCoreSpeechSynthesizer(
                            settings.OutputDeviceId);
            var kokoroSpeech = speech as KokoroSpeechSynthesizer;
            if (kokoroSpeech is not null
                && !await kokoroSpeech.WarmUpAsync(settings.Voice))
            {
                throw new InvalidOperationException(
                    kokoroSpeech.LastError
                    ?? "Kokoro warmup failed.");
            }
            var replyLevels = 0;
            var maxReplyLevel = 0d;
            if (speech is IAudioLevelSource levelSource)
            {
                levelSource.LevelChanged += level =>
                {
                    Interlocked.Increment(ref replyLevels);
                    maxReplyLevel = Math.Max(maxReplyLevel, level);
                };
            }
            var framesAtPlaybackStart = Volatile.Read(ref frames);
            await speech.SpeakAsync(
                "正在验证回声消除和持续监听。",
                settings.Voice,
                CancellationToken.None);
            var framesAfterPlayback = Volatile.Read(ref frames);
            await Task.Delay(500);
            await capture.DisposeAsync();
            report["audio"] = new
            {
                frames,
                maxLevel,
                sampleRate = ContinuousAudioCapture.SampleRate,
                microphone = captureDevice.FriendlyName,
                speaker = renderDevice.FriendlyName,
                recoveryFrames =
                    framesAfterRecovery - framesBeforeRecovery,
                framesDuringPlayback =
                    framesAfterPlayback - framesAtPlaybackStart,
            };
            if (framesAfterPlayback <= framesAtPlaybackStart)
            {
                throw new InvalidOperationException(
                    "TTS 播放期间麦克风采集停止。");
            }

            var model = ResolveModel(
                "ggml-large-v3-turbo-q8_0.bin");
            var useSenseVoice = string.Equals(
                settings.TranscriptionProvider,
                "sensevoice",
                StringComparison.OrdinalIgnoreCase);
            await using ITranscriber transcriber = useSenseVoice
                ? new SenseVoiceTranscriber()
                : new WhisperTranscriber(
                    model,
                    model,
                    0,
                    ResolveWhisperWorker());
            await transcriber.WarmupAsync();
            var spokenCommand =
                "请只回复语音链路测试通过，不要执行任何操作。";
            var recognitionAudio = ReadMono16k(
                Synthesize(spokenCommand));
            var recognitionStarted = DateTimeOffset.UtcNow;
            var transcript = await transcriber.TranscribeAsync(
                recognitionAudio,
                16000,
                CancellationToken.None);
            report["recognition"] = new
            {
                provider = useSenseVoice
                    ? "sensevoice-small-int8"
                    : "whisper-large-v3-turbo-q8",
                transcript.Text,
                transcript.Confidence,
                elapsedMs =
                    (DateTimeOffset.UtcNow - recognitionStarted)
                    .TotalMilliseconds,
            };
            if (string.IsNullOrWhiteSpace(transcript.Text))
            {
                throw new InvalidOperationException(
                    "Whisper 自检没有返回识别文本。");
            }

            await using var codex = new CodexAppServerClient(
                new CodexAppServerOptions
                {
                    WorkingDirectory = settings.Workspace,
                    ModelProvider = settings.ModelProvider,
                    Model = settings.Model,
                    Effort = settings.Effort,
                });
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(60));
            var started = DateTimeOffset.UtcNow;
            var turn = await codex.RunTurnAsync(
                transcript.Text,
                null,
                timeout.Token);
            if (string.IsNullOrWhiteSpace(turn.Text))
            {
                throw new InvalidOperationException(
                    "Codex 自检没有返回文本。");
            }
            report["codex"] = new
            {
                turn.ThreadId,
                turn.TurnId,
                turn.Text,
                elapsedMs =
                    (DateTimeOffset.UtcNow - started).TotalMilliseconds,
            };

            await speech.SpeakAsync(
                "Codex 语音助手自检完成。",
                settings.Voice,
                CancellationToken.None);
            var edgeSpeech = speech as EdgeTtsSpeechSynthesizer;
            report["tts"] = new
            {
                status = "ok",
                provider = speech is OneCoreSpeechSynthesizer
                    ? "onecore-male"
                    : kokoroSpeech is not null
                        ? kokoroSpeech.LastUsedFallback
                            ? "onecore-fallback"
                            : $"kokoro-{settings.Voice}"
                    : edgeSpeech is null
                        ? "sapi-male"
                        : edgeSpeech.LastUsedFallback
                            ? "sapi-fallback"
                            : "edge-tts",
                error = kokoroSpeech?.LastError ?? edgeSpeech?.LastError,
                levelEvents = replyLevels,
                maxLevel = maxReplyLevel,
            };
            report["status"] = "ok";
        }
        catch (Exception exception)
        {
            exitCode = 1;
            report["status"] = "failed";
            report["error"] = exception.ToString();
        }

        var path = Path.Combine(
            Path.GetTempPath(),
            "CodexVoiceAssistant-self-test.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                }));
        return exitCode;
    }

    private static string ResolveModel(string fileName)
    {
        var localRoot = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "CodexVoiceAssistant",
            "models");
        var local = Path.Combine(localRoot, fileName);
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

    private static byte[] Synthesize(string text)
    {
        return OneCoreSpeechSynthesizer.SynthesizeWaveBytes(text);
    }

    private static float[] ReadMono16k(byte[] waveBytes)
    {
        using var stream = new MemoryStream(waveBytes);
        using var reader = new WaveFileReader(stream);
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2)
        {
            samples = new StereoToMonoSampleProvider(samples)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f,
            };
        }
        if (samples.WaveFormat.SampleRate != 16000)
        {
            samples = new WdlResamplingSampleProvider(samples, 16000);
        }

        var values = new List<float>();
        var buffer = new float[1600];
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            values.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        return values.ToArray();
    }
}
