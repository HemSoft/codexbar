// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App;

using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.Core.Configuration;

public partial class MainWindow : Window
{
    private const double ZoomStep = 0.1;
    private const double MinZoom = 0.5;
    private const double MaxZoom = 3.0;

    private const int WMNCHITTEST = 0x0084;
    private const int WMENTERSIZEMOVE = 0x0231;
    private const int WMEXITSIZEMOVE = 0x0232;
    private const int WMSETCURSOR = 0x0020;
    private const int HTCAPTION = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    private static readonly IntPtr IDCSIZEALL = new IntPtr(32646);

    /// <summary>Duration after a drag ends during which Deactivated is suppressed.</summary>
    private static readonly TimeSpan DragCooldown = TimeSpan.FromMilliseconds(500);

    /// <summary>Delay before hiding the window on deactivation, allowing reactivation to cancel.</summary>
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(150);

    private readonly SettingsService settings;
    private readonly DispatcherTimer hideTimer;
    private double zoomLevel = 1.0;
    private double lastWidth;
    private double lastHeight;
    private double? savedLeft;
    private double? savedTop;
    private bool hasUserPosition;
    private bool isDragging;
    private DateTime dragEndedAtUtc = DateTime.MinValue;
    private HwndSource? hwndSource;

    public MainWindow(SettingsService settings)
    {
        this.settings = settings;
        this.InitializeComponent();

        this.hideTimer = new DispatcherTimer { Interval = HideDelay };
        this.hideTimer.Tick += this.HideTimer_Tick;

        var appSettings = this.settings.Load();
        this.zoomLevel = Math.Clamp(appSettings.ZoomLevel, MinZoom, MaxZoom);
        this.ZoomTransform.ScaleX = this.zoomLevel;
        this.ZoomTransform.ScaleY = this.zoomLevel;

        if (appSettings.WindowWidth is > 0)
        {
            this.Width = appSettings.WindowWidth.Value;
        }

        // Do NOT set Height from settings — SizeToContent="Height" controls it.
        if (appSettings.WindowLeft is not null && appSettings.WindowTop is not null)
        {
            this.savedLeft = appSettings.WindowLeft.Value;
            this.savedTop = appSettings.WindowTop.Value;
            this.Left = this.savedLeft.Value;
            this.Top = this.savedTop.Value;
            this.hasUserPosition = true;
        }

        this.lastWidth = appSettings.WindowWidth ?? this.Width;
        this.lastHeight = appSettings.WindowHeight ?? this.ActualHeight;

        this.SizeChanged += this.OnSizeChanged;
        this.PreviewMouseWheel += this.OnPreviewMouseWheel;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (this.IsLoaded && this.IsVisible)
        {
            this.lastWidth = this.ActualWidth;
            this.lastHeight = this.ActualHeight;

            // Only nudge if the window actually extends beyond the work area
            var workArea = SystemParameters.WorkArea;
            if (this.Left + this.ActualWidth > workArea.Right || this.Top + this.ActualHeight > workArea.Bottom
                || this.Left < workArea.Left || this.Top < workArea.Top)
            {
                this.EnsureOnScreen();
            }
        }
    }

    /// <summary>
    /// Called by App.ShowPopup() before Show() to set position.
    /// Position is applied before Show() to prevent flicker.
    /// EnsureOnScreen is deferred to Loaded so ActualWidth/ActualHeight are final.
    /// </summary>
    public void RestoreState()
    {
        this.Width = this.lastWidth;
        this.ZoomTransform.ScaleX = this.zoomLevel;
        this.ZoomTransform.ScaleY = this.zoomLevel;

        // Height is NOT set — SizeToContent="Height" controls it.
        if (this.savedLeft is not null && this.savedTop is not null)
        {
            this.Left = this.savedLeft.Value;
            this.Top = this.savedTop.Value;
            this.hasUserPosition = true;

            // Defer EnsureOnScreen to Loaded when ActualWidth is known.
            this.Loaded += this.OnLoadedEnsureOnScreen;
        }
        else
        {
            this.Loaded += this.OnLoadedPositionNearTray;
        }
    }

    private void OnLoadedEnsureOnScreen(object? sender, EventArgs e)
    {
        this.Loaded -= this.OnLoadedEnsureOnScreen;
        this.EnsureOnScreen();
    }

