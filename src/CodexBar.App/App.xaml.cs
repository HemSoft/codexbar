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
using CodexBar.Core.Providers.OpenRouter;
using CodexBar.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public partial class App : Application
{
    private H.NotifyIcon.TaskbarIcon? trayIcon;
    private ServiceProvider? services;
    private UsageRefreshService? refreshService;
    private MainViewModel? viewModel;
    private MainWindow? mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        this.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var services = new ServiceCollection();
        ConfigureServices(services);
        this.services = services.BuildServiceProvider();

        this.refreshService = this.services.GetRequiredService<UsageRefreshService>();
        this.viewModel = this.services.GetRequiredService<MainViewModel>();

        this.InitializeTrayIcon();

        this.refreshService.Start();
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

        services.AddSingleton<UsageRefreshService>();
        services.AddSingleton<MainViewModel>();
    }

    private void InitializeTrayIcon()
    {
        this.trayIcon = new H.NotifyIcon.TaskbarIcon
        {
            ToolTipText = "CodexBar — AI Usage Monitor",
            Icon = CreateDefaultIcon(),
            ContextMenu = this.CreateContextMenu(),
        };
        this.trayIcon.TrayMouseMove += (_, _) => this.ShowPopup();
        this.trayIcon.ForceCreate();
    }

    private System.Windows.Controls.ContextMenu CreateContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var refreshItem = new System.Windows.Controls.MenuItem { Header = "Refresh Now" };
        refreshItem.Click += async (_, _) =>
        {
            if (this.refreshService is not null)
            {
                await this.refreshService.RefreshAllAsync();
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
            this.mainWindow?.SaveWindowState();
            this.Shutdown();
        };
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ShowPopup()
    {
        try
        {
            if (this.mainWindow is { IsVisible: true })
            {
                return;
            }

            var settingsService = this.services!.GetRequiredService<SettingsService>();
            if (this.mainWindow is null)
            {
                this.mainWindow = new MainWindow(settingsService) { DataContext = this.viewModel };
                this.mainWindow.Closed += (_, _) => this.mainWindow = null;
            }

            this.mainWindow.Show();
            this.mainWindow.RestoreState();
            this.mainWindow.Activate();
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
        this.mainWindow?.SaveWindowState();

        // Only dispose non-DI resources manually. _viewModel and _refreshService
        // are DI singletons — ServiceProvider.Dispose() handles their lifetime.
        this.trayIcon?.Dispose();
        this.services?.Dispose();
        base.OnExit(e);
    }
}
