// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Infrastructure.Monitoring;

internal readonly record struct OpsReleaseVersion(Version Number, int? Candidate)
{
    public static bool IsNewer(string current, string latest)
    {
        var installed = Parse(current);
        var published = Parse(latest);
        if (installed is null || published is null)
        {
            return false;
        }
        var comparison = published.Value.Number.CompareTo(installed.Value.Number);
        return comparison > 0 || (comparison == 0 && installed.Value.Candidate is { } candidate
            && (published.Value.Candidate is null || published.Value.Candidate > candidate));
    }

    private static OpsReleaseVersion? Parse(string value)
    {
        value = value.Split('+')[0];
        if (value.StartsWith('v'))
        {
            value = value[1..];
        }
        var parts = value.Split('-');
        if (parts.Length > 2)
        {
            return null;
        }
        var numbers = parts[0].Split('.');
        if (numbers.Length is < 2 or > 3 || numbers.Any(part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return null;
        }
        if (!Version.TryParse(numbers.Length == 2 ? parts[0] + ".0" : parts[0], out var number))
        {
            return null;
        }
        int? candidate = null;
        if (parts.Length == 2)
        {
            if (!parts[1].StartsWith("rc.", StringComparison.Ordinal)
                || !int.TryParse(parts[1][3..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return null;
            }
            candidate = parsed;
        }
        return new(number, candidate);
    }
}
