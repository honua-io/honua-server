// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Publishing.Content;
using Honua.Core.Features.Publishing.Content.Domain;

namespace Honua.Db.Postgres.Features.Publishing;

internal sealed partial class PostgresContentPublicationStore
{
    private static ContentPublicationDependencyRef[] ReadStoredDependencies(string json)
    {
        // Immutable versions retain their original bytes; normalize only this private read projection.
        var dependencies = JsonNode.Parse(json) as JsonArray
            ?? throw new JsonException("Stored publication dependencies must be an array.");
        foreach (var dependency in dependencies)
        {
            if (dependency is JsonObject item && item["kind"] is JsonValue kind)
            {
                item["kind"] = CanonicalStoredDependencyKind(kind.GetValue<string>());
            }
        }

        return JsonSerializer.Deserialize(dependencies.ToJsonString(),
            ContentPublicationJsonContext.Default.ContentPublicationDependencyRefArray) ?? [];
    }

    private static string CanonicalStoredDependencyKind(string kind) => kind switch
    {
        "resource" => "dataset",
        "publication" => "layer",
        "map-package" => "map",
        "app-package" => "app",
        _ => kind,
    };
}
