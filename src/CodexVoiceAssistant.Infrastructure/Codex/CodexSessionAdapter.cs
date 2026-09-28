using CodexVoiceAssistant.Domain;
using CodexVoiceAssistant.Protocol;

namespace CodexVoiceAssistant.Infrastructure.Codex;

public sealed class CodexSessionAdapter : ICodexTurnClient
{
    private readonly CodexAppServerClient _client;

    public CodexSessionAdapter(CodexAppServerOptions options)
    {
        _client = new CodexAppServerClient(options);
    }

    public async Task<TurnResult> RunTurnAsync(
        long generation,
        string text,
        Action<TurnUpdate>? onUpdate,
        CancellationToken cancellationToken)
    {
        var result = await _client.RunTurnAsync(
            text,
            update => onUpdate?.Invoke(
                new TurnUpdate(
                    generation,
                    update.ThreadId,
                    update.TurnId,
                    update.Delta,
                    update.IsFinal)),
            cancellationToken);
        return new TurnResult(
            generation,
            result.ThreadId,
            result.TurnId,
            result.Text);
    }

    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        _client.InterruptAsync(cancellationToken);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
