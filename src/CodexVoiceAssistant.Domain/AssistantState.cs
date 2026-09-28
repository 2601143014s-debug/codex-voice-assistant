namespace CodexVoiceAssistant.Domain;

public enum AssistantState
{
    Stopped,
    Starting,
    Calibrating,
    Listening,
    Capturing,
    Transcribing,
    Thinking,
    Speaking,
    Interrupting,
    Recovering,
    Faulted,
}

public enum TurnPhase
{
    Idle,
    Submitting,
    Streaming,
    Speaking,
    Completed,
    Interrupted,
    Failed,
}