    private void OnLoadedPositionNearTray(object? sender, EventArgs e)
    {
        this.Loaded -= this.OnLoadedPositionNearTray;
        this.PositionNearTray();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        this.hideTimer.Stop();
        this.Focus();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        this.hideTimer.Stop();
        this.SaveWindowState();
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:
                    this.ApplyZoom(this.zoomLevel + ZoomStep);
                    e.Handled = true;
                    return;
                case Key.OemMinus or Key.Subtract:
                    this.ApplyZoom(this.zoomLevel - ZoomStep);
                    e.Handled = true;
                    return;
                case Key.D0 or Key.NumPad0:
                    this.ApplyZoom(1.0);
                    e.Handled = true;
                    return;
            }
        }

        base.OnKeyDown(e);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        this.ApplyZoom(this.zoomLevel + (e.Delta > 0 ? ZoomStep : -ZoomStep));
        e.Handled = true;
    }

    private void ApplyZoom(double newZoom)
    {
        this.zoomLevel = Math.Clamp(newZoom, MinZoom, MaxZoom);
        this.ZoomTransform.ScaleX = this.zoomLevel;
        this.ZoomTransform.ScaleY = this.zoomLevel;
    }

    private void PositionNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        this.Left = Math.Max(0, workArea.Right - this.ActualWidth - 8);
        this.Top = Math.Max(0, workArea.Bottom - this.ActualHeight - 8);
    }

    private void EnsureOnScreen()
    {
        var workArea = SystemParameters.WorkArea;
        if (this.Left + this.ActualWidth > workArea.Right)
        {
            this.Left = Math.Max(workArea.Left, workArea.Right - this.ActualWidth);
        }

        if (this.Top + this.ActualHeight > workArea.Bottom)
        {
            this.Top = Math.Max(workArea.Top, workArea.Bottom - this.ActualHeight);
        }

        if (this.Left < workArea.Left)
        {
            this.Left = workArea.Left;
        }

        if (this.Top < workArea.Top)
        {
            this.Top = workArea.Top;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        this.hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        this.hwndSource?.AddHook(this.WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WMNCHITTEST:
                return this.HandleNcHitTest(lParam, ref handled);
            case WMSETCURSOR:
                if (LowWord((long)lParam) == HTCAPTION)
                {
                    SetCursor(LoadCursor(IntPtr.Zero, IDCSIZEALL));
                    handled = true;
                    return (IntPtr)1;
                }

                break;
            case WMENTERSIZEMOVE:
                this.isDragging = true;
                this.hasUserPosition = true;
                this.hideTimer.Stop();
                break;
            case WMEXITSIZEMOVE:
                this.isDragging = false;
                this.dragEndedAtUtc = DateTime.UtcNow;
                this.SaveWindowState();
                break;
        }

        return IntPtr.Zero;
    }

    private IntPtr HandleNcHitTest(IntPtr lParam, ref bool handled)
    {
        if (this.hwndSource?.CompositionTarget == null)
        {
            return IntPtr.Zero;
        }

        var screenX = (short)(lParam.ToInt64() & 0xFFFF);
        var screenY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

        var transform = this.hwndSource.CompositionTarget.TransformFromDevice;
        var wpfScreenPoint = transform.Transform(new Point(screenX, screenY));
        var windowPoint = this.PointFromScreen(wpfScreenPoint);

        var result = VisualTreeHelper.HitTest(this, windowPoint);
        if (result != null)
        {
            var visual = result.VisualHit;
            while (visual != null)
            {
                if (visual == this.TitleBarBorder)
                {
                    handled = true;
                    return (IntPtr)HTCAPTION;
                }

                visual = VisualTreeHelper.GetParent(visual);
            }
        }

        return IntPtr.Zero;
    }

    private static short LowWord(long value) => (short)(value & 0xFFFF);

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (this.isDragging)
        {
            return;
        }

        if ((DateTime.UtcNow - this.dragEndedAtUtc) < DragCooldown)
        {
            return;
        }

        this.hideTimer.Stop();
        this.hideTimer.Start();
    }

    private void HideTimer_Tick(object? sender, EventArgs e)
    {
        this.hideTimer.Stop();
        if (this.IsActive || this.isDragging)
        {
            return;
        }

        this.SaveWindowState();
        this.Hide();
    }

    /// <summary>Persists window geometry to disk. Safe to call multiple times.</summary>
    internal void SaveWindowState()
    {
        try
        {
            // Use in-memory savedLeft/savedTop instead of WPF Left/Top to avoid
            // persisting stale values from hidden or uninitialized windows.
            var effectiveLeft = this.hasUserPosition ? (this.savedLeft ?? this.Left) : this.Left;
            var effectiveTop = this.hasUserPosition ? (this.savedTop ?? this.Top) : this.Top;

            var appSettings = this.settings.Load();
            appSettings.ZoomLevel = this.zoomLevel;
            appSettings.WindowWidth = this.lastWidth;
            appSettings.WindowHeight = this.lastHeight;
            appSettings.WindowLeft = effectiveLeft;
            appSettings.WindowTop = effectiveTop;
            this.savedLeft = effectiveLeft;
            this.savedTop = effectiveTop;
            this.hasUserPosition = true;

            this.settings.Save(appSettings);
        }
        catch
        {
            // Best-effort — don't crash on save failure
        }
    }
}
