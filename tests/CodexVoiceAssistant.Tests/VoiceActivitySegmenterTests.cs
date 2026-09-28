using System.Speech.Synthesis;
using CodexVoiceAssistant.Infrastructure.Audio;
using CodexVoiceAssistant.Infrastructure.Speech;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CodexVoiceAssistant.Tests;

public sealed class VoiceActivitySegmenterTests
{
    [Fact]
    public void EmitsUtteranceAfterSilence()
    {
        var segmenter = new VoiceActivitySegmenter(
            threshold: 0.02,
            silenceMilliseconds: 100,
            maxUtteranceSeconds: 5);
        float[]? utterance = null;
        segmenter.UtteranceReady += (samples, _) => utterance = samples;

        for (var i = 0; i < 50; i++)
        {
            segmenter.Process(Frame(0.001));
        }
        for (var i = 0; i < 8; i++)
        {
            segmenter.Process(Frame(0.08));
        }
        for (var i = 0; i < 12; i++)
        {
            segmenter.Process(Frame(0.001));
        }

        Assert.NotNull(utterance);
        Assert.True(utterance!.Length >= ContinuousAudioCapture.FrameSamples * 8);
    }

    [Fact]
    public void SilenceDoesNotEndListener()
    {
        var segmenter = new VoiceActivitySegmenter();
        var utterances = 0;
        segmenter.UtteranceReady += (_, _) => utterances++;

        for (var i = 0; i < 500; i++)
        {
            segmenter.Process(Frame(0.0005));
        }

        Assert.Equal(0, utterances);
    }

    [Fact]
    public void SileroRejectsNoiseAndAcceptsSpeech()
    {
        var modelPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "CodexVoiceAssistant",
            "models",
            "silero_vad.onnx");
        if (!File.Exists(modelPath))
        {
            return;
        }
        if (!MaleVoiceSelector.HasInstalledSapiMaleVoice())
        {
            return;
        }

        using (var noiseSegmenter = new VoiceActivitySegmenter(
                   threshold: 0.012,
                   silenceMilliseconds: 100,
                   maxUtteranceSeconds: 5,
                   sileroModelPath: modelPath))
        {
            var noiseUtterances = 0;
            noiseSegmenter.UtteranceReady += (_, _) =>
                noiseUtterances++;
            var random = new Random(12345);
            for (var index = 0; index < 150; index++)
            {
                var samples = Enumerable
                    .Range(0, ContinuousAudioCapture.FrameSamples)
                    .Select(_ => (float)((random.NextDouble() * 2) - 1)
                                 * 0.03f)
                    .ToArray();
                noiseSegmenter.Process(
                    new AudioFrame(
                        samples,
                        0,
                        CalculateLevel(samples)));
            }

            Assert.Equal(0, noiseUtterances);
        }

        using var speechSegmenter = new VoiceActivitySegmenter(
            threshold: 0.012,
            silenceMilliseconds: 100,
            maxUtteranceSeconds: 5,
            sileroModelPath: modelPath);
        float[]? speech = null;
        speechSegmenter.UtteranceReady += (samples, _) =>
            speech = samples;
        foreach (var frame in CreateSpeechFrames())
        {
            speechSegmenter.Process(frame);
        }
        for (var index = 0; index < 20; index++)
        {
            speechSegmenter.Process(Frame(0.0002));
        }

        Assert.NotNull(speech);
    }

    private static IEnumerable<AudioFrame> CreateSpeechFrames()
    {
        using var synthesizer = new SpeechSynthesizer();
        MaleVoiceSelector.SelectSapiVoice(
            synthesizer,
            MaleVoiceSelector.DefaultSapiVoice);
        using var output = new MemoryStream();
        synthesizer.SetOutputToWaveStream(output);
        synthesizer.Speak("你好，打开网易云音乐");
        output.Position = 0;
        using var reader = new WaveFileReader(output);
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.Channels == 2)
        {
            provider = new StereoToMonoSampleProvider(provider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f,
            };
        }
        if (provider.WaveFormat.SampleRate
            != ContinuousAudioCapture.SampleRate)
        {
            provider = new WdlResamplingSampleProvider(
                provider,
                ContinuousAudioCapture.SampleRate);
        }

        var samples = new List<float>();
        var buffer = new float[ContinuousAudioCapture.FrameSamples];
        int read;
        while ((read = provider.Read(
                   buffer,
                   0,
                   buffer.Length)) > 0)
        {
            if (read < buffer.Length)
            {
                Array.Clear(buffer, read, buffer.Length - read);
            }
            yield return new AudioFrame(
                buffer.ToArray(),
                0,
                CalculateLevel(buffer));
        }
    }

    private static double CalculateLevel(float[] samples) =>
        Math.Sqrt(
            samples.Length == 0
                ? 0
                : samples.Sum(sample => sample * sample)
                  / samples.Length);

    private static AudioFrame Frame(double amplitude)
    {
        var samples = Enumerable
            .Repeat((float)amplitude, ContinuousAudioCapture.FrameSamples)
            .ToArray();
        return new AudioFrame(samples, 0, amplitude);
    }
}
