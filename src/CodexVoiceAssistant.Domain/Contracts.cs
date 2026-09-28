namespace CodexVoiceAssistant.Domain;

public interface ICodexTurnClient
{
    Task<TurnResult> RunTurnAsync(
        long generation,
        string text,
        Action<TurnUpdate>? onUpdate,
        CancellationToken cancellationToken);

    Task InterruptAsync(CancellationToken cancellationToken = default);

    ValueTask DisposeAsync();
}

public interface ITranscriber
{
    Task WarmupAsync(CancellationToken cancellationToken = default);

    Task<Transcript> TranscribeAsync(
        ReadOnlyMemory<float> audio,
        int sampleRate,
        CancellationToken cancellationToken);

    ValueTask DisposeAsync();
}

public interface ISpeechSynthesizer
{
    Task SpeakAsync(
        string text,
        string voice,
        CancellationToken cancellationToken);

    Task StopAsync();

    ValueTask DisposeAsync();
}

public interface IAudioLevelSource
{
    event Action<double>? LevelChanged;
}

public interface IAppLogger
{
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
    void Stage(TimedStage stage);
}
