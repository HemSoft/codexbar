// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App;

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.App.ViewModels;
using CodexBar.Core.Configuration;
using CodexBar.Core.Providers;
using CodexBar.Core.Services;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class MainWindow : Window
{
    private const int WMNCHITTEST = 0x0084;
    private const int WMNCLBUTTONDOWN = 0x00A1;
    private const int WMENTERSIZEMOVE = 0x0231;
    private const int WMEXITSIZEMOVE = 0x0232;
    private const int WMSETCURSOR = 0x0020;
    private const int WMMOVE = 0x0003;
    private const int HTCAPTION = 2;
    private const int HTBOTTOMRIGHT = 17;
    private const uint MONITORDEFAULTTONEAREST = 2;
    private const uint SWPNOSIZE = 0x0001;
    private const uint SWPNOZORDER = 0x0004;
    private const uint SWPNOACTIVATE = 0x0010;
    internal const double WorkAreaEdgePadding = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private static readonly IntPtr IDCSIZEALL = new IntPtr(32646);

    /// <summary>Duration after a drag ends during which Deactivated is suppressed.</summary>
    private static readonly TimeSpan DragCooldown = TimeSpan.FromMilliseconds(500);

    /// <summary>Delay before hiding the window on deactivation, allowing reactivation to cancel.</summary>
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(150);

    private readonly SettingsService settings;
    private readonly IReadOnlyList<IUsageProvider> providers;
    private readonly UsageRefreshService? refreshService;
    private readonly DispatcherTimer hideTimer;
    private double zoomLevel = 1.0;
    private double lastWidth;
    private double? lastHeight;
    private Point providerCardDragStart;
    private ProviderCardViewModel? providerCardDragCandidate;

    /// <summary>Saved physical pixel X coordinate (from GetWindowRect). Null = no saved position.</summary>
    private int? physicalLeft;

    /// <summary>Saved physical pixel Y coordinate (from GetWindowRect). Null = no saved position.</summary>
    private int? physicalTop;

    /// <summary>Last position received via WM_MOVE during a drag. Used to commit the final position.</summary>
    private int lastDragX;
    private int lastDragY;

    private bool isDragging;
    private bool isResizeInProgress;
    private bool hasManualWindowHeight;
    private bool isConfigurationOpen;
    private DateTime dragEndedAtUtc = DateTime.MinValue;
    private HwndSource? hwndSource;

    public MainWindow(SettingsService settings, IEnumerable<IUsageProvider>? providers = null, UsageRefreshService? refreshService = null)
    {
        this.settings = settings;
        this.providers = providers?.ToList() ?? [];
        this.refreshService = refreshService;
        this.InitializeComponent();

        this.hideTimer = new DispatcherTimer { Interval = HideDelay };
        this.hideTimer.Tick += this.HideTimer_Tick;

        var appSettings = this.settings.Load();
        this.zoomLevel = ZoomHelper.ClampZoom(appSettings.ZoomLevel);
        this.ZoomTransform.ScaleX = this.zoomLevel;
        this.ZoomTransform.ScaleY = this.zoomLevel;

        if (appSettings.WindowWidth is > 0)
        {
            this.Width = appSettings.WindowWidth.Value;
        }

        if (appSettings.WindowHeight is > 0)
        {
            this.SizeToContent = SizeToContent.Manual;
            this.Height = appSettings.WindowHeight.Value;
            this.lastHeight = appSettings.WindowHeight.Value;
            this.hasManualWindowHeight = true;
        }

        this.lastWidth = appSettings.WindowWidth ?? this.Width;

        // Store saved physical pixel position for later use in Loaded handler.
        // Also set WPF Left/Top as a best-effort initial hint (exact at 100% DPI).
        if (appSettings.WindowLeft is not null && appSettings.WindowTop is not null)
        {
            this.physicalLeft = (int)appSettings.WindowLeft.Value;
            this.physicalTop = (int)appSettings.WindowTop.Value;
            this.Left = appSettings.WindowLeft.Value;
            this.Top = appSettings.WindowTop.Value;
            LogPosition($"CTOR: Loaded from settings → physicalLeft={this.physicalLeft}, physicalTop={this.physicalTop}, WPF Left={this.Left}, Top={this.Top}");
        }
        else
        {
            LogPosition("CTOR: No saved position in settings");
        }

        this.SizeChanged += this.OnSizeChanged;
        this.PreviewMouseWheel += this.OnPreviewMouseWheel;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (this.IsLoaded && this.IsVisible)
        {
            this.lastWidth = this.ActualWidth;
            if (this.hasManualWindowHeight)
            {
                this.lastHeight = this.ActualHeight;
            }

            this.EnsureOnScreen();
        }
    }

    /// <summary>
    /// Called by App.ShowPopup() before Show() to set position.
    /// On re-show (HWND exists), applies position directly via SetWindowPos.
    /// On first show, defers to OnSourceInitialized/Loaded.
    /// </summary>
    public void RestoreState()
    {
        this.Width = this.lastWidth;
        this.ZoomTransform.ScaleX = this.zoomLevel;
        this.ZoomTransform.ScaleY = this.zoomLevel;

        var hwnd = new WindowInteropHelper(this).Handle;
        LogPosition($"RESTORE: physicalLeft={this.physicalLeft}, physicalTop={this.physicalTop}, hwnd={hwnd}");
        if (this.physicalLeft is not null && this.physicalTop is not null)
        {
            if (hwnd != IntPtr.Zero)
            {
                // Re-show: HWND exists, position directly in physical pixels.
                SetWindowPos(hwnd, IntPtr.Zero, this.physicalLeft.Value, this.physicalTop.Value, 0, 0, SWPNOSIZE | SWPNOZORDER | SWPNOACTIVATE);
                LogPosition($"RESTORE: Re-show SetWindowPos({this.physicalLeft}, {this.physicalTop})");
                this.EnsureOnScreen();
            }
            else
            {
                // First show: set approximate WPF Left/Top; correct in Loaded via SetWindowPos.
                this.Left = this.physicalLeft.Value;
                this.Top = this.physicalTop.Value;
                LogPosition($"RESTORE: First show, set WPF Left={this.Left}, Top={this.Top}, hooking Loaded");
                this.Loaded += this.OnLoadedEnsureOnScreen;
            }
        }
        else
        {
            if (hwnd != IntPtr.Zero)
            {
                this.PositionNearTray();
            }
            else
            {
                this.Loaded += this.OnLoadedPositionNearTray;
            }
        }
    }

    private void OnLoadedEnsureOnScreen(object? sender, EventArgs e)
    {
        this.Loaded -= this.OnLoadedEnsureOnScreen;

        // Apply exact physical pixel position AFTER WPF has finished its own
        // DPI-based positioning. OnSourceInitialized is too early — WPF overrides it.
        if (this.physicalLeft is not null && this.physicalTop is not null)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // Log position BEFORE SetWindowPos
                GetWindowRect(hwnd, out var beforeRect);
                LogPosition($"LOADED: Before SetWindowPos → GetWindowRect=({beforeRect.Left},{beforeRect.Top}), target=({this.physicalLeft},{this.physicalTop})");

                SetWindowPos(hwnd, IntPtr.Zero, this.physicalLeft.Value, this.physicalTop.Value, 0, 0, SWPNOSIZE | SWPNOZORDER | SWPNOACTIVATE);

                // Log position AFTER SetWindowPos
                GetWindowRect(hwnd, out var afterRect);
                LogPosition($"LOADED: After SetWindowPos → GetWindowRect=({afterRect.Left},{afterRect.Top})");
            }
        }

        this.EnsureOnScreen();

        // Log final position
        var finalHwnd = new WindowInteropHelper(this).Handle;
        if (finalHwnd != IntPtr.Zero && GetWindowRect(finalHwnd, out var finalRect))
        {
            LogPosition($"LOADED: Final after EnsureOnScreen → GetWindowRect=({finalRect.Left},{finalRect.Top}), WPF Left={this.Left}, Top={this.Top}");
        }
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
        var zoomKey = e.Key switch
        {
            Key.OemPlus or Key.Add => ZoomKey.ZoomIn,
            Key.OemMinus or Key.Subtract => ZoomKey.ZoomOut,
            Key.D0 or Key.NumPad0 => ZoomKey.ResetZoom,
            _ => ZoomKey.Other,
        };

        var isCtrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var result = ZoomHelper.EvaluateKeyInput(this.zoomLevel, isCtrlHeld, zoomKey);
        if (result is not null)
        {
            this.ApplyZoom(result.Value.NewZoom);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        this.ApplyZoom(ZoomHelper.ClampZoom(this.zoomLevel + (e.Delta > 0 ? ZoomHelper.ZoomStep : -ZoomHelper.ZoomStep)));
        e.Handled = true;
    }

    private void ApplyZoom(double newZoom)
    {
        this.zoomLevel = ZoomHelper.ClampZoom(newZoom);
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
        this.UpdateMaxHeightFromCurrentMonitor();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!GetWindowRect(hwnd, out var windowRect))
        {
            return;
        }

        var hMonitor = MonitorFromWindow(hwnd, MONITORDEFAULTTONEAREST);
        var info = new MONITORINFO { CbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref info))
        {
            return;
        }

        var clamped = ClampToWorkArea(windowRect, info.RcWork);
        if (clamped.Left != windowRect.Left || clamped.Top != windowRect.Top)
        {
            SetWindowPos(hwnd, IntPtr.Zero, clamped.Left, clamped.Top, 0, 0, SWPNOSIZE | SWPNOZORDER | SWPNOACTIVATE);
        }

        // Always update physical position from actual HWND state.
        if (GetWindowRect(hwnd, out var finalRect))
        {
            this.physicalLeft = finalRect.Left;
            this.physicalTop = finalRect.Top;
        }
    }

    /// <summary>
    /// Pure geometry helper: clamps a window rect to stay within a work area.
    /// Both rects are in the same coordinate system (physical pixels).
    /// </summary>
    internal static RECT ClampToWorkArea(RECT windowRect, RECT workArea)
    {
        int windowWidth = windowRect.Right - windowRect.Left;
        int windowHeight = windowRect.Bottom - windowRect.Top;
        int newX = windowRect.Left;
        int newY = windowRect.Top;

        if (newX + windowWidth > workArea.Right)
        {
            newX = Math.Max(workArea.Left, workArea.Right - windowWidth);
        }

        if (newY + windowHeight > workArea.Bottom)
        {
            newY = Math.Max(workArea.Top, workArea.Bottom - windowHeight);
        }

        if (newX < workArea.Left)
        {
            newX = workArea.Left;
        }

        if (newY < workArea.Top)
        {
            newY = workArea.Top;
        }

        return new RECT { Left = newX, Top = newY, Right = newX + windowWidth, Bottom = newY + windowHeight };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        this.hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        this.hwndSource?.AddHook(this.WndProc);
        this.UpdateMaxHeightFromCurrentMonitor();
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
                this.hideTimer.Stop();
                LogPosition("DRAG: WM_ENTERSIZEMOVE received — drag started");
                break;
            case WMEXITSIZEMOVE:
                this.isDragging = false;
                this.dragEndedAtUtc = DateTime.UtcNow;

                if (this.isResizeInProgress)
                {
                    this.SaveCurrentWindowRect();
                    this.isResizeInProgress = false;
                    LogPosition($"RESIZE: WM_EXITSIZEMOVE — saved size ({this.lastWidth},{this.lastHeight})");
                }
                else
                {
                    // WPF layered windows (AllowsTransparency=True) never commit the final
                    // drag position to the native HWND — GetWindowRect returns stale pre-drag
                    // coordinates. Track the real position from the last WM_MOVE instead.
                    this.physicalLeft = this.lastDragX;
                    this.physicalTop = this.lastDragY;
                    LogPosition($"DRAG: WM_EXITSIZEMOVE — saved position ({this.lastDragX},{this.lastDragY})");
                }

                this.SaveWindowState();
                break;
            case WMMOVE:
                var moveX = (short)(lParam.ToInt64() & 0xFFFF);
                var moveY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                if (this.isDragging)
                {
                    this.lastDragX = moveX;
                    this.lastDragY = moveY;
                }

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

    private void ResizeGrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        e.Handled = true;
        this.BeginManualResize();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(hwnd, WMNCLBUTTONDOWN, (IntPtr)HTBOTTOMRIGHT, IntPtr.Zero);
    }

    private void BeginManualResize()
    {
        this.UpdateMaxHeightFromCurrentMonitor();

        if (this.ActualWidth > 0)
        {
            this.Width = this.ActualWidth;
            this.lastWidth = this.ActualWidth;
        }

        if (this.ActualHeight > 0)
        {
            this.Height = this.ActualHeight;
            this.lastHeight = this.ActualHeight;
        }

        this.SizeToContent = SizeToContent.Manual;
        this.hasManualWindowHeight = true;
        this.isResizeInProgress = true;
        this.hideTimer.Stop();
    }

    private void SaveCurrentWindowRect()
    {
        this.UpdateMaxHeightFromCurrentMonitor();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
        {
            this.physicalLeft = rect.Left;
            this.physicalTop = rect.Top;
        }

        this.lastWidth = this.ActualWidth;
        this.lastHeight = this.ActualHeight;
    }

    private void UpdateMaxHeightFromCurrentMonitor()
    {
        var maxHeight = this.TryGetCurrentMonitorMaxHeight();
        if (maxHeight is null)
        {
            return;
        }

        this.MaxHeight = maxHeight.Value;
        if (this.hasManualWindowHeight && !double.IsNaN(this.Height) && this.Height > maxHeight.Value)
        {
            this.Height = maxHeight.Value;
            this.lastHeight = maxHeight.Value;
        }
    }

    private double? TryGetCurrentMonitorMaxHeight()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || this.hwndSource?.CompositionTarget is null)
        {
            return null;
        }

        var hMonitor = MonitorFromWindow(hwnd, MONITORDEFAULTTONEAREST);
        var info = new MONITORINFO { CbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref info))
        {
            return null;
        }

        var workAreaHeightPixels = info.RcWork.Bottom - info.RcWork.Top;
        return CalculateMaxHeightFromWorkArea(
            workAreaHeightPixels,
            this.hwndSource.CompositionTarget.TransformFromDevice,
            this.MinHeight,
            WorkAreaEdgePadding);
    }

    internal static double CalculateMaxHeightFromWorkArea(
        int workAreaHeightPixels,
        Matrix transformFromDevice,
        double minHeight,
        double padding)
    {
        if (workAreaHeightPixels <= 0)
        {
            return minHeight;
        }

        var workAreaHeight = transformFromDevice.Transform(new Vector(0, workAreaHeightPixels)).Y;
        return Math.Max(minHeight, workAreaHeight - padding);
    }

    private static short LowWord(long value) => (short)(value & 0xFFFF);

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (this.isConfigurationOpen)
        {
            return;
        }

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

    private void ProviderCardDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        this.providerCardDragStart = e.GetPosition(this);
        this.providerCardDragCandidate = (sender as FrameworkElement)?.DataContext as ProviderCardViewModel;
        Mouse.Capture(sender as IInputElement);
        e.Handled = true;
    }

    private void ProviderCardDragHandle_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Mouse.Captured == sender)
        {
            Mouse.Capture(null);
        }

        this.providerCardDragCandidate = null;
    }

    private void ProviderCardDragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || this.providerCardDragCandidate is null)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - this.providerCardDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - this.providerCardDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(typeof(ProviderCardViewModel), this.providerCardDragCandidate);
        Mouse.Capture(null);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        this.providerCardDragCandidate = null;
        e.Handled = true;
    }

    private void ProviderCardContainer_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is DependencyObject container && FindVisualChildByName<FrameworkElement>(container, "ProviderCardDragHandle") is { } handle)
        {
            handle.Opacity = 0.9;
        }
    }

    private void ProviderCardContainer_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is DependencyObject container && FindVisualChildByName<FrameworkElement>(container, "ProviderCardDragHandle") is { } handle)
        {
            handle.Opacity = 0;
        }
    }

    private void ProviderCard_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanDropProviderCard(sender, e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void ProviderCard_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!CanDropProviderCard(sender, e) ||
            this.DataContext is not MainViewModel viewModel ||
            !e.Data.GetDataPresent(typeof(ProviderCardViewModel)) ||
            e.Data.GetData(typeof(ProviderCardViewModel)) is not ProviderCardViewModel moved ||
            (sender as FrameworkElement)?.DataContext is not ProviderCardViewModel target)
        {
            return;
        }

        var insertAfter = e.GetPosition((IInputElement)sender).Y > ((FrameworkElement)sender).ActualHeight / 2;
        viewModel.MoveProviderCard(moved.CardKey, target.CardKey, insertAfter);
    }

    private static bool CanDropProviderCard(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ProviderCardViewModel)) ||
            e.Data.GetData(typeof(ProviderCardViewModel)) is not ProviderCardViewModel moved ||
            (sender as FrameworkElement)?.DataContext is not ProviderCardViewModel target)
        {
            return false;
        }

        return !moved.IsHiddenCompanion &&
               !target.IsHiddenCompanion &&
               !string.Equals(moved.CardKey, target.CardKey, StringComparison.OrdinalIgnoreCase);
    }

    private static T? FindVisualChildByName<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && element.Name == name)
            {
                return element;
            }

            var match = FindVisualChildByName<T>(child, name);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        this.OpenConfigurationWindow();
    }

    private void OpenConfigurationWindow()
    {
        if (this.isConfigurationOpen)
        {
            return;
        }

        this.hideTimer.Stop();
        this.isConfigurationOpen = true;

        var window = new ProviderConfigurationWindow
        {
            Owner = this,
        };
        var currentProviderCards = (this.DataContext as MainViewModel)?.Providers;
        var viewModel = new ProviderConfigurationViewModel(this.settings, this.providers, window.Close, currentProviderCards);
        viewModel.Saved += async (_, _) =>
        {
            if (this.DataContext is MainViewModel mainViewModel)
            {
                mainViewModel.ReloadProviderVisibility();
            }

            if (this.refreshService is not null)
            {
                await this.refreshService.RefreshAllAsync();
            }
        };

        window.DataContext = viewModel;
        window.Closed += (_, _) =>
        {
            this.isConfigurationOpen = false;
            this.Activate();
        };
        window.ShowDialog();
    }

    /// <summary>Persists window geometry to disk using physical pixel coordinates from GetWindowRect.</summary>
    internal void SaveWindowState()
    {
        try
        {
            // physicalLeft / physicalTop are maintained by:
            //   - Constructor: loaded from persisted settings
            //   - WM_EXITSIZEMOVE: updated from the last WM_MOVE coordinates
            // We do NOT read from GetWindowRect because it returns stale values
            // for WPF layered windows (AllowsTransparency=True).
            if (this.physicalLeft == null || this.physicalTop == null)
            {
                // Fallback for test scenarios where no HWND/drag ever occurred.
                if (!double.IsNaN(this.Left) && !double.IsNaN(this.Top))
                {
                    this.physicalLeft = (int)this.Left;
                    this.physicalTop = (int)this.Top;
                    LogPosition($"SAVE: Fallback from WPF Left/Top → ({this.physicalLeft},{this.physicalTop})");
                }
            }

            var appSettings = this.settings.Load();
            appSettings.ZoomLevel = this.zoomLevel;
            appSettings.WindowWidth = this.lastWidth;
            appSettings.WindowHeight = this.hasManualWindowHeight ? this.lastHeight : null;
            appSettings.WindowLeft = this.physicalLeft;
            appSettings.WindowTop = this.physicalTop;

            this.settings.Save(appSettings);
            LogPosition($"SAVE: Written to settings → WindowLeft={appSettings.WindowLeft}, WindowTop={appSettings.WindowTop}");
        }
        catch (Exception ex)
        {
            LogPosition($"SAVE: EXCEPTION → {ex.Message}");
        }
    }

    private static void LogPosition(string message)
    {
        try
        {
            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codexbar", "position-debug.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging failures
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MONITORINFO
    {
        public int CbSize;
        public RECT RcMonitor;
        public RECT RcWork;
        public uint DwFlags;
    }
}
