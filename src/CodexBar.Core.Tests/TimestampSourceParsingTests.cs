// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Globalization;
using CodexBar.Core.Providers.Copilot;

public sealed class TimestampSourceParsingTests
{
    [Theory]
    [InlineData("2026-01-01T12:00:00", 0, 12)]
    [InlineData("2026-01-01T12:00:00+05:00", 5, 7)]
    [InlineData("2026-01-01T12:00:00-04:00", -4, 16)]
    public void ParseReset_InvariantSourceDate_PreservesInstantAndExplicitOffset(string source, int offsetHours, int utcHour)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var (instant, _) = CopilotProvider.ParseReset(source);
            Assert.Equal(new DateTimeOffset(2026, 1, 1, utcHour, 0, 0, TimeSpan.Zero), instant);
            Assert.Equal(TimeSpan.FromHours(offsetHours), instant!.Value.Offset);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
