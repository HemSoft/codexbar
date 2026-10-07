// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class ProviderConfigurationWindow : Window
{
    public ProviderConfigurationWindow()
    {
        this.InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var target = new WindowInteropHelper(this.Owner ?? this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var workArea = SystemParameters.WorkArea;
        if (GetMonitorInfo(MonitorFromWindow(target, 2), ref info))
        {
            var dpi = GetDpiForWindow(target);
            var scale = dpi == 0 ? 1 : 96.0 / dpi;
            workArea = new Rect(info.Work.Left * scale, info.Work.Top * scale,
                (info.Work.Right - info.Work.Left) * scale, (info.Work.Bottom - info.Work.Top) * scale);
        }

        this.FitToWorkArea(workArea);
    }

    internal void FitToWorkArea(Rect workArea)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0)
        {
            return;
        }

        const double padding = 8;
        var width = Math.Max(1, workArea.Width - (padding * 2));
        var height = Math.Max(1, workArea.Height - (padding * 2));
        this.MinWidth = Math.Min(520, width);
        this.MinHeight = Math.Min(340, height);
        this.MaxWidth = width;
        this.MaxHeight = height;
        this.Width = Math.Min(this.Width, width);
        this.Height = Math.Min(this.Height, height);
        var centerX = this.Owner is { } owner ? owner.Left + (owner.ActualWidth / 2) : workArea.Left + (workArea.Width / 2);
        var centerY = this.Owner is { } parent ? parent.Top + (parent.ActualHeight / 2) : workArea.Top + (workArea.Height / 2);
        this.WindowStartupLocation = WindowStartupLocation.Manual;
        this.Left = workArea.Left + Math.Clamp(centerX - (this.Width / 2) - workArea.Left, 0, Math.Max(0, workArea.Width - this.Width));
        this.Top = workArea.Top + Math.Clamp(centerY - (this.Height / 2) - workArea.Top, 0, Math.Max(0, workArea.Height - this.Height));
    }

    private void OnErrorMessageTargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (sender is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
        {
            var peer = UIElementAutomationPeer.FromElement(textBlock) ?? UIElementAutomationPeer.CreatePeerForElement(textBlock);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}
