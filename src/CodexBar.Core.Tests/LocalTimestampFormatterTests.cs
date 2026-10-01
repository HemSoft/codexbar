// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.Globalization;
using CodexBar.Core.Models;

public sealed class LocalTimestampFormatterTests
{
    [Theory]
    [InlineData("de-DE", 330, "01.01.2026 17:30 UTC+05:30")]
    [InlineData("en-GB", 540, "01/01/2026 21:00 UTC+09:00")]
    [InlineData("fr-FR", -480, "01/01/2026 04:00 UTC-08:00")]
    public void Format_NonUsZoneAndCulture_UsesLocalDateTimeAndOffset(string cultureName, int offsetMinutes, string expected)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromMinutes(offsetMinutes), "Test", "Test");
        var instant = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, LocalTimestampFormatter.Format(instant, timeZone: zone, culture: CultureInfo.GetCultureInfo(cultureName)));
    }

    [Fact]
    public void Format_TimeOnly_OmitsDateWithoutForcingTwelveHourClock()
    {
        var instant = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("12:00 UTC+00:00", LocalTimestampFormatter.Format(instant, false, TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("en-GB")));
    }

    [Fact]
    public void Format_Defaults_ReadsCurrentLocalZoneAndCulture()
    {
        var instant = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(LocalTimestampFormatter.Format(instant, true, TimeZoneInfo.Local, CultureInfo.CurrentCulture), LocalTimestampFormatter.Format(instant));
    }

    [Theory]
    [InlineData("2026-03-29T00:30:00Z", "29/03/2026 01:30 UTC+01:00")]
    [InlineData("2026-03-29T01:30:00Z", "29/03/2026 03:30 UTC+02:00")]
    [InlineData("2026-10-25T00:30:00Z", "25/10/2026 02:30 UTC+02:00")]
    [InlineData("2026-10-25T01:30:00Z", "25/10/2026 02:30 UTC+01:00")]
    public void Format_DstTransitions_PreservesInstantAndDistinguishesRepeatedClockTime(string source, string expected)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var instant = DateTimeOffset.Parse(source, CultureInfo.InvariantCulture);
        Assert.Equal(expected, LocalTimestampFormatter.Format(instant, timeZone: zone, culture: CultureInfo.GetCultureInfo("en-GB")));
    }
}
