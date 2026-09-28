using System.Globalization;
using System.Text.Json;
using CodexVoiceAssistant.Infrastructure.Speech;

if (args.Length < 4
    || !string.Equals(args[0], "--worker", StringComparison.Ordinal)
    || !int.TryParse(
        args[3],
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out var thresholdSeconds))
{
    Console.Error.WriteLine(
        "Usage: CodexVoiceAssistant.WhisperWorker --worker "
        + "<small-model> <large-model> <large-threshold-seconds>");
    return 2;
}

await using var transcriber = new WhisperTranscriber(
    args[1],
    args[2],
    thresholdSeconds,
    workerExecutable: null);
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
};

while (await Console.In.ReadLineAsync() is { } line)
{
    WorkerRequest? request;
    try
    {
        request = JsonSerializer.Deserialize<WorkerRequest>(
            line,
            options);
    }
    catch (Exception exception)
    {
        await WriteResponseAsync(
            new WorkerResponse
            {
                Ok = false,
                Error = $"Invalid request: {exception.Message}",
            });
        continue;
    }

    if (request is null)
    {
        continue;
    }

    try
    {
        switch (request.Command)
        {
            case "warmup":
                await transcriber.WarmupAsync();
                await WriteResponseAsync(
                    new WorkerResponse { Ok = true });
                break;
            case "transcribe":
            {
                if (request.SampleRate <= 0
                    || string.IsNullOrWhiteSpace(request.AudioBase64))
                {
                    throw new InvalidOperationException(
                        "Transcription request is incomplete.");
                }

                var audioBytes = Convert.FromBase64String(
                    request.AudioBase64);
                var samples = new float[audioBytes.Length / sizeof(float)];
                Buffer.BlockCopy(
                    audioBytes,
                    0,
                    samples,
                    0,
                    audioBytes.Length);
                var result = await transcriber.TranscribeAsync(
                    samples,
                    request.SampleRate,
                    CancellationToken.None);
                await WriteResponseAsync(
                    new WorkerResponse
                    {
                        Ok = true,
                        Text = result.Text,
                        Confidence = (float)result.Confidence,
                    });
                break;
            }
            case "exit":
                return 0;
            default:
                throw new InvalidOperationException(
                    $"Unknown command: {request.Command}");
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        await WriteResponseAsync(
            new WorkerResponse
            {
                Ok = false,
                Error = exception.Message,
            });
        if (exception is
            System.Runtime.InteropServices.SEHException
            or AccessViolationException)
        {
            return 86;
        }
    }
}

return 0;

static Task WriteResponseAsync(WorkerResponse response)
{
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    return Console.Out.WriteLineAsync(
        JsonSerializer.Serialize(response, options));
}

internal sealed class WorkerRequest
{
    public string Command { get; init; } = "";
    public int SampleRate { get; init; }
    public string? AudioBase64 { get; init; }
}

internal sealed class WorkerResponse
{
    public bool Ok { get; init; }
    public string? Text { get; init; }
    public float? Confidence { get; init; }
    public string? Error { get; init; }
}
