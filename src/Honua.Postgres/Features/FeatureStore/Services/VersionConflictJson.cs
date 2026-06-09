// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Source-generated JSON contract used by the branch-version conflict-resolution layer (#371) to
/// persist and read back the per-field three-way diff payload. AOT-safe and kept internal to the
/// Postgres versioning slice.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VersionConflictFieldDiff[]))]
internal sealed partial class VersionConflictJsonContext : JsonSerializerContext;

/// <summary>
/// Convenience accessor for the <see cref="VersionConflictJsonContext"/> type info.
/// </summary>
internal static class VersionConflictJson
{
    /// <summary>Type info for the persisted per-field three-way diff array.</summary>
    public static JsonTypeInfo<VersionConflictFieldDiff[]> FieldDiffArray =>
        VersionConflictJsonContext.Default.VersionConflictFieldDiffArray;
}
