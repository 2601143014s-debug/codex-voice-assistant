using System.Diagnostics;
using System.IO;
using CodexVoiceAssistant.Domain;
using CodexVoiceAssistant.Infrastructure.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CodexVoiceAssistant.Infrastructure.Speech;

public sealed class EdgeTtsSpeechSynthesizer :
    ISpeechSynthesizer,
    IAudioLevelSource
{
    private static readonly TimeSpan SynthesisTimeout =
        TimeSpan.FromSeconds(12);

    private readonly string? _outputDeviceId;
    private readonly string _voice;
    private readonly string _rate;
    private readonly string _pitch;
    private readonly SapiSpeechSynthesizer _fallback;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _activeProcess;
    private WasapiOut? _activeOutput;
    private int _disposed;

    public bool LastUsedFallback { get; private set; }
    public string? LastError { get; private set; }
    public event Action<double>? LevelChanged;

    public EdgeTtsSpeechSynthesizer(
        string? outputDeviceId,
        string voice,
        int fallbackRatePercent = 25,
        int fallbackPitchPercent = -12,
        string edgeRate = "+35%",
        string edgePitch = "-10Hz")
    {
        _outputDeviceId = outputDeviceId;
        _voice = MaleVoiceSelector.SelectEdgeVoice(voice);
        _rate = edgeRate;
        _pitch = edgePitch;
        _fallback = new SapiSpeechSynthesizer(
            outputDeviceId,
            fallbackRatePercent,
            fallbackPitchPercent);
        _fallback.LevelChanged += level => LevelChanged?.Invoke(level);
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
            var mediaPath = await TrySynthesizeEdgeAsync(
                text,
                cancellationToken);
            if (mediaPath is null)
            {
                LastUsedFallback = true;
                await _fallback.SpeakAsync(
                    text,
                    "Microsoft Kangkang",
                    cancellationToken);
                return;
            }

            LastUsedFallback = false;
            try
            {
                await PlayMediaAsync(
                    mediaPath,
                    cancellationToken);
            }
            finally
            {
                TryDelete(mediaPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> TrySynthesizeEdgeAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "CodexVoiceAssistant",
            "tts");
        Directory.CreateDirectory(directory);
        var mediaPath = Path.Combine(
            directory,
            $"{Guid.NewGuid():N}.mp3");

        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("edge_tts");
        startInfo.ArgumentList.Add($"--voice={_voice}");
        startInfo.ArgumentList.Add($"--rate={_rate}");
        startInfo.ArgumentList.Add($"--pitch={_pitch}");
        startInfo.ArgumentList.Add("--text");
        startInfo.ArgumentList.Add(text);
        startInfo.ArgumentList.Add("--write-media");
        startInfo.ArgumentList.Add(mediaPath);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                LastError = "Unable to start python.";
                return null;
            }
            _activeProcess = process;
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            timeout.CancelAfter(SynthesisTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                LastError = "Edge TTS synthesis timed out.";
                return null;
            }

            if (process.ExitCode != 0
                || !File.Exists(mediaPath)
                || new FileInfo(mediaPath).Length < 512)
            {
                LastError =
                    $"Edge TTS exited with code {process.ExitCode}: "
                    + await process.StandardError.ReadToEndAsync();
                TryDelete(mediaPath);
                return null;
            }

            LastError = null;
            return mediaPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = exception.ToString();
            TryDelete(mediaPath);
            return null;
        }
        finally
        {
            _activeProcess = null;
        }
    }

    private async Task PlayMediaAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var device = AudioDeviceService.GetRenderDevice(
            _outputDeviceId);
        using var reader = new MediaFoundationReader(path);
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

    public async Task StopAsync()
    {
        try
        {
            _activeOutput?.Stop();
        }
        catch
        {
        }
        TryKill(_activeProcess);
        await _fallback.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await StopAsync();
        await _fallback.DisposeAsync();
        _gate.Dispose();
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
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
