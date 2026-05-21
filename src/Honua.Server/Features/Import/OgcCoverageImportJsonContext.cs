// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;

namespace Honua.Server.Features.Import;

/// <summary>
/// Source-generated JSON serialization context for OGC coverage import endpoint payloads.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OgcCoverageImportApiRequest))]
[JsonSerializable(typeof(OgcCoverageImportRequest))]
[JsonSerializable(typeof(OgcCoverageImportResult))]
[JsonSerializable(typeof(OgcCoverageImportRecord))]
[JsonSerializable(typeof(OgcCoverageImportRecord[]))]
[JsonSerializable(typeof(MigrationCoverageStyleDiagnostic))]
[JsonSerializable(typeof(MigrationCoverageStyleDiagnostic[]))]
[JsonSerializable(typeof(OgcCoverageImportTarget))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, OgcCoverageImportTarget>))]
[JsonSerializable(typeof(MigrationSourceInventoryArtifact))]
[JsonSerializable(typeof(MigrationManifestArtifact))]
internal sealed partial class OgcCoverageImportJsonContext : JsonSerializerContext
{
}
