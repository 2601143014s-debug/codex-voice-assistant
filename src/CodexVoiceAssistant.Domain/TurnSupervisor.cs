using System.Threading.Channels;

namespace CodexVoiceAssistant.Domain;

public sealed class TurnSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan TtsCompletionTimeout =
        TimeSpan.FromMinutes(2);

    private readonly ICodexTurnClient _codex;
    private readonly ISpeechSynthesizer _speech;
    private readonly IAppLogger _logger;
    private readonly string _voice;
    private readonly Channel<Command> _commands;
    private readonly Channel<TtsItem> _ttsQueue;
    private readonly object _turnSync = new();
    private readonly object _stateSync = new();
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _currentTurn;
    private Task? _commandWorker;
    private Task? _ttsWorker;
    private long _generation;
    private AssistantState _state = AssistantState.Stopped;
    private int _disposed;

    public TurnSupervisor(
        ICodexTurnClient codex,
        ISpeechSynthesizer speech,
        IAppLogger logger,
        string voice)
    {
        _codex = codex;
        _speech = speech;
        _logger = logger;
        _voice = voice;
        _commands = Channel.CreateBounded<Command>(
            new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        _ttsQueue = Channel.CreateUnbounded<TtsItem>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
    }

    public event Action<AssistantState>? StateChanged;
    public event Action<Transcript>? TranscriptAccepted;
    public event Action<TurnUpdate>? UpdateReceived;
    public event Action<Exception>? Error;

    public AssistantState State
    {
        get
        {
            lock (_stateSync)
            {
                return _state;
            }
        }
    }

    public long CurrentGeneration => Interlocked.Read(ref _generation);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateSync)
        {
            if (_commandWorker is not null)
            {
                return Task.CompletedTask;
            }

            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            _commandWorker = Task.Run(
                ProcessCommandsAsync,
                CancellationToken.None);
            _ttsWorker = Task.Run(
                ProcessTtsAsync,
                CancellationToken.None);
        }
        SetState(AssistantState.Listening);
        return Task.CompletedTask;
    }

    public Task SubmitAsync(
        Transcript transcript,
        CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _generation);
        CancelCurrentTurn();
        TranscriptAccepted?.Invoke(transcript);
        if (!_commands.Writer.TryWrite(
                new Command(generation, transcript)))
        {
            Error?.Invoke(
                new InvalidOperationException(
                    "语音指令队列已关闭。"));
        }
        return Task.CompletedTask;
    }

    public async Task InterruptAsync(
        CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _generation);
        CancelCurrentTurn();
        SetState(AssistantState.Interrupting);
        try
        {
            await _speech.StopAsync();
            await _codex.InterruptAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            _logger.Error("Interrupt failed.", exception);
            Error?.Invoke(exception);
        }

        if (CurrentGeneration == generation)
        {
            SetState(AssistantState.Listening);
        }
    }

    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(
                               _lifetime.Token))
            {
                using var turnCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        _lifetime.Token);
                SetCurrentTurn(turnCancellation);
                try
                {
                    await ProcessTurnAsync(
                        command.Generation,
                        command.Transcript,
                        turnCancellation.Token);
                }
                finally
                {
                    ClearCurrentTurn(turnCancellation);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("Turn supervisor stopped.", exception);
            Error?.Invoke(exception);
            SetState(AssistantState.Faulted);
        }
    }

    private async Task ProcessTurnAsync(
        long generation,
        Transcript transcript,
        CancellationToken cancellationToken)
    {
        var stageStarted = DateTimeOffset.UtcNow;
        var chunker = new SentenceChunker();
        var ttsFinished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var speechCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        try
        {
            if (generation == CurrentGeneration)
            {
                SetState(AssistantState.Thinking);
            }
            var result = await _codex.RunTurnAsync(
                generation,
                transcript.Text,
                update =>
                {
                    if (update.Generation != generation
                        || generation != CurrentGeneration)
                    {
                        return;
                    }

                    UpdateReceived?.Invoke(update);
                    foreach (var sentence in chunker.Append(update.Delta))
                    {
                        _ttsQueue.Writer.TryWrite(
                            new TtsItem(
                                generation,
                                sentence,
                                null,
                                speechCancellation.Token));
                    }
                },
                cancellationToken);

            var final = chunker.Flush();
            if (!string.IsNullOrWhiteSpace(final))
            {
                _ttsQueue.Writer.TryWrite(
                    new TtsItem(
                        generation,
                        final,
                        null,
                        speechCancellation.Token));
            }
            _ttsQueue.Writer.TryWrite(
                new TtsItem(
                    generation,
                    "",
                    ttsFinished,
                    speechCancellation.Token));
            await ttsFinished.Task.WaitAsync(
                TtsCompletionTimeout,
                cancellationToken);

            _logger.Stage(
                new TimedStage(
                    "turn",
                    stageStarted,
                    DateTimeOffset.UtcNow - stageStarted,
                    Succeeded: true));
            _logger.Info(
                $"Turn completed: thread={result.ThreadId} turn={result.TurnId}");
        }
        catch (OperationCanceledException)
        {
            _logger.Stage(
                new TimedStage(
                    "turn",
                    stageStarted,
                    DateTimeOffset.UtcNow - stageStarted,
                    Succeeded: false,
                    Error: "interrupted"));
            ttsFinished.TrySetCanceled(cancellationToken);
        }
        catch (TimeoutException exception)
        {
            _logger.Error("TTS completion timed out.", exception);
            Error?.Invoke(exception);
            CancelCurrentTurn();
            ttsFinished.TrySetResult(false);
        }
        catch (Exception exception)
        {
            _logger.Error("Turn failed.", exception);
            Error?.Invoke(exception);
            ttsFinished.TrySetResult(false);
            if (generation == CurrentGeneration)
            {
                SetState(AssistantState.Recovering);
            }
        }
        finally
        {
            if (generation == CurrentGeneration)
            {
                SetState(AssistantState.Listening);
            }
        }
    }

    private async Task ProcessTtsAsync()
    {
        try
        {
            await foreach (var item in _ttsQueue.Reader.ReadAllAsync(
                               _lifetime.Token))
            {
                if (item.Generation != CurrentGeneration)
                {
                    item.Completion?.TrySetResult(true);
                    continue;
                }

                if (item.Completion is not null)
                {
                    item.Completion.TrySetResult(true);
                    continue;
                }

                SetState(AssistantState.Speaking);
                try
                {
                    await _speech.SpeakAsync(
                        item.Text,
                        _voice,
                        item.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    _logger.Error("Speech playback failed.", exception);
                    Error?.Invoke(exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("TTS worker stopped.", exception);
            Error?.Invoke(exception);
            DrainTtsQueue(exception);
        }
    }

    private void DrainTtsQueue(Exception exception)
    {
        while (_ttsQueue.Reader.TryRead(out var item))
        {
            item.Completion?.TrySetException(exception);
        }
    }

    private void SetCurrentTurn(CancellationTokenSource turn)
    {
        lock (_turnSync)
        {
            _currentTurn?.Dispose();
            _currentTurn = turn;
        }
    }

    private void ClearCurrentTurn(CancellationTokenSource turn)
    {
        lock (_turnSync)
        {
            if (ReferenceEquals(_currentTurn, turn))
            {
                _currentTurn = null;
            }
        }
    }

    private void CancelCurrentTurn()
    {
        CancellationTokenSource? turn;
        lock (_turnSync)
        {
            turn = _currentTurn;
        }
        try
        {
            turn?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SetState(AssistantState state)
    {
        lock (_stateSync)
        {
            if (_state == state)
            {
                return;
            }
            _state = state;
        }
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _commands.Writer.TryComplete();
        _ttsQueue.Writer.TryComplete();
        CancelCurrentTurn();
        _lifetime.Cancel();
        if (_commandWorker is not null)
        {
            await _commandWorker.ConfigureAwait(false);
        }
        if (_ttsWorker is not null)
        {
            await _ttsWorker.ConfigureAwait(false);
        }
        await _codex.DisposeAsync();
        await _speech.DisposeAsync();
        lock (_turnSync)
        {
            _currentTurn?.Dispose();
            _currentTurn = null;
        }
        _lifetime.Dispose();
    }

    private sealed record Command(
        long Generation,
        Transcript Transcript);

    private sealed record TtsItem(
        long Generation,
        string Text,
        TaskCompletionSource<bool>? Completion,
        CancellationToken CancellationToken);
}
