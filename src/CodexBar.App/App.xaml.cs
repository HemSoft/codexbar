using System.Drawing;
using System.Windows;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        _refreshService = _services.GetRequiredService<UsageRefreshService>();

        InitializeTrayIcon();

        _refreshService.Start();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddHttpClient();

        services.AddSingleton<IUsageProvider, ClaudeProvider>();
        services.AddSingleton<IUsageProvider, GeminiProvider>();
        services.AddSingleton<IUsageProvider, OpenRouterProvider>();
        services.AddSingleton<IUsageProvider, CopilotProvider>();

        services.AddSingleton<UsageRefreshService>();
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
        var window = Current.Windows.OfType<MainWindow>().FirstOrDefault();
        if (window is null)
        {
            window = new MainWindow();
            if (_refreshService is not null)
                window.DataContext = _refreshService;
        }
        window.Show();
        window.Activate();
    }

    private static Icon CreateDefaultIcon()
    {
        // Generate a simple 16x16 icon programmatically
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);

        // Draw two meter bars (matching CodexBar's icon concept)
        using var topBrush = new SolidBrush(Color.FromArgb(255, 99, 102, 241)); // indigo
        using var bottomBrush = new SolidBrush(Color.FromArgb(180, 99, 102, 241));
        g.FillRectangle(topBrush, 2, 3, 12, 4);
        g.FillRectangle(bottomBrush, 2, 9, 12, 4);

        var handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _refreshService?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }
}
