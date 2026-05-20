// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;

namespace Honua.Server.Features.Import;

/// <summary>
/// Source-generated JSON serialization context for legacy OGC WCS coverage import payloads.
/// Issue #1030 slice 3.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OgcWcsImportApiRequest))]
[JsonSerializable(typeof(OgcWcsImportRequest))]
[JsonSerializable(typeof(OgcWcsImportResult))]
[JsonSerializable(typeof(OgcCoverageImportRecord))]
[JsonSerializable(typeof(OgcCoverageImportRecord[]))]
[JsonSerializable(typeof(OgcCoverageImportTarget))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, OgcCoverageImportTarget>))]
[JsonSerializable(typeof(MigrationSourceInventoryArtifact))]
[JsonSerializable(typeof(MigrationManifestArtifact))]
internal sealed partial class OgcWcsImportJsonContext : JsonSerializerContext
{
}
