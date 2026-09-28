using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexVoiceAssistant.Protocol;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private static readonly TimeSpan ProcessShutdownTimeout =
        TimeSpan.FromSeconds(5);

    private readonly CodexAppServerOptions _options;
    private readonly Func<CodexAppServerOptions, ICodexAppServerProcess>
        _processFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private Connection? _connection;
    private long _nextRequestId;
    private int _disposed;

    public CodexAppServerClient(CodexAppServerOptions? options = null)
        : this(options ?? new CodexAppServerOptions(), StartProcess)
    {
    }

    internal CodexAppServerClient(
        CodexAppServerOptions options,
        Func<CodexAppServerOptions, ICodexAppServerProcess> processFactory)
    {
        _options = options;
        _processFactory = processFactory;
    }

    public string? ThreadId =>
        Volatile.Read(ref _connection)?.ThreadId;

    public event Action<string>? DiagnosticReceived;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        Connection? previous = null;
        Connection? current = null;
        try
        {
            ThrowIfDisposed();

            var activeConnection = Volatile.Read(ref _connection);
            if (activeConnection is
                {
                    IsInitialized: true,
                    IsAlive: true,
                })
            {
                return;
            }

            previous = Interlocked.Exchange(ref _connection, null);
            current = new Connection(_processFactory(_options));
            Interlocked.Exchange(ref _connection, current);
            current.StartReaders(
                connection => ReadLoopAsync(connection),
                connection => ReadErrorLoopAsync(connection));

            using var startupCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            startupCancellation.CancelAfter(_options.StartupTimeout);
            await SendRequestAsync(
                current,
                "initialize",
                new
                {
                    clientInfo = new
                    {
                    name = "codex_voice_assistant",
                    title = "Codex Voice Assistant",
                    version = _options.ClientVersion,
                    },
                },
                startupCancellation.Token);
            await SendNotificationAsync(
                current,
                "initialized",
                new { },
                startupCancellation.Token);
            current.MarkInitialized();
        }
        catch
        {
            if (current is not null)
            {
                Interlocked.CompareExchange(
                    ref _connection,
                    null,
                    current);
                await CleanupConnectionAsync(current);
                current = null;
            }

            throw;
        }
        finally
        {
            if (previous is not null
                && !ReferenceEquals(previous, current))
            {
                await CleanupConnectionAsync(previous);
            }

            _lifecycleGate.Release();
        }
    }

    public async Task<CodexTurnResult> RunTurnAsync(
        string text,
        Action<CodexTurnUpdate>? onUpdate,
        CancellationToken cancellationToken)
    {
        await _turnGate.WaitAsync(cancellationToken);
        Connection? connection = null;
        ActiveTurn? turn = null;
        try
        {
            ThrowIfDisposed();
            await StartAsync(cancellationToken);
            connection = Volatile.Read(ref _connection)
                ?? throw new InvalidOperationException(
                    "Codex app-server 尚未连接。");
            await EnsureThreadAsync(connection, cancellationToken);

            turn = new ActiveTurn(connection.ThreadId!)
            {
                OnUpdate = onUpdate,
            };
            connection.RegisterActiveTurn(turn);

            using var turnCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            turnCancellation.CancelAfter(_options.TurnTimeout);

            PublishUpdate(
                turn,
                new CodexTurnUpdate(
                    turn.ThreadId,
                    turn.TurnId,
                    "",
                    IsFinal: false));

            var startResult = await SendRequestAsync(
                connection,
                "turn/start",
                new
                {
                    threadId = turn.ThreadId,
                    effort = _options.Effort,
                    input = new[]
                    {
                        new
                        {
                            type = "text",
                            text,
                        },
                    },
                },
                turnCancellation.Token);

            var turnId = GetTurnId(startResult);
            if (!string.IsNullOrWhiteSpace(turnId))
            {
                connection.TrySetTurnId(turn, turnId);
            }

            using var cancellationRegistration =
                turnCancellation.Token.Register(
                    () => _ = InterruptAsync(
                        connection,
                        turn,
                        CancellationToken.None));
            using var firstDeltaCancellation =
                new CancellationTokenSource(_options.FirstDeltaTimeout);
            using var firstDeltaRegistration =
                firstDeltaCancellation.Token.Register(
                    () => _ = InterruptAsync(
                        connection,
                        turn,
                        CancellationToken.None));
            _ = turn.FirstDelta.Task.ContinueWith(
                _ =>
                {
                    try
                    {
                        firstDeltaCancellation.CancelAfter(
                            Timeout.InfiniteTimeSpan);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            try
            {
                return await turn.Completion.Task.WaitAsync(
                    turnCancellation.Token);
            }
            catch (OperationCanceledException)
                when (turnCancellation.IsCancellationRequested)
            {
                await InterruptAsync(
                    connection,
                    turn,
                    CancellationToken.None);
                throw;
            }
        }
        finally
        {
            if (turn is not null && connection is not null)
            {
                connection.RetireTurn(turn);
            }

            _turnGate.Release();
        }
    }

    public Task InterruptAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = Volatile.Read(ref _connection);
        var turn = connection?.GetActiveTurn();
        return turn is null
            ? Task.CompletedTask
            : InterruptAsync(connection!, turn, cancellationToken);
    }

    private async Task EnsureThreadAsync(
        Connection connection,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(connection.ThreadId))
        {
            return;
        }

        var result = await SendRequestAsync(
            connection,
            "thread/start",
            new
            {
                cwd = _options.WorkingDirectory,
                modelProvider = _options.ModelProvider,
                model = _options.Model,
                approvalPolicy = "never",
                sandbox = _options.Sandbox,
                baseInstructions =
                    "You are the backend for the user's Windows voice assistant. "
                    + "Treat each user turn as ordinary typed conversation. "
                    + "For normal conversation or dictation, answer immediately "
                    + "in one short Simplified Chinese sentence and do not call "
                    + "tools or inspect windows. "
                    + "Use tools only for explicit computer-action commands "
                    + "such as open, close, run, launch, type, click, search, "
                    + "edit, move, or delete. "
                    + "If a command is ambiguous, ask one short question instead "
                    + "of investigating. "
                    + "Do not ask for manual confirmation. "
                    + "Do not explain your reasoning.",
            },
            cancellationToken);
        connection.ThreadId = result
            .GetProperty("thread")
            .GetProperty("id")
            .GetString()
            ?? throw new InvalidOperationException(
                "Codex app-server 没有返回线程 ID。");
    }

    private async Task InterruptAsync(
        Connection connection,
        ActiveTurn turn,
        CancellationToken cancellationToken)
    {
        if (turn.InterruptRequested)
        {
            return;
        }

        var turnId = turn.TurnId;
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref turn.InterruptRequestedFlag,
                1,
                0) != 0)
        {
            return;
        }

        try
        {
            await SendRequestAsync(
                connection,
                "turn/interrupt",
                new
                {
                    threadId = turn.ThreadId,
                    turnId,
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            PublishDiagnostic(
                $"Interrupt request failed: {exception.Message}");
        }
    }

    private async Task ReadLoopAsync(Connection connection)
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                var line = await connection.Process
                    .ReadOutputLineAsync(CancellationToken.None);
                if (line is null)
                {
                    break;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                PublishDiagnostic(line);

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException exception)
                {
                    PublishDiagnostic(
                        $"Invalid app-server JSON: {exception.Message}");
                    continue;
                }

                using (document)
                {
                    await HandleMessageAsync(
                        connection,
                        document.RootElement);
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            await OnConnectionClosedAsync(connection, failure);
        }
    }

    private async Task ReadErrorLoopAsync(Connection connection)
    {
        try
        {
            while (await connection.Process.ReadErrorLineAsync(
                       CancellationToken.None) is { } errorLine)
            {
                PublishDiagnostic($"STDERR {errorLine}");
            }
        }
        catch (Exception exception)
        {
            if (!connection.IsUnavailable)
            {
                PublishDiagnostic(
                    $"STDERR reader failed: {exception.Message}");
            }
        }
    }

    private async Task HandleMessageAsync(
        Connection connection,
        JsonElement root)
    {
        if (root.TryGetProperty("method", out var methodElement)
            && root.TryGetProperty("id", out var requestId))
        {
            await RespondUnsupportedServerRequestAsync(
                connection,
                requestId,
                methodElement.GetString() ?? "unknown");
            return;
        }

        if (root.TryGetProperty("id", out var responseId))
        {
            CompleteRequest(connection, responseId, root);
            return;
        }

        if (!root.TryGetProperty("method", out methodElement)
            || !root.TryGetProperty("params", out var parameters))
        {
            return;
        }

        try
        {
            HandleNotification(
                connection,
                methodElement.GetString() ?? "",
                parameters);
        }
        catch (Exception exception)
        {
            PublishDiagnostic(
                $"Notification handling failed: {exception.Message}");
        }
    }

    private void HandleNotification(
        Connection connection,
        string method,
        JsonElement parameters)
    {
        var threadId = GetString(parameters, "threadId");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        switch (method)
        {
            case "turn/started":
            {
                var turnId = GetTurnId(parameters);
                if (string.IsNullOrWhiteSpace(turnId))
                {
                    return;
                }

                connection.AttachTurnId(threadId, turnId);
                return;
            }
            case "item/agentMessage/delta":
            {
                var turnId = GetString(parameters, "turnId");
                if (string.IsNullOrWhiteSpace(turnId))
                {
                    return;
                }

                var turn = connection.FindTurn(threadId, turnId);
                if (turn is null)
                {
                    return;
                }

                var delta = GetString(parameters, "delta") ?? "";
                turn.FirstDelta.TrySetResult();
                turn.Append(delta);
                PublishUpdate(
                    turn,
                    new CodexTurnUpdate(
                        turn.ThreadId,
                        turn.TurnId,
                        delta,
                        IsFinal: false));
                return;
            }
            case "turn/completed":
            {
                var turnId = GetTurnId(parameters);
                if (string.IsNullOrWhiteSpace(turnId))
                {
                    return;
                }

                var turn = connection.RemoveTurn(threadId, turnId);
                if (turn is null)
                {
                    return;
                }

                var result = new CodexTurnResult(
                    turn.ThreadId,
                    turn.TurnId,
                    turn.GetText().Trim());
                PublishUpdate(
                    turn,
                    new CodexTurnUpdate(
                        turn.ThreadId,
                        turn.TurnId,
                        "",
                        IsFinal: true));
                turn.Completion.TrySetResult(result);
                return;
            }
        }
    }

    private static string? GetString(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? GetTurnId(JsonElement element)
    {
        var directTurnId = GetString(element, "turnId");
        if (!string.IsNullOrWhiteSpace(directTurnId))
        {
            return directTurnId;
        }

        return element.TryGetProperty("turn", out var turn)
               && turn.ValueKind == JsonValueKind.Object
            ? GetString(turn, "id")
            : null;
    }

    private void CompleteRequest(
        Connection connection,
        JsonElement idElement,
        JsonElement root)
    {
        var id = idElement.ValueKind == JsonValueKind.Number
            ? idElement.GetInt64()
            : -1;
        if (!connection.Pending.TryRemove(id, out var request))
        {
            return;
        }

        if (root.TryGetProperty("error", out var error))
        {
            request.Completion.TrySetException(
                new InvalidOperationException(
                    error.TryGetProperty("message", out var message)
                        ? message.GetString()
                        : "Codex request failed."));
            return;
        }

        request.Completion.TrySetResult(
            root.TryGetProperty("result", out var result)
                ? result.Clone()
                : JsonSerializer.SerializeToElement(new { }));
    }

    private async Task RespondUnsupportedServerRequestAsync(
        Connection connection,
        JsonElement id,
        string method)
    {
        var response = JsonSerializer.Serialize(new
        {
            id = id.Clone(),
            error = new
            {
                code = -32601,
                message = $"Unsupported server request: {method}",
            },
        });
        await WriteMessageAsync(
            connection,
            response,
            CancellationToken.None);
    }

    private async Task<JsonElement> SendRequestAsync(
        Connection connection,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        EnsureAvailable(connection);

        var id = Interlocked.Increment(ref _nextRequestId);
        var pending = new PendingRequest();
        connection.Pending[id] = pending;
        try
        {
            var message = JsonSerializer.Serialize(new
            {
                method,
                id,
                @params = parameters,
            });
            await WriteMessageAsync(
                connection,
                message,
                cancellationToken);
            return await pending.Completion.Task.WaitAsync(
                cancellationToken);
        }
        catch
        {
            connection.Pending.TryRemove(id, out _);
            pending.Completion.TrySetCanceled(cancellationToken);
            throw;
        }
    }

    private Task SendNotificationAsync(
        Connection connection,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Serialize(new
        {
            method,
            @params = parameters,
        });
        return WriteMessageAsync(
            connection,
            message,
            cancellationToken);
    }

    private async Task WriteMessageAsync(
        Connection connection,
        string message,
        CancellationToken cancellationToken)
    {
        await connection.WriteGate.WaitAsync(cancellationToken);
        try
        {
            EnsureAvailable(connection);
            await connection.Process.WriteInputLineAsync(
                message,
                cancellationToken);
        }
        finally
        {
            connection.WriteGate.Release();
        }
    }

    private static void EnsureAvailable(Connection connection)
    {
        if (connection.IsUnavailable)
        {
            throw new InvalidOperationException(
                "Codex app-server 已断开连接。");
        }
    }

    private async Task OnConnectionClosedAsync(
        Connection connection,
        Exception? failure)
    {
        connection.MarkUnavailable();
        if (Interlocked.Exchange(
                ref connection.FailureSignaled,
                1) != 0)
        {
            return;
        }

        var exception = new InvalidOperationException(
            "Codex app-server 已断开连接。",
            failure);
        foreach (var entry in connection.Pending)
        {
            if (connection.Pending.TryRemove(entry.Key, out var request))
            {
                request.Completion.TrySetException(exception);
            }
        }

        connection.FailActiveTurn(exception);
        Interlocked.CompareExchange(
            ref _connection,
            null,
            connection);
        await Task.CompletedTask;
    }

    private async Task CleanupConnectionAsync(Connection connection)
    {
        connection.MarkUnavailable();
        try
        {
            connection.Process.Kill();
        }
        catch
        {
        }

        await WaitForShutdownAsync(
            connection.Process.WaitForExitAsync(CancellationToken.None),
            "process exit");

        if (connection.ReaderTask is not null)
        {
            await WaitForShutdownAsync(
                connection.ReaderTask,
                "reader shutdown");
        }
        if (connection.ErrorReaderTask is not null)
        {
            await WaitForShutdownAsync(
                connection.ErrorReaderTask,
                "stderr shutdown");
        }

        try
        {
            await connection.Process.DisposeAsync();
        }
        catch
        {
        }

        connection.WriteGate.Dispose();
    }

    private async Task WaitForShutdownAsync(
        Task task,
        string stage)
    {
        try
        {
            await task.WaitAsync(ProcessShutdownTimeout);
        }
        catch (TimeoutException)
        {
            PublishDiagnostic(
                $"Codex app-server {stage} timed out.");
        }
        catch
        {
        }
    }

    private void PublishUpdate(
        ActiveTurn turn,
        CodexTurnUpdate update)
    {
        try
        {
            turn.OnUpdate?.Invoke(update);
        }
        catch (Exception exception)
        {
            PublishDiagnostic(
                $"Turn callback failed: {exception.Message}");
        }
    }

    private void PublishDiagnostic(string message)
    {
        try
        {
            DiagnosticReceived?.Invoke(message);
        }
        catch
        {
        }
    }

    private static ICodexAppServerProcess StartProcess(
        CodexAppServerOptions options)
    {
        Directory.CreateDirectory(options.WorkingDirectory);
        PrepareCodexHome(options);
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Executable,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.Environment["CODEX_HOME"] = options.CodexHome;
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            $"model_reasoning_effort=\"{options.Effort}\"");
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "无法启动 Codex app-server。");
        return new ProcessCodexAppServerProcess(process);
    }

    private static void PrepareCodexHome(
        CodexAppServerOptions options)
    {
        Directory.CreateDirectory(options.CodexHome);
        var catalogLine = string.IsNullOrWhiteSpace(
            options.ModelCatalogPath)
            ? ""
            : $"model_catalog_json = '{options.ModelCatalogPath}'";
        var config =
            $"""
             model_provider = "{EscapeToml(options.ModelProvider)}"
             model = "{EscapeToml(options.Model)}"
             model_reasoning_effort = "{EscapeToml(options.Effort)}"
             {catalogLine}

             [model_providers.DeepSeek]
             name = "DeepSeek"
             base_url = "https://api.deepseek.com"
             wire_api = "responses"
             env_key = "DEEPSEEK_API_KEY"
             requires_openai_auth = false
             stream_idle_timeout_ms = 300000
             """;
        File.WriteAllText(
            Path.Combine(options.CodexHome, "config.toml"),
            config,
            new UTF8Encoding(false));
    }

    private static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(
                nameof(CodexAppServerClient));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync();
        try
        {
            var connection = Interlocked.Exchange(
                ref _connection,
                null);
            if (connection is not null)
            {
                await CleanupConnectionAsync(connection);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await _turnGate.WaitAsync();
        _turnGate.Release();
        _lifecycleGate.Dispose();
        _turnGate.Dispose();
    }

    private sealed class Connection
    {
        private readonly object _turnSync = new();
        private readonly Dictionary<string, ActiveTurn> _turns =
            new(StringComparer.Ordinal);
        private readonly Queue<ActiveTurn> _retiredTurns = new();
        private int _initialized;
        private int _unavailable;

        public Connection(ICodexAppServerProcess process)
        {
            Process = process;
        }

        public ICodexAppServerProcess Process { get; }
        public ConcurrentDictionary<long, PendingRequest> Pending { get; } =
            new();
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public Task? ReaderTask { get; private set; }
        public Task? ErrorReaderTask { get; private set; }
        public string? ThreadId { get; set; }
        public ActiveTurn? ActiveTurn { get; private set; }
        public bool IsInitialized => Volatile.Read(ref _initialized) != 0;
        public bool IsUnavailable => Volatile.Read(ref _unavailable) != 0;
        public int FailureSignaled;
        public bool IsAlive
        {
            get
            {
                if (IsUnavailable)
                {
                    return false;
                }

                try
                {
                    return !Process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void StartReaders(
            Func<Connection, Task> reader,
            Func<Connection, Task> errorReader)
        {
            ReaderTask = Task.Run(() => reader(this));
            ErrorReaderTask = Task.Run(() => errorReader(this));
        }

        public void MarkInitialized()
        {
            Volatile.Write(ref _initialized, 1);
        }

        public void MarkUnavailable()
        {
            Volatile.Write(ref _unavailable, 1);
        }

        public void RegisterActiveTurn(ActiveTurn turn)
        {
            lock (_turnSync)
            {
                if (ActiveTurn is not null)
                {
                    throw new InvalidOperationException(
                        "A turn is already active.");
                }

                ActiveTurn = turn;
            }
        }

        public ActiveTurn? GetActiveTurn()
        {
            lock (_turnSync)
            {
                return ActiveTurn;
            }
        }

        public void AttachTurnId(string threadId, string turnId)
        {
            lock (_turnSync)
            {
                var turn = ActiveTurn;
                if (turn is null
                    || !string.Equals(
                        turn.ThreadId,
                        threadId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(turn.TurnId)
                    && !string.Equals(
                        turn.TurnId,
                        turnId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                turn.TurnId = turnId;
                _turns[turnId] = turn;
            }
        }

        public bool TrySetTurnId(ActiveTurn turn, string turnId)
        {
            lock (_turnSync)
            {
                if (!ReferenceEquals(ActiveTurn, turn)
                    || turn.Completion.Task.IsCompleted)
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(turn.TurnId)
                    && !string.Equals(
                        turn.TurnId,
                        turnId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                turn.TurnId = turnId;
                _turns[turnId] = turn;
                return true;
            }
        }

        public ActiveTurn? FindTurn(string threadId, string turnId)
        {
            lock (_turnSync)
            {
                return _turns.TryGetValue(turnId, out var turn)
                       && string.Equals(
                           turn.ThreadId,
                           threadId,
                           StringComparison.Ordinal)
                    ? turn
                    : null;
            }
        }

        public ActiveTurn? RemoveTurn(string threadId, string turnId)
        {
            lock (_turnSync)
            {
                if (!_turns.TryGetValue(turnId, out var turn)
                    || !string.Equals(
                        turn.ThreadId,
                        threadId,
                        StringComparison.Ordinal))
                {
                    return null;
                }

                _turns.Remove(turnId);
                return turn;
            }
        }

        public void RetireTurn(ActiveTurn turn)
        {
            lock (_turnSync)
            {
                if (ReferenceEquals(ActiveTurn, turn))
                {
                    ActiveTurn = null;
                }

                if (turn.Completion.Task.IsCompleted)
                {
                    if (!string.IsNullOrWhiteSpace(turn.TurnId))
                    {
                        _turns.Remove(turn.TurnId);
                    }

                    return;
                }

                if (!string.IsNullOrWhiteSpace(turn.TurnId))
                {
                    _turns.TryAdd(turn.TurnId, turn);
                    _retiredTurns.Enqueue(turn);
                }

                while (_turns.Count > 16
                       && _retiredTurns.TryDequeue(out var retired))
                {
                    if (!ReferenceEquals(ActiveTurn, retired)
                        && !string.IsNullOrWhiteSpace(retired.TurnId))
                    {
                        _turns.Remove(retired.TurnId);
                    }
                }
            }
        }

        public void FailActiveTurn(Exception exception)
        {
            lock (_turnSync)
            {
                ActiveTurn?.Completion.TrySetException(exception);
                _turns.Clear();
                _retiredTurns.Clear();
                ActiveTurn = null;
            }
        }
    }

    private sealed class PendingRequest
    {
        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ActiveTurn
    {
        private readonly object _textSync = new();
        private readonly StringBuilder _text = new();

        public ActiveTurn(string threadId)
        {
            ThreadId = threadId;
        }

        public string ThreadId { get; }
        public string TurnId { get; set; } = "";
        public TaskCompletionSource FirstDelta { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CodexTurnResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<CodexTurnUpdate>? OnUpdate { get; set; }
        public int InterruptRequestedFlag;
        public bool InterruptRequested =>
            Volatile.Read(ref InterruptRequestedFlag) != 0;

        public void Append(string delta)
        {
            lock (_textSync)
            {
                _text.Append(delta);
            }
        }

        public string GetText()
        {
            lock (_textSync)
            {
                return _text.ToString();
            }
        }
    }

    private sealed class ProcessCodexAppServerProcess :
        ICodexAppServerProcess
    {
        private readonly Process _process;
        private readonly StreamWriter _input;

        public ProcessCodexAppServerProcess(Process process)
        {
            _process = process;
            _input = process.StandardInput;
        }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch
                {
                    return true;
                }
            }
        }

        public async Task<string?> ReadOutputLineAsync(
            CancellationToken cancellationToken)
        {
            return await _process.StandardOutput.ReadLineAsync(
                cancellationToken);
        }

        public async Task<string?> ReadErrorLineAsync(
            CancellationToken cancellationToken)
        {
            return await _process.StandardError.ReadLineAsync(
                cancellationToken);
        }

        public async Task WriteInputLineAsync(
            string line,
            CancellationToken cancellationToken)
        {
            await _input.WriteLineAsync(
                line.AsMemory(),
                cancellationToken);
            await _input.FlushAsync(cancellationToken);
        }

        public void Kill()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        public Task WaitForExitAsync(
            CancellationToken cancellationToken)
        {
            return _process.WaitForExitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                _input.Dispose();
            }
            catch
            {
            }

            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal interface ICodexAppServerProcess : IAsyncDisposable
{
    bool HasExited { get; }

    Task<string?> ReadOutputLineAsync(
        CancellationToken cancellationToken);

    Task<string?> ReadErrorLineAsync(
        CancellationToken cancellationToken);

    Task WriteInputLineAsync(
        string line,
        CancellationToken cancellationToken);

    void Kill();

    Task WaitForExitAsync(CancellationToken cancellationToken);
}
