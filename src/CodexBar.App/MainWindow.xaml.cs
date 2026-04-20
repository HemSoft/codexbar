using System.Windows;
using System.Windows.Input;

namespace CodexBar.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // SizeToContent computes ActualHeight after layout; defer positioning
        Loaded += (_, _) => PositionNearTray();
        // Re-position when dynamic content (e.g., Copilot account cards) changes the height
        SizeChanged += (_, _) => { if (IsLoaded) PositionNearTray(); };
    }

    private void PositionNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(0, workArea.Right - ActualWidth - 8);
        Top = Math.Max(0, workArea.Bottom - ActualHeight - 8);
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        Hide();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // Allow dragging
        base.OnMouseLeftButtonDown(e);
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}