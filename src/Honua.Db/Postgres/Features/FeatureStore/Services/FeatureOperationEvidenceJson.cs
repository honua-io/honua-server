// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal static class FeatureOperationEvidenceJson
{
    internal static FeatureOperationEvidence Read(JsonElement item)
    {
        var generation = item.GetProperty("generation").GetInt64();
        var operation = (FeatureChangeOperation)item.GetProperty("operation").GetInt16();
        if (generation <= 0 || operation is not (FeatureChangeOperation.Insert or
            FeatureChangeOperation.Update or FeatureChangeOperation.Delete))
        {
            throw new JsonException("Invalid operation evidence.");
        }
        var publicId = item.GetProperty("public_objectid");
        var versionId = item.GetProperty("version_id");
        return new FeatureOperationEvidence
        {
            Generation = generation,
            LayerId = item.GetProperty("layer_id").GetInt32(),
            ObjectId = item.GetProperty("objectid").GetInt64(),
            PublicObjectId = publicId.ValueKind == JsonValueKind.Null ? null : publicId.GetInt64(),
            VersionId = versionId.ValueKind == JsonValueKind.Null ? null : versionId.GetGuid(),
            Operation = operation
        };
    }

    internal static void Write(Utf8JsonWriter writer, FeatureOperationEvidence evidence)
    {
        writer.WriteStartObject();
        writer.WriteNumber("generation", evidence.Generation);
        writer.WriteNumber("layer_id", evidence.LayerId);
        writer.WriteNumber("objectid", evidence.ObjectId);
        if (evidence.PublicObjectId is { } publicId)
        {
            writer.WriteNumber("public_objectid", publicId);
        }
        else
        {
            writer.WriteNull("public_objectid");
        }
        if (evidence.VersionId is { } versionId)
        {
            writer.WriteString("version_id", versionId);
        }
        else
        {
            writer.WriteNull("version_id");
        }
        writer.WriteNumber("operation", (short)evidence.Operation);
        writer.WriteEndObject();
    }
}
