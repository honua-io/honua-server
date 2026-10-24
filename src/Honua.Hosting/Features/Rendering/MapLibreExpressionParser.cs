// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Infrastructure.Rendering;

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
