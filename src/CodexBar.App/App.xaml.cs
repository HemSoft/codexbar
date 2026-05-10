// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App;

using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CodexBar.App.ViewModels;
using CodexBar.Core.Configuration;
using CodexBar.Core.Providers;
using CodexBar.Core.Providers.Claude;
using CodexBar.Core.Providers.Copilot;
using CodexBar.Core.Providers.OpenCodeGo;
using CodexBar.Core.Providers.OpenCodeZen;
using CodexBar.Core.Providers.OpenRouter;
using CodexBar.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        this.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var services = new ServiceCollection();
        ConfigureServices(services);
        this._services = services.BuildServiceProvider();

        this._refreshService = this._services.GetRequiredService<UsageRefreshService>();
        this._viewModel = this._services.GetRequiredService<MainViewModel>();

        this.InitializeTrayIcon();

        this._refreshService.Start();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Debug.WriteLine($"[CodexBar] Unhandled dispatcher exception: {e.Exception}");
        e.Handled = true; // Prevent crash — keep tray icon alive
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) => Debug.WriteLine($"[CodexBar] Unhandled domain exception: {e.ExceptionObject}");

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(b => b.AddConsole().AddFile().SetMinimumLevel(LogLevel.Debug));
        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(b =>
            b.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15)));

        services.AddSingleton<SettingsService>();
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());

        services.AddSingleton<IUsageProvider, OpenRouterProvider>();
        services.AddSingleton<IUsageProvider, CopilotProvider>();
        services.AddSingleton<IUsageProvider, ClaudeProvider>();
        services.AddSingleton<IUsageProvider, OpenCodeGoProvider>();
        services.AddSingleton<IUsageProvider, OpenCodeZenProvider>();

        services.AddSingleton<UsageRefreshService>();
        services.AddSingleton<MainViewModel>();
    }

    private void InitializeTrayIcon()
    {
        this._trayIcon = new H.NotifyIcon.TaskbarIcon
        {
            ToolTipText = "CodexBar — AI Usage Monitor",
            Icon = CreateDefaultIcon(),
            ContextMenu = this.CreateContextMenu(),
        };
        this._trayIcon.TrayMouseMove += (_, _) => this.ShowPopup();
        this._trayIcon.ForceCreate();
    }

    private System.Windows.Controls.ContextMenu CreateContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var refreshItem = new System.Windows.Controls.MenuItem { Header = "Refresh Now" };
        refreshItem.Click += async (_, _) =>
        {
            if (this._refreshService is not null)
            {
                await this._refreshService.RefreshAllAsync();
            }
        };
        menu.Items.Add(refreshItem);

        var startupItem = new System.Windows.Controls.MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = StartupManager.IsEnabled(),
        };
        startupItem.Click += (_, _) =>
        {
            try
            {
                StartupManager.SetEnabled(startupItem.IsChecked);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CodexBar] Failed to set startup: {ex.Message}");
                startupItem.IsChecked = StartupManager.IsEnabled();
            }
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) =>
        {
            this._mainWindow?.SaveWindowState();

            // Close context menu and remove tray icon BEFORE shutdown
            // to prevent a ghost icon/menu lingering in the system tray.
            if (this._trayIcon != null)
            {
                if (this._trayIcon.ContextMenu is { IsOpen: true } ctx)
                {
                    ctx.IsOpen = false;
                }

                this._trayIcon.Visibility = System.Windows.Visibility.Collapsed;
                this._trayIcon.Dispose();
                this._trayIcon = null;
            }

            this.Shutdown();
        };
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ShowPopup()
    {
        try
        {
            if (this._mainWindow is { IsVisible: true })
            {
                return;
            }

            var settingsService = this._services!.GetRequiredService<SettingsService>();
            if (this._mainWindow is null)
            {
                this._mainWindow = new MainWindow(settingsService) { DataContext = this._viewModel };
                this._mainWindow.Closed += (_, _) => this._mainWindow = null;
            }

            this._mainWindow.RestoreState();
            this._mainWindow.Show();
            this._mainWindow.Activate();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodexBar] ShowPopup error: {ex.Message}");
        }
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
        this._mainWindow?.SaveWindowState();

        if (this._trayIcon != null)
        {
            if (this._trayIcon.ContextMenu is { IsOpen: true } menu)
            {
                menu.IsOpen = false;
            }

            this._trayIcon.Visibility = System.Windows.Visibility.Collapsed;
            this._trayIcon.Dispose();
            this._trayIcon = null;
        }

        this._services?.Dispose();
        base.OnExit(e);
    }
}
