// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geoprocessing;

namespace Honua.Infrastructure.Monitoring;

internal static class McpOpsReaderHelpers
{
    public static TEnum? ParseOptionalEnum<TEnum>(string? value, string fieldName)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new GeoprocessingValidationException(
            $"'{fieldName}' contains unsupported value '{value}'.");
    }
}
