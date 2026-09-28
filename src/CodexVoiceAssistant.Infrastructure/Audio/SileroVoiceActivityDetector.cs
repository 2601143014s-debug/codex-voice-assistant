using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CodexVoiceAssistant.Infrastructure.Audio;

internal sealed class SileroVoiceActivityDetector : IDisposable
{
    private const int InputSampleRate = 16000;
    private const int ChunkSamples = 512;
    private const int ContextSamples = 64;

    private readonly InferenceSession _session;
    private readonly List<float> _resampled = new();
    private readonly float[] _context = new float[ContextSamples];
    private float[] _state = new float[2 * 1 * 128];
    private readonly float _threshold;
    private bool _lastDecision;

    public SileroVoiceActivityDetector(
        string modelPath,
        float threshold = 0.32f)
    {
        _threshold = threshold;
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel =
                GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, options);
    }

    public bool IsSpeech(ReadOnlySpan<float> samples48Khz)
    {
        for (var index = 0;
             index + 2 < samples48Khz.Length;
             index += 3)
        {
            _resampled.Add(
                (samples48Khz[index]
                 + samples48Khz[index + 1]
                 + samples48Khz[index + 2])
                / 3f);
        }

        while (_resampled.Count >= ChunkSamples)
        {
            var input = new float[ContextSamples + ChunkSamples];
            _context.CopyTo(input, 0);
            for (var index = 0; index < ChunkSamples; index++)
            {
                input[ContextSamples + index] = _resampled[index];
            }
            _resampled.RemoveRange(0, ChunkSamples);

            var inputTensor = new DenseTensor<float>(
                input,
                new[] { 1, input.Length });
            var stateTensor = new DenseTensor<float>(
                _state,
                new[] { 2, 1, 128 });
            var sampleRateTensor = new DenseTensor<long>(
                new[] { (long)InputSampleRate },
                Array.Empty<int>());

            using var results = _session.Run(
                new[]
                {
                    NamedOnnxValue.CreateFromTensor(
                        "input",
                        inputTensor),
                    NamedOnnxValue.CreateFromTensor(
                        "state",
                        stateTensor),
                    NamedOnnxValue.CreateFromTensor(
                        "sr",
                        sampleRateTensor),
                });
            var probability = results
                .First(result => result.Name == "output")
                .AsTensor<float>()[0, 0];
            var nextState = results
                .First(result => result.Name == "stateN")
                .AsTensor<float>()
                .ToArray();
            if (nextState.Length == _state.Length)
            {
                _state = nextState;
            }

            Array.Copy(
                input,
                input.Length - ContextSamples,
                _context,
                0,
                ContextSamples);
            _lastDecision = probability >= _threshold;
        }

        return _lastDecision;
    }

    public void Reset()
    {
        _resampled.Clear();
        Array.Clear(_context);
        Array.Clear(_state);
        _lastDecision = false;
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
