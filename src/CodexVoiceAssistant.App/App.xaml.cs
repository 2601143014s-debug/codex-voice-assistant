using System.Threading;
using System.Windows;

namespace CodexVoiceAssistant.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? _singleInstance;
    private AssistantHost? _host;
    private TrayController? _tray;
    private Task? _hostStartupTask;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            var exitCode = await SelfTestRunner.RunAsync();
            Shutdown(exitCode);
            return;
        }

        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.IsPrimary)
        {
            _singleInstance.TryActivateExisting();
            Shutdown();
            return;
        }

        _host = new AssistantHost();
        _hostStartupTask = StartHostAsync(_host);
        var orb = new OrbWindow(_host);
        orb.Show();
        _tray = new TrayController(_host, orb, Shutdown);
        _singleInstance.ActivationRequested += () =>
            orb.Dispatcher.BeginInvoke(
                () =>
                {
                    orb.Show();
                });
    }

    private static async Task StartHostAsync(AssistantHost host)
    {
        try
        {
            await host.StartAsync();
        }
        catch (Exception exception)
        {
            host.ReportStartupFailure(exception);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
