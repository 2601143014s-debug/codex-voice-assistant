using System.IO;
using System.Security;
using System.Speech.Synthesis;
using CodexVoiceAssistant.Domain;
using CodexVoiceAssistant.Infrastructure.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CodexVoiceAssistant.Infrastructure.Speech;

public sealed class SapiSpeechSynthesizer :
    ISpeechSynthesizer,
    IAudioLevelSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _outputDeviceId;
    private readonly int _ratePercent;
    private readonly int _pitchPercent;
    private WasapiOut? _activeOutput;

    public event Action<double>? LevelChanged;

    public SapiSpeechSynthesizer(
        string? outputDeviceId,
        int ratePercent = 25,
        int pitchPercent = -12)
    {
        _outputDeviceId = outputDeviceId;
        _ratePercent = ratePercent;
        _pitchPercent = pitchPercent;
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
            var waveBytes = await Task.Run(
                () => Synthesize(text, voice, cancellationToken),
                cancellationToken);
            await PlayAsync(waveBytes, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StopAsync()
    {
        try
        {
            _activeOutput?.Stop();
        }
        catch
        {
        }
        return Task.CompletedTask;
    }

    private byte[] Synthesize(
        string text,
        string voice,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var synthesizer = new SpeechSynthesizer();
        MaleVoiceSelector.SelectSapiVoice(synthesizer, voice);
        var escaped = SecurityElement.Escape(text) ?? text;
        var ssml =
            "<speak version=\"1.0\" "
            + "xmlns=\"http://www.w3.org/2001/10/synthesis\" "
            + "xml:lang=\"zh-CN\">"
            + $"<prosody pitch=\"{_pitchPercent}%\" "
            + $"rate=\"+{_ratePercent}%\">"
            + escaped
            + "</prosody></speak>";
        using var stream = new MemoryStream();
        synthesizer.SetOutputToWaveStream(stream);
        try
        {
            synthesizer.SpeakSsml(ssml);
        }
        catch (InvalidOperationException)
        {
            synthesizer.Rate = Math.Clamp(
                _ratePercent / 10,
                -10,
                10);
            synthesizer.Speak(text);
        }
        return stream.ToArray();
    }

    private async Task PlayAsync(
        byte[] waveBytes,
        CancellationToken cancellationToken)
    {
        using var device = AudioDeviceService.GetRenderDevice(_outputDeviceId);
        using var stream = new MemoryStream(waveBytes);
        using var reader = new WaveFileReader(stream);
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2)
        {
            samples = new NAudio.Wave.SampleProviders
                .StereoToMonoSampleProvider(samples);
        }
        samples = new LevelReportingSampleProvider(
            samples,
            level => LevelChanged?.Invoke(level));
        using var output = new WasapiOut(
            device,
            AudioClientShareMode.Shared,
            true,
            100);
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
