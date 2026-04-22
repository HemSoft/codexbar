using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CodexBar.Core.Configuration;

namespace CodexBar.App;

public partial class MainWindow : Window
{
    private const double ZoomStep = 0.1;
    private const double MinZoom = 0.5;
    private const double MaxZoom = 3.0;

    /// <summary>Duration after a drag ends during which Deactivated is suppressed.</summary>
    private static readonly TimeSpan DragCooldown = TimeSpan.FromMilliseconds(500);

    /// <summary>Delay before hiding the window on deactivation, allowing reactivation to cancel.</summary>
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(150);

    private readonly SettingsService _settings;
    private readonly DispatcherTimer _hideTimer;
    private double _zoomLevel = 1.0;
    private double _lastWidth;
    private double _lastHeight;
    private bool _hasUserPosition;
    private bool _isDragging;
    private DateTime _dragEndedAtUtc = DateTime.MinValue;
    private Point _dragStartMouseScreen;
    private double _dragStartLeft;
    private double _dragStartTop;
    private UIElement? _capturedTitleBar;

    public MainWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        _hideTimer = new DispatcherTimer { Interval = HideDelay };
        _hideTimer.Tick += HideTimer_Tick;

        var appSettings = _settings.Load();
        _zoomLevel = Math.Clamp(appSettings.ZoomLevel, MinZoom, MaxZoom);
        ZoomTransform.ScaleX = _zoomLevel;
        ZoomTransform.ScaleY = _zoomLevel;

        if (appSettings.WindowWidth is > 0)
            Width = appSettings.WindowWidth.Value;
        if (appSettings.WindowHeight is > 0)
            Height = appSettings.WindowHeight.Value;

        if (appSettings.WindowLeft is not null && appSettings.WindowTop is not null)
        {
            Left = appSettings.WindowLeft.Value;
            Top = appSettings.WindowTop.Value;
            _hasUserPosition = true;
        }

        _lastWidth = Width;
        _lastHeight = Height;

        Loaded += (_, _) =>
        {
            if (_hasUserPosition)
                EnsureOnScreen();
            else
                PositionNearTray();
        };
        SizeChanged += OnSizeChanged;
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded && IsVisible)
        {
            _lastWidth = ActualWidth;
            _lastHeight = ActualHeight;

            // Only nudge if the window actually extends beyond the work area
            var workArea = SystemParameters.WorkArea;
            if (Left + ActualWidth > workArea.Right || Top + ActualHeight > workArea.Bottom
                || Left < workArea.Left || Top < workArea.Top)
            {
                EnsureOnScreen();
            }
        }
    }

    /// <summary>
    /// Called by App.ShowPopup() after Show() to restore size, zoom, and position.
    /// </summary>
    public void RestoreState()
    {
        Width = _lastWidth;
        Height = _lastHeight;
        ZoomTransform.ScaleX = _zoomLevel;
        ZoomTransform.ScaleY = _zoomLevel;

        if (_hasUserPosition)
            EnsureOnScreen();
        else
            PositionNearTray();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _hideTimer.Stop();
        Focus();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _hideTimer.Stop();
        SaveWindowState();
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:
                    ApplyZoom(_zoomLevel + ZoomStep);
                    e.Handled = true;
                    return;
                case Key.OemMinus or Key.Subtract:
                    ApplyZoom(_zoomLevel - ZoomStep);
                    e.Handled = true;
                    return;
                case Key.D0 or Key.NumPad0:
                    ApplyZoom(1.0);
                    e.Handled = true;
                    return;
            }
        }
        base.OnKeyDown(e);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        ApplyZoom(_zoomLevel + (e.Delta > 0 ? ZoomStep : -ZoomStep));
        e.Handled = true;
    }

    private void ApplyZoom(double newZoom)
    {
        _zoomLevel = Math.Clamp(newZoom, MinZoom, MaxZoom);
        ZoomTransform.ScaleX = _zoomLevel;
        ZoomTransform.ScaleY = _zoomLevel;
    }

    private void PositionNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(0, workArea.Right - ActualWidth - 8);
        Top = Math.Max(0, workArea.Bottom - ActualHeight - 8);
    }

    private void EnsureOnScreen()
    {
        var workArea = SystemParameters.WorkArea;
        if (Left + ActualWidth > workArea.Right)
            Left = Math.Max(workArea.Left, workArea.Right - ActualWidth);
        if (Top + ActualHeight > workArea.Bottom)
            Top = Math.Max(workArea.Top, workArea.Bottom - ActualHeight);
        if (Left < workArea.Left) Left = workArea.Left;
        if (Top < workArea.Top) Top = workArea.Top;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && IsLoaded && sender is UIElement titleBar)
        {
            e.Handled = true;
            _hasUserPosition = true;
            _isDragging = true;
            _hideTimer.Stop();
            _dragStartMouseScreen = PointToScreen(e.GetPosition(this));
            _dragStartLeft = Left;
            _dragStartTop = Top;
            Activate();
            _capturedTitleBar = titleBar;
            titleBar.CaptureMouse();
        }
    }

    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTitleBarDrag();
            return;
        }

        var currentMouseScreen = PointToScreen(e.GetPosition(this));
        Left = _dragStartLeft + (currentMouseScreen.X - _dragStartMouseScreen.X);
        Top = _dragStartTop + (currentMouseScreen.Y - _dragStartMouseScreen.Y);
        e.Handled = true;
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            return;

        e.Handled = true;
        EndTitleBarDrag();
    }

    private void TitleBar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isDragging)
            EndTitleBarDrag();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_isDragging) return;
        if ((DateTime.UtcNow - _dragEndedAtUtc) < DragCooldown) return;

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideTimer_Tick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        if (IsActive || _isDragging) return;

        SaveWindowState();
        Hide();
    }

    /// <summary>Persists window geometry to disk. Safe to call multiple times.</summary>
    internal void SaveWindowState()
    {
        try
        {
            var settings = _settings.Load();
            settings.ZoomLevel = _zoomLevel;
            settings.WindowWidth = _lastWidth;
            settings.WindowHeight = _lastHeight;

            if (_hasUserPosition)
            {
                settings.WindowLeft = Left;
                settings.WindowTop = Top;
            }
            else
            {
                settings.WindowLeft = null;
                settings.WindowTop = null;
            }

            _settings.Save(settings);
        }
        catch
        {
            // Best-effort — don't crash on save failure
        }
    }

    private void EndTitleBarDrag()
    {
        _isDragging = false;
        _dragEndedAtUtc = DateTime.UtcNow;
        var capturedTitleBar = _capturedTitleBar;
        _capturedTitleBar = null;

        if (capturedTitleBar?.IsMouseCaptured == true)
            capturedTitleBar.ReleaseMouseCapture();

        SaveWindowState();
    }
}
