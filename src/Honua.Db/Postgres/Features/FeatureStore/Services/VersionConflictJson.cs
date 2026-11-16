// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VersionConflictFieldDiff[]))]
internal sealed partial class VersionConflictJsonContext : JsonSerializerContext;

internal static class VersionConflictJson
{
    public static JsonTypeInfo<VersionConflictFieldDiff[]> FieldDiffArray =>
        VersionConflictJsonContext.Default.VersionConflictFieldDiffArray;
}
