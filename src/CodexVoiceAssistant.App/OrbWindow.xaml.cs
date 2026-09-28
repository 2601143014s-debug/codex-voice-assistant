using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace CodexVoiceAssistant.App;

public partial class OrbWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private readonly AssistantHost _host;
    private readonly DispatcherTimer _glowTimer;
    private readonly SolidColorBrush _frameBrush =
        new(MediaColor.FromRgb(0x39, 0xD9, 0xFF));
    private readonly SolidColorBrush _leftWaveBrush =
        new(MediaColor.FromRgb(0x64, 0xEA, 0xFF));
    private readonly SolidColorBrush _rightWaveBrush =
        new(MediaColor.FromRgb(0x7A, 0x67, 0xFF));
    private double _targetGlowLevel;
    private double _smoothedGlowLevel;
    private double _phase;

    public OrbWindow(AssistantHost host)
    {
        InitializeComponent();
        _host = host;
        FramePolygon.Stroke = _frameBrush;
        LeftWave.Stroke = _leftWaveBrush;
        RightWave.Stroke = _rightWaveBrush;
        _host.ReplyLevelChanged += OnReplyLevelChanged;
        _glowTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            DispatcherPriority.Render,
            (_, _) => UpdateGlow(),
            Dispatcher);
        SourceInitialized += (_, _) => MakeClickThrough();
        Loaded += async (_, _) =>
        {
            PositionAboveTaskbar();
            BringAboveTaskbar();
            StartGlow();
            if (Environment.GetCommandLineArgs().Contains(
                    "--glow-test",
                    StringComparer.OrdinalIgnoreCase))
            {
                _targetGlowLevel = 1;
                await Task.Delay(10000);
                _targetGlowLevel = 0;
            }
            else
            {
                _targetGlowLevel = 0.8;
                await Task.Delay(900);
                _targetGlowLevel = 0;
            }
        };
        Closed += (_, _) =>
        {
            _glowTimer.Stop();
            _host.ReplyLevelChanged -= OnReplyLevelChanged;
        };
    }

    private void PositionAboveTaskbar()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left;
        Width = workArea.Width;
        Height = 28;
        Top = workArea.Bottom - Height;
        UpdateFrameGeometry();
    }

    private void UpdateFrameGeometry()
    {
        var width = Math.Max(320, Width);
        var height = Math.Max(20, Height);
        var center = width / 2;
        FramePolygon.Points = new PointCollection
        {
            new(0, height),
            new(22, 0),
            new(center - 185, 0),
            new(center - 150, height),
            new(center + 150, height),
            new(center + 185, 0),
            new(width - 22, 0),
            new(width, height),
        };
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style |= WsExTransparent
                 | WsExToolWindow
                 | WsExNoActivate;
        SetWindowLongPtr(
            handle,
            GwlExStyle,
            new IntPtr(style));
        BringAboveTaskbar();
    }

    private void BringAboveTaskbar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }
        SetWindowPos(
            handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove
            | SwpNoSize
            | SwpNoActivate
            | SwpShowWindow);
    }

    private void OnReplyLevelChanged(double level)
    {
        _targetGlowLevel = Math.Clamp(level, 0, 1);
        if (_targetGlowLevel > 0 || _smoothedGlowLevel > 0)
        {
            StartGlow();
        }
    }

    private void StartGlow()
    {
        if (!_glowTimer.IsEnabled)
        {
            _glowTimer.Start();
        }
    }

    private void UpdateGlow()
    {
        _smoothedGlowLevel = Math.Max(
            _targetGlowLevel,
            _smoothedGlowLevel * 0.72);
        if (_targetGlowLevel == 0 && _smoothedGlowLevel < 0.01)
        {
            _smoothedGlowLevel = 0;
            _glowTimer.Stop();
        }

        var intensity = _smoothedGlowLevel;
        _phase += 0.42 + (intensity * 0.35);
        FramePolygon.StrokeThickness = 1.5 + (intensity * 3.5);
        FramePolygon.Opacity = 0.56 + (intensity * 0.44);
        _frameBrush.Color = MediaColor.FromRgb(
            (byte)(0x39 + (0xA0 * intensity)),
            (byte)(0xD9 + (0x18 * intensity)),
            255);
        _leftWaveBrush.Color = MediaColor.FromRgb(
            (byte)(0x64 + (0x75 * intensity)),
            (byte)(0xEA + (0x08 * intensity)),
            255);
        _rightWaveBrush.Color = MediaColor.FromRgb(
            (byte)(0x7A + (0x85 * intensity)),
            (byte)(0x67 + (0x80 * intensity)),
            255);
        LeftWave.Points = CreateWavePoints(
            Math.Max(120, ActualWidth / 2 - 520),
            Math.Max(190, ActualWidth / 2 - 190),
            ActualHeight,
            intensity,
            -0.9);
        RightWave.Points = CreateWavePoints(
            Math.Max(ActualWidth / 2 + 190, ActualWidth - 520),
            ActualWidth - 120,
            ActualHeight,
            intensity,
            0.7);
        if (intensity > 0.05)
        {
            BringAboveTaskbar();
        }
    }

    private PointCollection CreateWavePoints(
        double left,
        double right,
        double height,
        double intensity,
        double phaseOffset)
    {
        var points = new PointCollection();
        var count = 30;
        var centerY = height / 2;
        var maximum = Math.Max(2, (height - 6) / 2);
        var amplitude = maximum
                        * (0.12 + (0.88 * intensity));
        for (var index = 0; index <= count; index++)
        {
            var ratio = index / (double)count;
            var x = left + ((right - left) * ratio);
            var envelope = 0.45
                           + (0.55
                              * Math.Sin(Math.PI * ratio));
            var y = centerY
                    + (Math.Sin(
                           (_phase * 1.7)
                           + phaseOffset
                           + (index * 0.82))
                       * amplitude
                       * envelope);
            points.Add(new WpfPoint(x, y));
        }
        return points;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(
        IntPtr hWnd,
        int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr hWnd,
        int nIndex,
        IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}
