using NAudio.Wave;

namespace CodexVoiceAssistant.Infrastructure.Audio;

internal sealed class LevelReportingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Action<double> _levelChanged;

    public LevelReportingSampleProvider(
        ISampleProvider source,
        Action<double> levelChanged)
    {
        _source = source;
        _levelChanged = levelChanged;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read <= 0)
        {
            return read;
        }

        double sum = 0;
        for (var index = 0; index < read; index++)
        {
            var sample = buffer[offset + index];
            sum += sample * sample;
        }
        var level = Math.Sqrt(sum / read);
        _levelChanged(Math.Clamp(level * 4.5, 0, 1));
        return read;
    }
}
