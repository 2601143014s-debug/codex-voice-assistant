using SoundFlow.Extensions.WebRtc.Apm;

namespace CodexVoiceAssistant.Infrastructure.Audio;

internal sealed class WebRtcAecProcessor : IDisposable
{
    private readonly AudioProcessingModule _module = new();
    private readonly StreamConfig _config;

    public WebRtcAecProcessor(int sampleRate, int streamDelayMs = 60)
    {
        _config = new StreamConfig(sampleRate, 1);
        using var apmConfig = new ApmConfig();
        apmConfig.SetEchoCanceller(true, mobileMode: false);
        apmConfig.SetNoiseSuppression(
            true,
            NoiseSuppressionLevel.High);
        apmConfig.SetGainController1(
            true,
            GainControlMode.AdaptiveDigital,
            targetLevelDbfs: -3,
            compressionGainDb: 18,
            enableLimiter: true);
        apmConfig.SetHighPassFilter(true);
        apmConfig.SetPipeline(
            sampleRate,
            multiChannelRender: false,
            multiChannelCapture: false,
            DownmixMethod.AverageChannels);
        _module.Initialize();
        _module.ApplyConfig(apmConfig);
        _module.SetStreamDelayMs(
            Math.Clamp(streamDelayMs, 20, 400));
    }

    public void ProcessRender(ReadOnlySpan<float> frame)
    {
        var source = new[] { frame.ToArray() };
        var destination = new[] { new float[frame.Length] };
        _module.ProcessReverseStream(
            source,
            _config,
            _config,
            destination);
    }

    public float[] ProcessCapture(ReadOnlySpan<float> frame)
    {
        var source = new[] { frame.ToArray() };
        var destination = new[] { new float[frame.Length] };
        _module.ProcessStream(
            source,
            _config,
            _config,
            destination);
        return destination[0];
    }

    public void Dispose()
    {
        _config.Dispose();
        _module.Dispose();
    }
}
