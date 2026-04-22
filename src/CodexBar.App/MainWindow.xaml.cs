using System.Windows;
using System.Windows.Input;
using CodexBar.Core.Configuration;

namespace CodexBar.App;

public partial class MainWindow : Window
{
    private const double ZoomStep = 0.1;
    private const double MinZoom = 0.5;
    private const double MaxZoom = 3.0;

    private readonly SettingsService _settings;
    private double _zoomLevel = 1.0;
    private double _lastWidth;
    private double _lastHeight;

    public MainWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        var appSettings = _settings.Load();
        _zoomLevel = Math.Clamp(appSettings.ZoomLevel, MinZoom, MaxZoom);
        ZoomTransform.ScaleX = _zoomLevel;
        ZoomTransform.ScaleY = _zoomLevel;

        if (appSettings.WindowWidth is > 0)
            Width = appSettings.WindowWidth.Value;
        if (appSettings.WindowHeight is > 0)
            Height = appSettings.WindowHeight.Value;

        _lastWidth = Width;
        _lastHeight = Height;

        Loaded += (_, _) => PositionNearTray();
        SizeChanged += OnSizeChanged;
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded && IsVisible)
        {
            _lastWidth = ActualWidth;
            _lastHeight = ActualHeight;
        }
        if (IsLoaded) PositionNearTray();
    }

    /// <summary>
    /// Called by App.ShowPopup() after Show() to restore size and zoom.
    /// </summary>
    public void RestoreState()
    {
        Width = _lastWidth;
        Height = _lastHeight;
        ZoomTransform.ScaleX = _zoomLevel;
        ZoomTransform.ScaleY = _zoomLevel;
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Focus();
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

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        SaveWindowState();
        Hide();
    }

    private void SaveWindowState()
    {
        try
        {
            var settings = _settings.Load();
            settings.ZoomLevel = _zoomLevel;
            settings.WindowWidth = _lastWidth;
            settings.WindowHeight = _lastHeight;
            _settings.Save(settings);
        }
        catch
        {
            // Best-effort — don't crash on save failure
        }
    }
}