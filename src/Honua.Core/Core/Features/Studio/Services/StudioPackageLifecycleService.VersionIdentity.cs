// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

public sealed partial class StudioPackageLifecycleService
{
    private static StudioPackageEnvelope NormalizeVersionForReopen(StudioPackageEnvelope envelope)
    {
        if (envelope.Family is not (StudioPackageFamily.Map or StudioPackageFamily.App))
        {
            return envelope;
        }

        // Project immutable history into a new draft; never rewrite its stored bytes or hash.
        var body = envelope.Body is { } storedBody
            ? JsonNode.Parse(storedBody.GetRawText()) as JsonObject : null;
        if (body is null)
        {
            throw new JsonException("Stored Map/App version must contain an object body.");
        }

        RenameVersionIdentity(body, "mapPackageId", "mapId");
        RenameVersionIdentity(body, "appPackageId", "appId");
        if (body["format"] is JsonValue format)
        {
            body["format"] = CanonicalVersionFormat(format.GetValue<string>());
        }

        using var document = JsonDocument.Parse(body.ToJsonString());
        return envelope with
        {
            Format = CanonicalVersionFormat(envelope.Format),
            Body = document.RootElement.Clone(),
        };
    }

    private static void RenameVersionIdentity(JsonObject body, string oldName, string newName)
    {
        if (!body.TryGetPropertyValue(oldName, out var legacy))
        {
            return;
        }

        if (body.TryGetPropertyValue(newName, out var canonical) && !JsonNode.DeepEquals(legacy, canonical))
        {
            throw new JsonException("Stored version contains conflicting legacy and canonical identities.");
        }

        body[newName] = legacy?.DeepClone();
        body.Remove(oldName);
    }

    private static string? CanonicalVersionFormat(string? format) => format switch
    {
        "honua_map_package.v1" => "honua_map.v1",
        "honua_app_package.v1" => "honua_app.v1",
        _ => format,
    };
}
