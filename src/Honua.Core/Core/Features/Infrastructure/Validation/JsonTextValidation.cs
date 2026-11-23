// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Infrastructure.Validation;

// PostgreSQL text and jsonb cannot store the NUL character, so client JSON bound for either is
// checked first: string values, array elements, property names and nested values. Unescaping an
// unpaired surrogate throws InvalidOperationException; callers decide how to report that.
public static class JsonTextValidation
{
    public static bool ContainsNul(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()?.Contains('\0', StringComparison.Ordinal) == true,
        JsonValueKind.Array => element.EnumerateArray().Any(ContainsNul),
        JsonValueKind.Object => element.EnumerateObject().Any(static property =>
            property.Name.Contains('\0', StringComparison.Ordinal) || ContainsNul(property.Value)),
        _ => false,
    };
}
