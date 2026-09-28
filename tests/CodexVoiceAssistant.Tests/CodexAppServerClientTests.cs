using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using CodexVoiceAssistant.Protocol;

namespace CodexVoiceAssistant.Tests;

public sealed class CodexAppServerClientTests
{
    private static readonly TimeSpan TestTimeout =
        TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConcurrentRestartCreatesOnlyOneReplacementProcess()
    {
        var factory = new ScriptedProcessFactory
        {
            AutoCompleteTurns = true,
        };
        await using var client = CreateClient(factory);

        await client.StartAsync(CancellationToken.None);
        var first = await factory.WaitForProcessAsync(0, TestTimeout);
        first.Fail();

        await Task.WhenAll(
                Enumerable.Range(0, 8)
                    .Select(
                        _ => client.StartAsync(
                            CancellationToken.None)))
            .WaitAsync(TestTimeout);

        Assert.Equal(2, factory.Count);
        var result = await client.RunTurnAsync(
                "hello",
                null,
                CancellationToken.None)
            .WaitAsync(TestTimeout);
        Assert.Equal("response-turn-1", result.Text);
        Assert.Equal(2, factory.Count);
    }

    [Fact]
    public async Task StaleReaderCannotResetReplacementConnection()
    {
        var factory = new ScriptedProcessFactory
        {
            AutoCompleteTurns = true,
        };
        await using var client = CreateClient(factory);

        await client.StartAsync(CancellationToken.None);
        var first = await factory.WaitForProcessAsync(0, TestTimeout);
        first.Fail(holdReaderAtEnd: true);

        var restart = client.StartAsync(CancellationToken.None);
        var replacement = await factory.WaitForProcessAsync(1, TestTimeout);
        await replacement.Initialized.WaitAsync(TestTimeout);

        first.AllowReaderToExit();
        await restart.WaitAsync(TestTimeout);

        var result = await client.RunTurnAsync(
                "after restart",
                null,
                CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.Equal("response-turn-1", result.Text);
        Assert.Equal(2, factory.Count);
    }

    [Fact]
    public async Task ProcessDeathFailsPendingRequest()
    {
        var factory = new ScriptedProcessFactory();
        await using var client = CreateClient(factory);

        await client.StartAsync(CancellationToken.None);
        var process = await factory.WaitForProcessAsync(0, TestTimeout);
        process.IgnoreMethod("turn/start");

        var turn = client.RunTurnAsync(
            "pending",
            null,
            CancellationToken.None);
        await process.WaitForRequestAsync("turn/start", TestTimeout);

        process.Fail();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => turn.WaitAsync(TestTimeout));
        Assert.Contains("断开连接", exception.Message);
    }

    [Fact]
    public async Task FirstDeltaDisarmsFirstDeltaTimeout()
    {
        var factory = new ScriptedProcessFactory
        {
            AutoCompleteTurns = false,
        };
        await using var client = new CodexAppServerClient(
            new CodexAppServerOptions
            {
                StartupTimeout = TestTimeout,
                TurnTimeout = TestTimeout,
                FirstDeltaTimeout = TimeSpan.FromMilliseconds(150),
            },
            factory.Create);

        await client.StartAsync(CancellationToken.None);
        var process = await factory.WaitForProcessAsync(0, TestTimeout);
        var turn = client.RunTurnAsync(
            "streaming",
            null,
            CancellationToken.None);
        await process.WaitForRequestAsync("turn/start", TestTimeout);

        await process.EmitDeltaAsync(
            process.ThreadId,
            process.LastTurnId,
            "partial");
        await Task.Delay(400);

        Assert.DoesNotContain(
            "turn/interrupt",
            process.RequestMethods);
        await process.EmitCompletedAsync(
            process.ThreadId,
            process.LastTurnId);
        var result = await turn.WaitAsync(TestTimeout);
        Assert.Equal("partial", result.Text);
    }

    [Fact]
    public async Task LateInterruptedTurnDeltaIsPreservedButIsolated()
    {
        var factory = new ScriptedProcessFactory
        {
            AutoCompleteTurns = false,
        };
        await using var client = CreateClient(factory);

        await client.StartAsync(CancellationToken.None);
        var process = await factory.WaitForProcessAsync(0, TestTimeout);
        var oldDeltas = new ConcurrentQueue<string>();
        var newDeltas = new ConcurrentQueue<string>();
        using var oldTurnCancellation =
            new CancellationTokenSource(TestTimeout);

        var oldTurn = client.RunTurnAsync(
            "old",
            update =>
            {
                if (update.Delta.Length > 0)
                {
                    oldDeltas.Enqueue(update.Delta);
                }
            },
            oldTurnCancellation.Token);

        await process.WaitForRequestAsync("turn/start", TestTimeout);
        var oldTurnId = process.LastTurnId;
        await client.InterruptAsync();
        oldTurnCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => oldTurn);

        var newTurn = client.RunTurnAsync(
            "new",
            update =>
            {
                if (update.Delta.Length > 0)
                {
                    newDeltas.Enqueue(update.Delta);
                }
            },
            CancellationToken.None);

