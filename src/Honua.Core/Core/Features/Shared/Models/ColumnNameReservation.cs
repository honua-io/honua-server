// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Shared.Models;

internal static class ColumnNameReservation
{
    public static string Reserve(string preferred, HashSet<string> usedNames)
    {
        var candidate = preferred;
        for (var suffix = 1; !usedNames.Add(candidate); suffix++)
        {
            candidate = preferred + "_" + suffix.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }
}
