using System.Speech.Synthesis;
using CodexVoiceAssistant.Infrastructure.Speech;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CodexVoiceAssistant.Tests;

public sealed class WhisperIntegrationTests
{
    [Fact]
    public async Task RecognizesGeneratedChineseCommand()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "CODEX_VOICE_LIVE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var small = Path.Combine(
            local,
            "Jarvis",
            "models",
            "ggml-small-q5_1.bin");
        var large = Path.Combine(
            local,
            "Jarvis",
            "models",
            "ggml-large-v3-turbo-q5_0.bin");
        Assert.True(File.Exists(small), small);

        var waveBytes = Synthesize("贾维斯，打开网易云音乐");
        var samples = ReadMono16k(waveBytes);
        await using var transcriber = new WhisperTranscriber(
            small,
            large,
            largeThresholdSeconds: 4);
        var result = await transcriber.TranscribeAsync(
            samples,
            16000,
            CancellationToken.None);

        Assert.Contains("网易云音乐", result.Text);
    }

    [Fact]
    public async Task CancellationDisposesProcessorCleanly()
    {
        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var small = Path.Combine(
            local,
            "Jarvis",
            "models",
            "ggml-small-q5_1.bin");
        if (!File.Exists(small))
        {
            return;
        }

        await using var transcriber = new WhisperTranscriber(
            small,
            small,
            largeThresholdSeconds: 4);
        using var cancellation =
            new CancellationTokenSource(TimeSpan.FromMilliseconds(75));
        var audio = Enumerable
            .Repeat(0.02f, 48000 * 30)
            .ToArray();

        var exception = await Record.ExceptionAsync(
            () => transcriber.TranscribeAsync(
                audio,
                48000,
                cancellation.Token));

        Assert.True(
            exception is null
            or OperationCanceledException,
            exception?.ToString());
    }

    private static byte[] Synthesize(string text)
    {
        using var synthesizer = new SpeechSynthesizer();
        MaleVoiceSelector.SelectSapiVoice(
            synthesizer,
            MaleVoiceSelector.DefaultSapiVoice);
        using var stream = new MemoryStream();
        synthesizer.SetOutputToWaveStream(stream);
        synthesizer.Speak(text);
        return stream.ToArray();
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
            values.AddRange(buffer.Take(read));
        }
        return values.ToArray();
    }
}
