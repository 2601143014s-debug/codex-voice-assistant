namespace CodexVoiceAssistant.Domain;

public sealed record AudioDeviceInfo(string Id, string Name);

public sealed record Transcript(
    string Text,
    double Confidence,
    DateTimeOffset CapturedAt,
    TimeSpan Duration);

public sealed record TurnUpdate(
    long Generation,
    string ThreadId,
    string TurnId,
    string Delta,
    bool IsFinal);

public sealed record TurnResult(
    long Generation,
    string ThreadId,
    string TurnId,
    string Text);

public sealed record TimedStage(
    string Name,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    bool Succeeded,
    string? Error = null);
