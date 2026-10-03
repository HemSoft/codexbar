// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App;

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class ProviderConfigurationWindow : Window
{
    public ProviderConfigurationWindow()
    {
        this.InitializeComponent();
    }

    private void OnErrorMessageTargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (sender is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
        {
            var peer = UIElementAutomationPeer.FromElement(textBlock) ?? UIElementAutomationPeer.CreatePeerForElement(textBlock);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
