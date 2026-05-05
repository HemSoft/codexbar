// <copyright file="StartupManagerTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;
using Xunit;

public class StartupManagerTests
{
    [Fact]
    public void IsEnabled_DoesNotThrow()
    {
        // On any platform, IsEnabled should not throw
        var result = StartupManager.IsEnabled();
        Assert.IsType<bool>(result);
    }

    [Fact]
    public void SetEnabledFalse_DoesNotThrow()
    {
        // On any platform, SetEnabled(false) should either no-op or succeed
        // We test with false to avoid leaving run entries in the registry
        try
        {
            StartupManager.SetEnabled(false);
        }
        catch (Exception ex)
        {
            // On non-Windows or when registry access fails, this is expected
            Assert.True(ex is InvalidOperationException or UnauthorizedAccessException);
        }
    }
}
