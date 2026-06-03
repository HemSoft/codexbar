// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App;

using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CodexBar.App.ViewModels;
using CodexBar.Core.Configuration;
using CodexBar.Core.Providers;
using CodexBar.Core.Providers.Claude;
using CodexBar.Core.Providers.Codex;
using CodexBar.Core.Providers.Copilot;
using CodexBar.Core.Providers.Cursor;
using CodexBar.Core.Providers.OpenCodeGo;
using CodexBar.Core.Providers.OpenCodeZen;
using CodexBar.Core.Providers.OpenRouter;
using CodexBar.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class App : Application
{
    private H.NotifyIcon.TaskbarIcon? _trayIcon;
    private ServiceProvider? _services;
    private UsageRefreshService? _refreshService;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private bool _shutdownRequested;

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
        services.AddSingleton<IUsageProvider, CodexProvider>();
        services.AddSingleton<IUsageProvider, CursorProvider>();
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
        this._trayIcon.TrayMouseMove += this.TrayIcon_TrayMouseMove;
        this._trayIcon.ForceCreate();
    }

    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu();

        var refreshItem = new MenuItem { Header = "Refresh Now" };
        refreshItem.Click += async (_, _) =>
        {
            if (this._refreshService is not null)
            {
                await this._refreshService.RefreshAllAsync();
            }
        };
        menu.Items.Add(refreshItem);

        var startupItem = new MenuItem
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

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += this.ExitItem_Click;
        menu.Items.Add(exitItem);

        return menu;
    }

    private void TrayIcon_TrayMouseMove(object? sender, RoutedEventArgs e) => this.ShowPopup();

    private async void ExitItem_Click(object sender, RoutedEventArgs e)
    {
        if (this._shutdownRequested)
        {
            return;
        }

        this._shutdownRequested = true;
        this._mainWindow?.SaveWindowState();
        CloseContextMenu(ResolveContextMenu(sender) ?? this._trayIcon?.ContextMenu);

        try
        {
            await this.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            this.DisposeTrayIcon();
            if (this._refreshService is not null)
            {
                await this._refreshService.StopAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodexBar] Graceful shutdown cleanup failed: {ex.Message}");
        }
        finally
        {
            this.Shutdown();
        }
    }

    private static ContextMenu? ResolveContextMenu(object? source) =>
        (source as FrameworkElement)?.Parent as ContextMenu;

    private static void CloseContextMenu(ContextMenu? menu)
    {
        if (menu is null)
        {
            return;
        }

        menu.IsOpen = false;
        menu.Visibility = Visibility.Collapsed;
        Keyboard.ClearFocus();
        Mouse.Capture(null);
    }

    private void DisposeTrayIcon()
    {
        var trayIcon = this._trayIcon;
        if (trayIcon is null)
        {
            return;
        }

        trayIcon.TrayMouseMove -= this.TrayIcon_TrayMouseMove;
        var contextMenu = trayIcon.ContextMenu;
        CloseContextMenu(contextMenu);
        trayIcon.ContextMenu = null;
        trayIcon.Visibility = Visibility.Collapsed;
        trayIcon.Dispose();
        this._trayIcon = null;

        if (contextMenu is null)
        {
            return;
        }

        contextMenu.Items.Clear();
        contextMenu.PlacementTarget = null;
        contextMenu.DataContext = null;
    }

    private void ShowPopup()
    {
        try
        {
            if (this._mainWindow is { IsVisible: true })
            {
                return;
            }

            var services = this._services!;
            var settingsService = services.GetRequiredService<SettingsService>();
            if (this._mainWindow is null)
            {
                var providers = services.GetRequiredService<IEnumerable<IUsageProvider>>();
                this._mainWindow = new MainWindow(settingsService, providers, this._refreshService!) { DataContext = this._viewModel };
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
        this.DisposeTrayIcon();
        this._services?.Dispose();
        base.OnExit(e);
    }
}
