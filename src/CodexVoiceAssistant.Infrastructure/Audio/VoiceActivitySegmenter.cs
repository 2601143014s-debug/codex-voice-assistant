namespace CodexVoiceAssistant.Infrastructure.Audio;

public sealed class VoiceActivitySegmenter : IDisposable
{
    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float> _utterance = new();
    private readonly double _threshold;
    private readonly int _silenceFrames;
    private readonly int _maxFrames;
    private readonly SileroVoiceActivityDetector? _silero;
    private readonly object _sync = new();
    private bool _calibrated;
    private int _calibrationFrames;
    private double _noiseFloor = 0.002;
    private int _speechFrames;
    private int _silentFrames;
    private int _preRollSamples;

    public VoiceActivitySegmenter(
        double threshold = 0.012,
        int silenceMilliseconds = 550,
        int maxUtteranceSeconds = 20,
        int sampleRate = ContinuousAudioCapture.SampleRate,
        string? sileroModelPath = null)
    {
        _threshold = threshold;
        _silenceFrames = silenceMilliseconds
                         * sampleRate
                         / 1000
                         / ContinuousAudioCapture.FrameSamples;
        _maxFrames = maxUtteranceSeconds
                     * sampleRate
                     / ContinuousAudioCapture.FrameSamples;
        if (!string.IsNullOrWhiteSpace(sileroModelPath)
            && File.Exists(sileroModelPath))
        {
            _silero = new SileroVoiceActivityDetector(sileroModelPath);
        }
    }

    public event Action? SpeechStarted;
    public event Action<double>? LevelChanged;
    public event Action<float[], TimeSpan>? UtteranceReady;

    public void Process(AudioFrame frame)
    {
        lock (_sync)
        {
            LevelChanged?.Invoke(frame.Level);
            if (!_calibrated)
            {
                _noiseFloor = ((_noiseFloor * _calibrationFrames)
                               + frame.Level)
                              / (_calibrationFrames + 1);
                _calibrationFrames++;
                if (_calibrationFrames >= 45)
                {
                    _calibrated = true;
                }
                return;
            }

            var activeThreshold = Math.Max(
                _threshold,
                Math.Min(0.025, _noiseFloor * 2.0));
            var modelDetected = _silero?.IsSpeech(frame.Samples) ?? false;
            var energyDetected = frame.Level >= Math.Max(
                _threshold,
                Math.Min(0.02, _noiseFloor * 2.0));
            var voiceDetected = modelDetected || energyDetected;
            var audibleSpeech = frame.Level >= Math.Max(
                0.0005,
                Math.Min(0.008, _noiseFloor * 1.4));
            var isSpeech = voiceDetected && audibleSpeech;

            if (_utterance.Count == 0)
            {
                AddPreRoll(frame.Samples);
                _speechFrames = isSpeech ? _speechFrames + 1 : 0;
                if (_speechFrames < 2)
                {
                    return;
                }

                foreach (var preRoll in _preRoll)
                {
                    _utterance.AddRange(preRoll);
                }
                _preRoll.Clear();
                _preRollSamples = 0;
                _silentFrames = 0;
                SpeechStarted?.Invoke();
                return;
            }

            _utterance.AddRange(frame.Samples);
            _silentFrames = isSpeech ? 0 : _silentFrames + 1;
            var frameCount = _utterance.Count
                             / ContinuousAudioCapture.FrameSamples;
            if (_silentFrames >= _silenceFrames || frameCount >= _maxFrames)
            {
                Complete();
            }
        }
    }

    private void AddPreRoll(float[] samples)
    {
        _preRoll.Enqueue(samples);
        _preRollSamples += samples.Length;
        var maximum = ContinuousAudioCapture.SampleRate * 350 / 1000;
        while (_preRollSamples > maximum && _preRoll.Count > 0)
        {
            _preRollSamples -= _preRoll.Dequeue().Length;
        }
    }

    private void Complete()
    {
        var samples = _utterance.ToArray();
        _utterance.Clear();
        _speechFrames = 0;
        _silentFrames = 0;
        var duration = TimeSpan.FromSeconds(
            samples.Length / (double)ContinuousAudioCapture.SampleRate);
        UtteranceReady?.Invoke(samples, duration);
    }

    public void Dispose()
    {
        _silero?.Dispose();
    }
}
