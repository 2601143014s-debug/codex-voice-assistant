using System.IO;
using System.IO.Pipes;

namespace CodexVoiceAssistant.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName =
        @"Local\CodexVoiceAssistant.SingleInstance";
    private const string PipeName =
        "CodexVoiceAssistant.Activation";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task? _listener;
    private bool _ownsMutex;

    public SingleInstanceCoordinator()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        IsPrimary = createdNew;
        if (createdNew)
        {
            _listener = Task.Run(ListenAsync);
        }
    }

    public bool IsPrimary { get; }

    public event Action? ActivationRequested;

    public bool TryActivateExisting()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                client.Connect(250);
                client.WriteByte(1);
                client.Flush();
                return true;
            }
            catch (TimeoutException)
            {
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
        return false;
    }

    private async Task ListenAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_shutdown.Token);
                if (server.ReadByte() >= 0)
                {
                    ActivationRequested?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(100, _shutdown.Token);
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
        _shutdown.Dispose();

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
            }
            _ownsMutex = false;
        }
        _mutex.Dispose();
    }
}
