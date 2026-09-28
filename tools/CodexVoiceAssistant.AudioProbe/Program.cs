using System.Diagnostics;
using System.Speech.Synthesis;
using CodexVoiceAssistant.Infrastructure.Speech;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net.LibraryLoader;

const string defaultCommand = "贾维斯，打开网易云音乐";
RuntimeOptions.RuntimeLibraryOrder =
[
    RuntimeLibrary.Vulkan,
    RuntimeLibrary.Cpu,
];
var command = args.Length == 0 ? defaultCommand : string.Join(' ', args);
var local = Environment.GetFolderPath(
    Environment.SpecialFolder.LocalApplicationData);
var modelRoot = Path.Combine(local, "CodexVoiceAssistant", "models");
var modelName = Environment.GetEnvironmentVariable(
                    "CODEX_VOICE_MODEL")
                ?? "ggml-large-v3-turbo-q8_0.bin";
var model = ResolveModel(modelRoot, modelName);

Console.WriteLine($"COMMAND={command}");
Console.WriteLine($"MODEL={model}");

var synthesizer = Stopwatch.StartNew();
var waveBytes = Synthesize(command);
synthesizer.Stop();
var samples = ReadMono16k(waveBytes);
Console.WriteLine($"TTS_MS={synthesizer.ElapsedMilliseconds}");
Console.WriteLine($"AUDIO_SECONDS={samples.Length / 16000d:F2}");

await using var transcriber = new WhisperTranscriber(
    model,
    model,
    largeThresholdSeconds: 0);
var recognition = Stopwatch.StartNew();
var result = await transcriber.TranscribeAsync(
    samples,
    16000,
    CancellationToken.None);
recognition.Stop();
var warm = Stopwatch.StartNew();
var warmResult = await transcriber.TranscribeAsync(
    samples,
    16000,
    CancellationToken.None);
warm.Stop();

Console.WriteLine($"TEXT={result.Text}");
Console.WriteLine($"CONFIDENCE={result.Confidence:F3}");
Console.WriteLine($"ELAPSED_MS={recognition.ElapsedMilliseconds}");
Console.WriteLine($"WARM_TEXT={warmResult.Text}");
Console.WriteLine($"WARM_ELAPSED_MS={warm.ElapsedMilliseconds}");
Console.WriteLine($"RUNTIME={RuntimeOptions.LoadedLibrary}");

var expectedToken = command.Contains("网易云音乐", StringComparison.Ordinal)
    ? "网易云音乐"
    : "";
return expectedToken.Length > 0
       && !result.Text.Contains(expectedToken, StringComparison.Ordinal)
    ? 2
    : 0;

static string ResolveModel(string modelRoot, string fileName)
{
    var current = Path.Combine(modelRoot, fileName);
    if (File.Exists(current))
    {
        return current;
    }

    var legacy = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "Jarvis",
        "models",
        fileName);
    return File.Exists(legacy) ? legacy : current;
}

static byte[] Synthesize(string text)
{
    return OneCoreSpeechSynthesizer.SynthesizeWaveBytes(text);
}

static float[] ReadMono16k(byte[] waveBytes)
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
        values.AddRange(buffer.AsSpan(0, read).ToArray());
    }
    return values.ToArray();
}
