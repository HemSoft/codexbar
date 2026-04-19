using System.Windows;
using System.Windows.Input;

namespace CodexBar.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PositionNearTray();
    }

    private void PositionNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 8;
        Top = workArea.Bottom - Height - 8;
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