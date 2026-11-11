// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.Operations;

internal static class AdminOperationSchema
{
    internal static (JsonElement Schema, bool AllowsNull) Resolve(JsonElement root, JsonElement schema)
    {
        var allowsNull = false;
        while (true)
        {
            allowsNull |= schema.TryGetProperty("nullable", out var nullable) && nullable.ValueKind == JsonValueKind.True;
            if (schema.TryGetProperty("allOf", out var allOf) && allOf.GetArrayLength() == 1)
            {
                schema = allOf[0];
                continue;
            }

            if (!schema.TryGetProperty("$ref", out var reference))
            {
                return (schema, allowsNull);
            }

            schema = root;
            foreach (var segment in reference.GetString()![2..].Split('/'))
            {
                schema = schema.GetProperty(segment);
            }
        }
    }
}
