using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using CodexBar.App.ViewModels;
using CodexBar.Core.Configuration;
using CodexBar.Core.Providers;
using CodexBar.Core.Providers.Claude;
using CodexBar.Core.Providers.Copilot;
using CodexBar.Core.Providers.Gemini;
using CodexBar.Core.Providers.OpenRouter;
using CodexBar.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexBar.App;

public partial class App : Application
{
    private H.NotifyIcon.TaskbarIcon? _trayIcon;
    private ServiceProvider? _services;
    private UsageRefreshService? _refreshService;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        _refreshService = _services.GetRequiredService<UsageRefreshService>();
        _viewModel = _services.GetRequiredService<MainViewModel>();

        InitializeTrayIcon();

        _refreshService.Start();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddHttpClient();

        services.AddSingleton<SettingsService>();

        services.AddSingleton<IUsageProvider, ClaudeProvider>();
        services.AddSingleton<IUsageProvider, GeminiProvider>();
        services.AddSingleton<IUsageProvider, OpenRouterProvider>();
        services.AddSingleton<IUsageProvider, CopilotProvider>();

        services.AddSingleton<UsageRefreshService>();
        services.AddSingleton<MainViewModel>();
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new H.NotifyIcon.TaskbarIcon
        {
            ToolTipText = "CodexBar — AI Usage Monitor",
            Icon = CreateDefaultIcon(),
            ContextMenu = CreateContextMenu()
        };
        _trayIcon.TrayLeftMouseUp += (_, _) => ShowPopup();
        _trayIcon.ForceCreate();
    }

    private System.Windows.Controls.ContextMenu CreateContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var refreshItem = new System.Windows.Controls.MenuItem { Header = "Refresh Now" };
        refreshItem.Click += async (_, _) =>
        {
            if (_refreshService is not null)
                await _refreshService.RefreshAllAsync();
        };
        menu.Items.Add(refreshItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ShowPopup()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow { DataContext = _viewModel };
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private static Icon CreateDefaultIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);

        using var topBrush = new SolidBrush(Color.FromArgb(255, 99, 102, 241));
        using var bottomBrush = new SolidBrush(Color.FromArgb(180, 99, 102, 241));
        g.FillRectangle(topBrush, 2, 3, 12, 4);
        g.FillRectangle(bottomBrush, 2, 9, 12, 4);

        // Clone to create an owned icon — FromHandle doesn't take ownership.
        // DestroyIcon must be called to free the unmanaged HICON from GetHicon().
        var handle = bmp.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(handle);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _trayIcon?.Dispose();
        _refreshService?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }
}
