namespace CodexBar.App.Tests;

using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBar.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

[Collection("WPF UI")]
public sealed class MainWindowPositionTests : IDisposable
{
    private readonly string tempDir;
    private readonly WpfApplicationFixture wpfApplicationFixture;

    public MainWindowPositionTests(WpfApplicationFixture wpfApplicationFixture)
    {
        this.wpfApplicationFixture = wpfApplicationFixture;
        this.tempDir = Path.Combine(Path.GetTempPath(), $"codexbar-app-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.tempDir))
            {
                Directory.Delete(this.tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void SaveWindowState_PersistsCurrentCoordinates()
    {
        this.wpfApplicationFixture.Run(() =>
        {
            var settingsService = this.CreateSettingsService();
            var settings = settingsService.Load();
            settings.WindowWidth = 340;
            settings.WindowHeight = 420;
            settingsService.Save(settings);

            var window = new MainWindow(settingsService)
            {
                Left = 123,
                Top = 456,
                Width = 340,
                Height = 420,
            };

            window.SaveWindowState();

            var saved = settingsService.Load();
            Assert.Equal(123, saved.WindowLeft);
            Assert.Equal(456, saved.WindowTop);
        });
    }

    [Fact]
    public void RestoreState_ReappliesSavedCoordinates()
    {
        this.wpfApplicationFixture.Run(() =>
        {
            var settingsService = this.CreateSettingsService();
            var settings = settingsService.Load();
            settings.WindowLeft = 321;
            settings.WindowTop = 654;
            settings.WindowWidth = 340;
            settings.WindowHeight = 420;
            settingsService.Save(settings);

            var window = new MainWindow(settingsService)
            {
                Left = 10,
                Top = 20,
            };

            window.Left = 42;
            window.Top = 24;

            window.RestoreState();

            Assert.Equal(321, window.Left);
            Assert.Equal(654, window.Top);
        });
    }

    [Fact]
    public void SaveWindowState_PersistsSavedCoordinates_WhenPositionRestoredFromSettings()
    {
        // Regression test: After Hide(), WPF may reset Left/Top.
        // SaveWindowState should use the in-memory savedLeft/savedTop values
        // (which were set from settings at startup) instead of WPF Left/Top.
        this.wpfApplicationFixture.Run(() =>
        {
            var settingsService = this.CreateSettingsService();
            var settings = settingsService.Load();
            settings.WindowLeft = 500;
            settings.WindowTop = 300;
            settings.WindowWidth = 340;
            settings.WindowHeight = 420;
            settingsService.Save(settings);

            var window = new MainWindow(settingsService);

            // Constructor loads savedLeft=500, savedTop=300 from settings,
            // and sets hasUserPosition=true.
            window.SaveWindowState();

            var saved = settingsService.Load();
            Assert.Equal(500, saved.WindowLeft);
            Assert.Equal(300, saved.WindowTop);
        });
    }

    private SettingsService CreateSettingsService() =>
        new(NullLogger<SettingsService>.Instance, this.tempDir);
}

[CollectionDefinition("WPF UI", DisableParallelization = true)]
public sealed class WpfUiCollectionDefinition : ICollectionFixture<WpfApplicationFixture>;

public sealed class WpfApplicationFixture : IDisposable
{
    private readonly ManualResetEventSlim ready = new(false);
    private readonly Thread thread;
    private Application? application;
    private Dispatcher? dispatcher;
    private Exception? startupException;

    public WpfApplicationFixture()
    {
        this.thread = new Thread(this.InitializeApplicationThread)
        {
            IsBackground = true,
        };

        this.thread.SetApartmentState(ApartmentState.STA);
        this.thread.Start();

        Assert.True(this.ready.Wait(TimeSpan.FromSeconds(10)), "WPF test application timed out during startup.");

        if (this.startupException is not null)
        {
            ExceptionDispatchInfo.Capture(this.startupException).Throw();
        }
    }

    public void Dispose()
    {
        if (this.dispatcher is null)
        {
            this.ready.Dispose();
            return;
        }

        this.dispatcher.Invoke(() => this.application?.Shutdown());
        this.dispatcher.InvokeShutdown();
        this.thread.Join();
        this.ready.Dispose();
    }

    public void Run(Action action)
    {
        if (this.dispatcher is null)
        {
            throw new InvalidOperationException("WPF test application was not initialized.");
        }

        var operation = this.dispatcher.InvokeAsync(action);
        Assert.True(operation.Task.Wait(TimeSpan.FromSeconds(10)), "STA test timed out.");
        operation.Task.GetAwaiter().GetResult();
    }

    private void InitializeApplicationThread()
    {
        try
        {
            this.application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };

            RegisterApplicationResources(this.application.Resources);
            this.dispatcher = Dispatcher.CurrentDispatcher;
        }
        catch (Exception ex)
        {
            this.startupException = ex;
        }
        finally
        {
            this.ready.Set();
        }

        if (this.startupException is null)
        {
            Dispatcher.Run();
        }
    }

    private static void RegisterApplicationResources(ResourceDictionary resources)
    {
        resources["OpenRouterColor"] = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1));
        resources["CopilotColor"] = new SolidColorBrush(Color.FromRgb(0x23, 0x86, 0x36));
        resources["BackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
        resources["SurfaceBrush"] = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3E));
        resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    }
}
