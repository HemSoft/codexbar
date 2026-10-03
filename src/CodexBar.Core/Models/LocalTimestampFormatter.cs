// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Models;

using System.Globalization;

/// <summary>Formats source instants for the current local timezone and culture without changing their identity.</summary>
public static class LocalTimestampFormatter
{
    public static string Format(DateTimeOffset timestamp, bool includeDate = true, TimeZoneInfo? timeZone = null, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTime(timestamp, timeZone ?? TimeZoneInfo.Local);
        var date = includeDate ? local.ToString("d", culture) + " " : string.Empty;
        return $"{date}{local.ToString("t", culture)} UTC{local.ToString("zzz", CultureInfo.InvariantCulture)}";
    }
}
