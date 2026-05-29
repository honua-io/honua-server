// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.Infrastructure.Rendering;

internal static class MapLibreExpressionParser
{
    public static MapLibreExpression Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return MapLibreExpression.Null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, MapLibreStyleJsonContext.Default.MapLibreExpression);
        }
        catch (JsonException)
        {
            return MapLibreExpression.Null;
        }
    }
}
