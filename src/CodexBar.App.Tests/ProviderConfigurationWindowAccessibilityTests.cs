// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CodexBar.App.ViewModels;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using NSubstitute;

[Collection("WPF UI")]
public sealed class ProviderConfigurationWindowAccessibilityTests(WpfApplicationFixture fixture)
{
    [Fact]
    public void ErrorMessage_ShownWindow_IsPoliteLiveRegion()
    {
        fixture.Run(() =>
        {
            var window = new ProviderConfigurationWindow();
            try
            {
                window.Show();
                var error = FindErrorText(window);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(error));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("schema")]
    [InlineData("io")]
    public void Save_Failure_AnnouncesBoundErrorIncludingRepeatedAttempts(string failure)
    {
        using var announcements = new BlockingCollection<string>();
        var service = Substitute.For<ISettingsService>();
        service.Load().Returns(new AppSettings { Providers = new() { ["Claude"] = new() { Enabled = true } } });
        service.When(settings => settings.Save(Arg.Any<AppSettings>())).Do(_ =>
        {
            if (failure == "schema")
            {
                throw new InvalidOperationException("synthetic schema detail");
            }

            throw new IOException("synthetic file detail");
        });
        var provider = Substitute.For<IUsageProvider>();
        provider.Metadata.Returns(new ProviderMetadata { Id = ProviderId.Claude, DisplayName = "Claude", Description = "Synthetic provider" });
        ProviderConfigurationWindow? window = null;
        ProviderConfigurationViewModel? viewModel = null;
        var handle = IntPtr.Zero;
        fixture.Run(() =>
        {
            window = new ProviderConfigurationWindow();
            viewModel = new ProviderConfigurationViewModel(service, [provider], window.Close);
            if (failure == "validation")
            {
                viewModel.Accounts[0].DisplayLabel = " ";
            }

            window.DataContext = viewModel;
            window.Show();
            handle = new WindowInteropHelper(window).Handle;
        });
        var root = AutomationElement.FromHandle(handle);
        AutomationEventHandler handler = (sender, _) => announcements.Add(((AutomationElement)sender).Current.Name);
        Automation.AddAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent, root, TreeScope.Descendants, handler);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                fixture.Run(() => viewModel!.SaveCommand.Execute(null));
                Assert.True(announcements.TryTake(out var spoken, TimeSpan.FromSeconds(5)), "The Save failure did not raise LiveRegionChanged.");
                fixture.Run(() =>
                {
                    Assert.True(window!.IsVisible);
                    Assert.NotEmpty(viewModel!.ErrorMessage);
                    Assert.Equal(viewModel.ErrorMessage, FindErrorText(window).Text);
                    Assert.Equal(viewModel.ErrorMessage, spoken);
                });
            }
        }
        finally
        {
            Automation.RemoveAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent, root, handler);
            fixture.Run(() => window!.Close());
        }
    }

    private static TextBlock FindErrorText(DependencyObject root) =>
        FindTextBlocks(root).Single(text => text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(ProviderConfigurationViewModel.ErrorMessage));

    private static IEnumerable<TextBlock> FindTextBlocks(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is TextBlock text)
            {
                yield return text;
            }

            foreach (var descendant in FindTextBlocks(child))
            {
                yield return descendant;
            }
        }
    }
}
