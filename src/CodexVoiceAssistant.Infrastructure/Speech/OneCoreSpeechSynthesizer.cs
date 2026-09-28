using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using CodexVoiceAssistant.Domain;
using CodexVoiceAssistant.Infrastructure.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CodexVoiceAssistant.Infrastructure.Speech;

public sealed class OneCoreSpeechSynthesizer :
    ISpeechSynthesizer,
    IAudioLevelSource
{
    private const string OneCoreVoiceCategory =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices";
    private const int SsfmCreateForWrite = 3;
    private const int SvsFlagsAsync = 1;
    private const int SvsFlagsIsXml = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _outputDeviceId;
    private WasapiOut? _activeOutput;
    private int _disposed;

    public OneCoreSpeechSynthesizer(string? outputDeviceId)
    {
        _outputDeviceId = outputDeviceId;
    }

    public event Action<double>? LevelChanged;

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
                () => SynthesizeWaveBytes(
                    text,
                    cancellationToken),
                cancellationToken);
            await PlayAsync(waveBytes, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PlayAsync(
        byte[] waveBytes,
        CancellationToken cancellationToken)
    {
        using var device = AudioDeviceService.GetRenderDevice(
            _outputDeviceId);
        using var stream = new MemoryStream(waveBytes);
        using var reader = new WaveFileReader(stream);
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

    public static byte[] SynthesizeWaveBytes(
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(
            Path.GetTempPath(),
            $"CodexVoiceAssistant-{Guid.NewGuid():N}.wav");
        object? voiceObject = null;
        object? categoryObject = null;
        object? streamObject = null;
        try
        {
            dynamic voice = CreateComObject("SAPI.SpVoice");
            voiceObject = voice;
            dynamic category = CreateComObject(
                "SAPI.SpObjectTokenCategory");
            categoryObject = category;
            category.SetId(OneCoreVoiceCategory);
            dynamic tokens = category.EnumerateTokens();
            dynamic? selectedToken = null;
            for (var index = 0; index < tokens.Count; index++)
            {
                dynamic token = tokens.Item(index);
                var description =
                    (string?)token.GetDescription() ?? "";
                if (description.Contains(
                        "Kangkang",
                        StringComparison.OrdinalIgnoreCase))
                {
                    selectedToken = token;
                    break;
                }
            }
            if (selectedToken is null)
            {
                throw new InvalidOperationException(
                    "没有找到 OneCore Kangkang 男声。");
            }

            voice.Voice = selectedToken;
            voice.Rate = 2;
            dynamic stream = CreateComObject("SAPI.SpFileStream");
            streamObject = stream;
            stream.Open(path, SsfmCreateForWrite, false);
            voice.AudioOutputStream = stream;
            var escaped = SecurityElement.Escape(text) ?? text;
            var ssml =
                "<speak version=\"1.0\" "
                + "xmlns=\"http://www.w3.org/2001/10/synthesis\" "
                + "xml:lang=\"zh-CN\">"
                + $"<prosody pitch=\"-12%\" rate=\"+35%\">"
                + escaped
                + "</prosody></speak>";
            try
            {
                voice.Speak(ssml, SvsFlagsIsXml);
            }
            catch (COMException)
            {
                voice.Speak(text, 0);
            }
            voice.WaitUntilDone(60000);
            voice.AudioOutputStream = null;
            stream.Close();
            streamObject = null;
            return File.ReadAllBytes(path);
        }
        finally
        {
            ReleaseComObject(streamObject);
            ReleaseComObject(categoryObject);
            ReleaseComObject(voiceObject);
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await StopAsync();
        _gate.Dispose();
    }

    private static object CreateComObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId);
        return type is null
            ? throw new InvalidOperationException(
                $"COM component not found: {progId}")
            : Activator.CreateInstance(type)
              ?? throw new InvalidOperationException(
                  $"Cannot create COM component: {progId}");
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null)
        {
            return;
        }
        try
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
        catch
        {
        }
    }
}
