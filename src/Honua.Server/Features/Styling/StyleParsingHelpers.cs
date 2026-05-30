// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

namespace Honua.Server.Features.Styling;

internal static class StyleParsingHelpers
{
    internal static bool TryGetDouble(JsonElement element, out double value)
    {
        value = 0;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var numeric))
        {
            value = numeric;
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }
}
