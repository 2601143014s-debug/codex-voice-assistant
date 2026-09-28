using System.Collections.Concurrent;
using CodexVoiceAssistant.Domain;

namespace CodexVoiceAssistant.Tests;

public sealed class TurnSupervisorTests
{
    [Fact]
    public async Task StreamsSentencesToSpeech()
    {
        var codex = new FakeCodexClient
        {
            Deltas = { "第一句。", "第二句！" },
        };
        var speech = new RecordingSpeechSynthesizer();
        await using var supervisor = new TurnSupervisor(
            codex,
            speech,
            NullLogger.Instance,
            "voice");

        await supervisor.StartAsync();
        await supervisor.SubmitAsync(
            new Transcript("你好", 1, DateTimeOffset.Now, TimeSpan.FromSeconds(1)));
        await codex.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(100);

        Assert.Equal(new[] { "第一句。", "第二句！" }, speech.Spoken);
    }

    [Fact]
    public async Task InterruptInvalidatesPreviousTurn()
    {
        var codex = new FakeCodexClient
        {
            Block = true,
        };
        var speech = new RecordingSpeechSynthesizer();
        await using var supervisor = new TurnSupervisor(
            codex,
            speech,
            NullLogger.Instance,
            "voice");

        await supervisor.StartAsync();
        await supervisor.SubmitAsync(
            new Transcript("旧任务", 1, DateTimeOffset.Now, TimeSpan.FromSeconds(1)));
        await codex.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await supervisor.InterruptAsync();

        Assert.Equal(AssistantState.Listening, supervisor.State);
        Assert.Contains("旧任务", codex.InterruptedThreads);
    }

    [Fact]
    public async Task InterruptStopsActiveSpeech()
    {
        var codex = new FakeCodexClient
        {
            Deltas = { "第一句。" },
            Block = true,
        };
        var speech = new BlockingSpeechSynthesizer();
        await using var supervisor = new TurnSupervisor(
            codex,
            speech,
            NullLogger.Instance,
            "voice");

        await supervisor.StartAsync();
        await supervisor.SubmitAsync(
            new Transcript("旧任务", 1, DateTimeOffset.Now, TimeSpan.FromSeconds(1)));
        await speech.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await supervisor.InterruptAsync();

        Assert.Equal(AssistantState.Listening, supervisor.State);
        Assert.Equal(1, speech.StopCalls);
    }

    [Fact]
    public async Task RecoversAfterCodexTurnFailure()
    {
        var codex = new FailOnceCodexClient();
        var speech = new RecordingSpeechSynthesizer();
        await using var supervisor = new TurnSupervisor(
            codex,
            speech,
            NullLogger.Instance,
            "voice");

        await supervisor.StartAsync();
        await supervisor.SubmitAsync(
            new Transcript("第一次", 1, DateTimeOffset.Now, TimeSpan.FromSeconds(1)));
        await codex.FirstFailed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await supervisor.SubmitAsync(
            new Transcript("第二次", 1, DateTimeOffset.Now, TimeSpan.FromSeconds(1)));
        await codex.SecondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(100);

        Assert.Equal(AssistantState.Listening, supervisor.State);
        Assert.Contains("恢复成功。", speech.Spoken);
    }

    [Fact]
    public async Task ContinuesAfterSpeechPlaybackFailure()
    {
        var codex = new FakeCodexClient
        {
            Deltas = { "回复。" },
        };
        var speech = new FailOnceSpeechSynthesizer();
        await using var supervisor = new TurnSupervisor(
            codex,
            speech,
            NullLogger.Instance,
            "voice");

        await supervisor.StartAsync();
        await supervisor.SubmitAsync(
            new Transcript("第一次", 1, DateTimeOffset.Now, TimeSpan.FromSeconds(1)));
        await speech.FirstFailed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await supervisor.SubmitAsync(
            new Transcript("第二次", 1, DateTimeOffset.Now, TimeSpan.FromSeconds(1)));
        await speech.SecondSpoken.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(AssistantState.Listening, supervisor.State);
    }

    private sealed class FakeCodexClient : ICodexTurnClient
    {
        private readonly ConcurrentDictionary<long, TaskCompletionSource<TurnResult>>
            _completions = new();
        public List<string> Deltas { get; } = new();
        public bool Block { get; init; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentBag<string> InterruptedThreads { get; } = new();

        public async Task<TurnResult> RunTurnAsync(
            long generation,
            string text,
            Action<TurnUpdate>? onUpdate,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            foreach (var delta in Deltas)
            {
                onUpdate?.Invoke(
                    new TurnUpdate(generation, "thread", "turn", delta, false));
            }

            if (Block)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            Finished.TrySetResult();
            return new TurnResult(generation, "thread", "turn", text);
        }

        public Task InterruptAsync(CancellationToken cancellationToken = default)
        {
            InterruptedThreads.Add("旧任务");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSpeechSynthesizer : ISpeechSynthesizer
    {
        public List<string> Spoken { get; } = new();

        public Task SpeakAsync(
            string text,
            string voice,
            CancellationToken cancellationToken)
        {
            Spoken.Add(text);
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSpeechSynthesizer : ISpeechSynthesizer
    {
        private int _stopCalls;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopCalls => Volatile.Read(ref _stopCalls);

        public async Task SpeakAsync(
            string text,
            string voice,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref _stopCalls);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailOnceCodexClient : ICodexTurnClient
    {
        private int _calls;

        public TaskCompletionSource FirstFailed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TurnResult> RunTurnAsync(
            long generation,
            string text,
            Action<TurnUpdate>? onUpdate,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstFailed.TrySetResult();
                throw new InvalidOperationException("Codex crashed.");
            }

            onUpdate?.Invoke(
                new TurnUpdate(
                    generation,
                    "thread",
                    "turn",
                    "恢复成功。",
                    false));
            SecondCompleted.TrySetResult();
            await Task.CompletedTask;
            return new TurnResult(
                generation,
                "thread",
                "turn",
                "恢复成功。");
        }

        public Task InterruptAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailOnceSpeechSynthesizer : ISpeechSynthesizer
    {
        private int _calls;

        public TaskCompletionSource FirstFailed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSpoken { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SpeakAsync(
            string text,
            string voice,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstFailed.TrySetResult();
                throw new InvalidOperationException(
                    "Playback device was removed.");
            }

            SecondSpoken.TrySetResult();
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullLogger : IAppLogger
    {
        public static NullLogger Instance { get; } = new();
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
        public void Stage(TimedStage stage) { }
    }
}