        await process.WaitForRequestAsync("turn/start", TestTimeout);
        var newTurnId = process.LastTurnId;
        Assert.NotEqual(oldTurnId, newTurnId);

        await process.EmitDeltaAsync(
            "wrong-thread",
            oldTurnId,
            "wrong-thread");
        await process.EmitDeltaAsync(
            process.ThreadId,
            oldTurnId,
            "late-old");
        await process.EmitCompletedAsync(
            process.ThreadId,
            oldTurnId);
        await process.EmitDeltaAsync(
            process.ThreadId,
            newTurnId,
            "new-text");
        await process.EmitCompletedAsync(
            process.ThreadId,
            newTurnId);

        var result = await newTurn.WaitAsync(TestTimeout);

        Assert.Equal("new-text", result.Text);
        Assert.Contains("late-old", oldDeltas);
        Assert.DoesNotContain("wrong-thread", oldDeltas);
        Assert.DoesNotContain("late-old", newDeltas);
        Assert.Contains("new-text", newDeltas);
    }

    private static CodexAppServerClient CreateClient(
        ScriptedProcessFactory factory)
    {
        return new CodexAppServerClient(
            new CodexAppServerOptions
            {
                StartupTimeout = TestTimeout,
                TurnTimeout = TestTimeout,
                FirstDeltaTimeout = TestTimeout,
            },
            factory.Create);
    }

    private sealed class ScriptedProcessFactory
    {
        private readonly object _sync = new();
        private readonly List<FakeAppServerProcess> _processes = new();
        private readonly ConcurrentDictionary<
            int,
            TaskCompletionSource<FakeAppServerProcess>> _created = new();

        public bool AutoCompleteTurns { get; init; }
        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _processes.Count;
                }
            }
        }

        public ICodexAppServerProcess Create(
            CodexAppServerOptions options)
        {
            int index;
            FakeAppServerProcess process;
            lock (_sync)
            {
                index = _processes.Count;
                process = new FakeAppServerProcess(
                    index,
                    AutoCompleteTurns);
                _processes.Add(process);
            }

            _created.GetOrAdd(
                    index,
                    _ => new TaskCompletionSource<FakeAppServerProcess>(
                        TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult(process);
            return process;
        }

        public async Task<FakeAppServerProcess> WaitForProcessAsync(
            int index,
            TimeSpan timeout)
        {
            var completion = _created.GetOrAdd(
                index,
                _ => new TaskCompletionSource<FakeAppServerProcess>(
                    TaskCreationOptions.RunContinuationsAsynchronously));
            return await completion.Task.WaitAsync(timeout);
        }
    }

    private sealed class FakeAppServerProcess :
        ICodexAppServerProcess
    {
        private readonly Channel<string> _input =
            Channel.CreateUnbounded<string>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                });
        private readonly Channel<string> _output =
            Channel.CreateUnbounded<string>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                });
        private readonly Channel<string> _error =
            Channel.CreateUnbounded<string>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                });
        private readonly Channel<JsonElement> _requests =
            Channel.CreateUnbounded<JsonElement>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true,
                });
        private readonly ConcurrentDictionary<string, byte> _ignoredMethods =
            new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _requestMethods = new();
        private readonly TaskCompletionSource _allowReaderToExit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _inputLoop;
        private readonly bool _autoCompleteTurns;
        private readonly TaskCompletionSource _initialized =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _hasExited;
        private int _holdReaderAtEnd;
        private int _turnCounter;
        private string _lastTurnId = "";

        public FakeAppServerProcess(
            int index,
            bool autoCompleteTurns)
        {
            ThreadId = $"thread-{index + 1}";
            _autoCompleteTurns = autoCompleteTurns;
            _inputLoop = Task.Run(ProcessInputAsync);
        }

        public string ThreadId { get; }
        public string LastTurnId => Volatile.Read(ref _lastTurnId);
        public bool HasExited => Volatile.Read(ref _hasExited) != 0;
        public Task Initialized => _initialized.Task;
        public IReadOnlyCollection<string> RequestMethods =>
            _requestMethods.ToArray();

        public void IgnoreMethod(string method)
        {
            _ignoredMethods[method] = 0;
        }

        public void Fail(bool holdReaderAtEnd = false)
        {
            if (holdReaderAtEnd)
            {
                Volatile.Write(ref _holdReaderAtEnd, 1);
            }

            if (Interlocked.Exchange(ref _hasExited, 1) != 0)
            {
                return;
            }

            _input.Writer.TryComplete();
            _output.Writer.TryComplete();
            _error.Writer.TryComplete();
            _exit.TrySetResult();
        }

        public void AllowReaderToExit()
        {
            _allowReaderToExit.TrySetResult();
        }

        public async Task<string?> ReadOutputLineAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                return await _output.Reader.ReadAsync(
                    cancellationToken);
            }
            catch (ChannelClosedException)
            {
                if (Volatile.Read(ref _holdReaderAtEnd) != 0)
                {
                    await _allowReaderToExit.Task.WaitAsync(
                        cancellationToken);
                }

                return null;
            }
        }

        public async Task<string?> ReadErrorLineAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                return await _error.Reader.ReadAsync(
                    cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public Task WriteInputLineAsync(
            string line,
            CancellationToken cancellationToken)
        {
            if (HasExited)
            {
                throw new IOException("Fake process exited.");
            }

            return _input.Writer.WriteAsync(
                    line,
                    cancellationToken)
                .AsTask();
        }

        public void Kill()
        {
            Fail();
        }

        public Task WaitForExitAsync(
            CancellationToken cancellationToken)
        {
            return _exit.Task.WaitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Fail();
            await _inputLoop.ConfigureAwait(false);
        }

        public async Task EmitDeltaAsync(
            string threadId,
            string turnId,
            string delta)
        {
            await WriteOutputAsync(
                JsonSerializer.Serialize(new
                {
                    method = "item/agentMessage/delta",
                    @params = new
                    {
                        threadId,
                        turnId,
                        itemId = "item-1",
                        delta,
                    },
                }));
        }

        public async Task EmitCompletedAsync(
            string threadId,
            string turnId)
        {
            await WriteOutputAsync(
                JsonSerializer.Serialize(new
                {
                    method = "turn/completed",
                    @params = new
                    {
                        threadId,
                        turn = new
                        {
                            id = turnId,
                            status = "completed",
                            items = Array.Empty<object>(),
                        },
                    },
                }));
        }

        public async Task<JsonElement> WaitForRequestAsync(
            string method,
            TimeSpan timeout)
        {
            using var cancellation =
                new CancellationTokenSource(timeout);
            while (await _requests.Reader.WaitToReadAsync(
                       cancellation.Token))
            {
                while (_requests.Reader.TryRead(out var request))
                {
                    if (string.Equals(
                            request.GetProperty("method").GetString(),
                            method,
                            StringComparison.Ordinal))
                    {
                        return request;
                    }
                }
            }

            throw new TimeoutException(
                $"Request {method} was not received.");
        }

        private async Task ProcessInputAsync()
        {
            try
            {
                while (await _input.Reader.WaitToReadAsync())
                {
                    while (_input.Reader.TryRead(out var line))
                    {
                        await ProcessLineAsync(line);
                    }
                }
            }
            catch (ChannelClosedException)
            {
            }
        }

        private async Task ProcessLineAsync(string line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var methodElement))
            {
                return;
            }

            var method = methodElement.GetString() ?? "";
            _requestMethods.Enqueue(method);
            if (!root.TryGetProperty("id", out var id))
            {
                return;
            }

            if (_ignoredMethods.ContainsKey(method))
            {
                _requests.Writer.TryWrite(root.Clone());
                return;
            }

            switch (method)
            {
                case "initialize":
                    await WriteResultAsync(id, new { });
                    _initialized.TrySetResult();
                    break;
                case "thread/start":
                    await WriteResultAsync(
                        id,
                        new
                        {
                            thread = new
                            {
                                id = ThreadId,
                            },
                        });
                    break;
                case "turn/start":
                    await StartTurnAsync(id);
                    break;
                case "turn/interrupt":
                    await WriteResultAsync(id, new { });
                    break;
                default:
                    await WriteErrorAsync(
                        id,
                        -32601,
                        $"Unsupported method: {method}");
                    break;
            }

            _requests.Writer.TryWrite(root.Clone());
        }

        private async Task StartTurnAsync(JsonElement id)
        {
            var turnId = $"turn-{Interlocked.Increment(ref _turnCounter)}";
            Volatile.Write(ref _lastTurnId, turnId);
            await WriteResultAsync(
                id,
                new
                {
                    turn = new
                    {
                        id = turnId,
                        status = "inProgress",
                        items = Array.Empty<object>(),
                    },
                });

            await WriteOutputAsync(
                JsonSerializer.Serialize(new
                {
                    method = "turn/started",
                    @params = new
                    {
                        threadId = ThreadId,
                        turn = new
                        {
                            id = turnId,
                            status = "inProgress",
                            items = Array.Empty<object>(),
                        },
                    },
                }));

            if (!_autoCompleteTurns)
            {
                return;
            }

            await EmitDeltaAsync(ThreadId, turnId, "response-");
            await EmitDeltaAsync(ThreadId, turnId, turnId);
            await EmitCompletedAsync(ThreadId, turnId);
        }

        private Task WriteResultAsync(
            JsonElement id,
            object result)
        {
            return WriteOutputAsync(
                JsonSerializer.Serialize(new
                {
                    id = id.Clone(),
                    result,
                }));
        }

        private Task WriteErrorAsync(
            JsonElement id,
            int code,
            string message)
        {
            return WriteOutputAsync(
                JsonSerializer.Serialize(new
                {
                    id = id.Clone(),
                    error = new
                    {
                        code,
                        message,
                    },
                }));
        }

        private Task WriteOutputAsync(string line)
        {
            return _output.Writer.WriteAsync(line).AsTask();
        }
    }
}
