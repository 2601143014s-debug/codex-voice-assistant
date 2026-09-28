using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CodexVoiceAssistant.Infrastructure.Audio;

public sealed record AudioFrame(
    float[] Samples,
    long Timestamp,
    double Level);

public sealed class ContinuousAudioCapture : IAsyncDisposable
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 480;

    private readonly ConcurrentQueue<ReferenceFrame> _referenceFrames = new();
    private readonly List<float> _microphoneCarry = new();
    private readonly object _sampleSync = new();
    private readonly object _lifecycleSync = new();
    private WasapiCapture? _capture;
    private WasapiLoopbackCapture? _loopback;
    private MMDevice? _captureDevice;
    private MMDevice? _renderDevice;
    private WebRtcAecProcessor? _apm;
    private CancellationTokenSource? _recoveryCancellation;
    private Task? _recoveryTask;
    private ReferenceFrame? _lastReference;
    private string? _captureDeviceId;
    private string? _outputDeviceId;
    private long _microphoneOrigin;
    private long _microphoneTotalSamples;
    private long _referenceToleranceTicks;
    private int _recoveryState;
    private bool _running;
    private bool _stopping;

    public event Action<AudioFrame>? FrameAvailable;
    public event Action<Exception>? Faulted;
    public event Action? Recovered;

    public void SimulateDeviceFailureForDiagnostics()
    {
        _capture?.StopRecording();
    }

    public void Start(string? captureDeviceId, string? outputDeviceId)
    {
        lock (_lifecycleSync)
        {
            if (_running)
            {
                return;
            }

            _captureDeviceId = captureDeviceId;
            _outputDeviceId = outputDeviceId;
            _stopping = false;
            _running = true;
            _recoveryCancellation?.Dispose();
            _recoveryCancellation = new CancellationTokenSource();
            try
            {
                OpenAndStartDevices();
            }
            catch
            {
                _running = false;
                _stopping = true;
                StopDevices();
                throw;
            }
        }
    }

    private void OpenAndStartDevices()
    {
        _captureDevice = AudioDeviceService.GetCaptureDevice(
            _captureDeviceId);
        _renderDevice = AudioDeviceService.GetRenderDevice(
            _outputDeviceId);
        _capture = new WasapiCapture(
            _captureDevice,
            useEventSync: true,
            audioBufferMillisecondsLength: 20);
        _loopback = new WasapiLoopbackCapture(_renderDevice);
        if (_capture.WaveFormat.SampleRate != SampleRate
            || _loopback.WaveFormat.SampleRate != SampleRate)
        {
            throw new InvalidOperationException(
                "AEC 需要 48 kHz 麦克风和扬声器。");
        }

        var aecDelayMs = GetAecDelayMilliseconds(_renderDevice);
        _referenceToleranceTicks =
            Stopwatch.Frequency * (aecDelayMs + 120L) / 1000;
        _apm = new WebRtcAecProcessor(SampleRate, aecDelayMs);
        _capture.DataAvailable += OnMicrophoneData;
        _loopback.DataAvailable += OnReferenceData;
        _capture.RecordingStopped += OnStopped;
        _loopback.RecordingStopped += OnStopped;
        lock (_sampleSync)
        {
            _microphoneCarry.Clear();
            _microphoneOrigin = 0;
            _microphoneTotalSamples = 0;
            _lastReference = null;
            while (_referenceFrames.TryDequeue(out _))
            {
            }
        }

        _capture.StartRecording();
        _loopback.StartRecording();
    }

    private void OnReferenceData(object? sender, WaveInEventArgs eventArgs)
    {
        if (!_running || _loopback is null)
        {
            return;
        }

        var samples = ToMono(
            eventArgs.Buffer,
            eventArgs.BytesRecorded,
            _loopback.WaveFormat);
        var ticksPerSample =
            Stopwatch.Frequency / (double)SampleRate;
        var endTimestamp = Stopwatch.GetTimestamp();
        var firstTimestamp = endTimestamp
                             - (long)(samples.Length * ticksPerSample);
        for (var offset = 0;
             offset + FrameSamples <= samples.Length;
             offset += FrameSamples)
        {
            var centerTimestamp = firstTimestamp
                                  + (long)((offset
                                            + FrameSamples / 2d)
                                           * ticksPerSample);
            _referenceFrames.Enqueue(
                new ReferenceFrame(
                    centerTimestamp,
                    samples
                        .AsSpan(offset, FrameSamples)
                        .ToArray()));
        }

        while (_referenceFrames.Count > 50)
        {
            _referenceFrames.TryDequeue(out _);
        }
    }

    private void OnMicrophoneData(object? sender, WaveInEventArgs eventArgs)
    {
        if (!_running || _capture is null || _apm is null)
        {
            return;
        }

        var samples = ToMono(
            eventArgs.Buffer,
            eventArgs.BytesRecorded,
            _capture.WaveFormat);
        var ticksPerSample =
            Stopwatch.Frequency / (double)SampleRate;
        var endTimestamp = Stopwatch.GetTimestamp();
        var measuredStart = endTimestamp
                            - (long)(samples.Length * ticksPerSample);

        lock (_sampleSync)
        {
            if (_microphoneCarry.Count == 0)
            {
                _microphoneOrigin = measuredStart;
                _microphoneTotalSamples = 0;
            }
            else
            {
                var expectedStart = _microphoneOrigin
                                    + (long)(_microphoneTotalSamples
                                             * ticksPerSample);
                var drift = Math.Abs(expectedStart - measuredStart);
                if (drift > Stopwatch.Frequency / 2)
                {
                    _microphoneCarry.Clear();
                    _microphoneOrigin = measuredStart;
                    _microphoneTotalSamples = 0;
                }
            }

            _microphoneCarry.AddRange(samples);
            _microphoneTotalSamples += samples.Length;
            while (_microphoneCarry.Count >= FrameSamples)
            {
                var raw = _microphoneCarry
                    .Take(FrameSamples)
                    .ToArray();
                _microphoneCarry.RemoveRange(0, FrameSamples);
                var frameStart = _microphoneTotalSamples
                                 - _microphoneCarry.Count;
                var timestamp = _microphoneOrigin
                                + (long)((frameStart
                                          + FrameSamples / 2d)
                                         * ticksPerSample);
                var reference = TakeNearestReference(timestamp)?.Samples
                                ?? new float[FrameSamples];
                _apm.ProcessRender(reference);
                var processed = _apm.ProcessCapture(raw);
                var referenceLevel = CalculateLevel(reference);
                var analyzed = referenceLevel > 0.003
                    ? processed
                    : raw;
                FrameAvailable?.Invoke(
                    new AudioFrame(
                        analyzed,
                        timestamp,
                        CalculateLevel(analyzed)));
            }
        }
    }

    private ReferenceFrame? TakeNearestReference(long timestamp)
    {
        var frames = new List<ReferenceFrame>();
        while (_referenceFrames.TryDequeue(out var frame))
        {
            frames.Add(frame);
        }

        if (frames.Count == 0)
        {
            return _lastReference;
        }

        var cutoff = timestamp - _referenceToleranceTicks;
        var eligible = frames
            .Where(frame => frame.Timestamp >= cutoff)
            .ToArray();
        IReadOnlyList<ReferenceFrame> pool =
            eligible.Length == 0 ? frames : eligible;
        var nearest = pool
            .OrderBy(frame => Math.Abs(frame.Timestamp - timestamp))
            .First();
        _lastReference = nearest;

        foreach (var frame in frames)
        {
            if (frame.Timestamp > nearest.Timestamp)
            {
                _referenceFrames.Enqueue(frame);
            }
        }
        return nearest;
    }

    private static float[] ToMono(
        byte[] buffer,
        int bytesRecorded,
        WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        if (format.Encoding == WaveFormatEncoding.IeeeFloat
            && format.BitsPerSample == 32)
        {
            var samples = bytesRecorded / sizeof(float);
            var mono = new float[samples / channels];
            for (var i = 0; i < mono.Length; i++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += BitConverter.ToSingle(
                        buffer,
                        (i * channels + channel) * sizeof(float));
                }
                mono[i] = sum / channels;
            }
            return mono;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm
            && format.BitsPerSample == 16)
        {
            var samples = bytesRecorded / sizeof(short);
            var mono = new float[samples / channels];
            for (var i = 0; i < mono.Length; i++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += BitConverter.ToInt16(
                               buffer,
                               (i * channels + channel) * sizeof(short))
                           / 32768f;
                }
                mono[i] = sum / channels;
            }
            return mono;
        }

        throw new NotSupportedException($"Unsupported format: {format}");
    }

    private static double CalculateLevel(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }
        return Math.Sqrt(sum / samples.Length);
    }

    private void OnStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (!_running
            || _stopping
            || Interlocked.CompareExchange(
                ref _recoveryState,
                1,
                0) != 0)
        {
            return;
        }

        var error = eventArgs.Exception
                    ?? new InvalidOperationException(
                        "音频设备已停止，正在自动恢复。");
        Faulted?.Invoke(error);
        _recoveryTask = Task.Run(RecoverAsync);
    }

    private async Task RecoverAsync()
    {
        var delay = TimeSpan.FromMilliseconds(250);
        var token = _recoveryCancellation?.Token ?? CancellationToken.None;
        try
        {
            for (var attempt = 1; !token.IsCancellationRequested; attempt++)
            {
                try
                {
                    await Task.Delay(delay, token);
                    StopDevices();
                    lock (_lifecycleSync)
                    {
                        if (!_stopping && _running)
                        {
                            OpenAndStartDevices();
                        }
                    }
                    Recovered?.Invoke();
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    StopDevices();
                    if (attempt <= 3 || attempt % 10 == 0)
                    {
                        Faulted?.Invoke(
                            new InvalidOperationException(
                                $"音频设备恢复失败，第 {attempt} 次重试。",
                                exception));
                    }
                    delay = TimeSpan.FromMilliseconds(
                        Math.Min(3000, delay.TotalMilliseconds * 1.8));
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _recoveryState, 0);
        }
    }

    private static int GetAecDelayMilliseconds(MMDevice renderDevice)
    {
        try
        {
            var name = renderDevice.FriendlyName;
            if (name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                || name.Contains("蓝牙", StringComparison.OrdinalIgnoreCase)
                || name.Contains(
                    "Hands-Free",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 180;
            }
            if (name.Contains("HDMI", StringComparison.OrdinalIgnoreCase)
                || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }
            if (name.Contains("USB", StringComparison.OrdinalIgnoreCase))
            {
                return 80;
            }
            return 60;
        }
        catch
        {
            return 60;
        }
    }

    private void StopDevices()
    {
        WasapiCapture? capture;
        WasapiLoopbackCapture? loopback;
        MMDevice? captureDevice;
        MMDevice? renderDevice;
        WebRtcAecProcessor? apm;
        lock (_lifecycleSync)
        {
            capture = _capture;
            loopback = _loopback;
            captureDevice = _captureDevice;
            renderDevice = _renderDevice;
            apm = _apm;
            _capture = null;
            _loopback = null;
            _captureDevice = null;
            _renderDevice = null;
            _apm = null;
        }

        try
        {
            capture?.StopRecording();
            loopback?.StopRecording();
        }
        catch
        {
        }

        if (capture is not null)
        {
            capture.DataAvailable -= OnMicrophoneData;
            capture.RecordingStopped -= OnStopped;
            capture.Dispose();
        }
        if (loopback is not null)
        {
            loopback.DataAvailable -= OnReferenceData;
            loopback.RecordingStopped -= OnStopped;
            loopback.Dispose();
        }
        captureDevice?.Dispose();
        renderDevice?.Dispose();
        apm?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _running = false;
        _stopping = true;
        _recoveryCancellation?.Cancel();
        if (_recoveryTask is not null)
        {
            try
            {
                await _recoveryTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }
        StopDevices();
        _recoveryCancellation?.Dispose();
        _recoveryCancellation = null;
    }

    private sealed record ReferenceFrame(
        long Timestamp,
        float[] Samples);
}
