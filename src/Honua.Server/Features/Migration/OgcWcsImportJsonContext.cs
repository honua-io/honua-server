// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Server.Features.Import;
using Honua.Server.Features.Migration;
using Honua.Server.Features.FileImport;
using Honua.Server.Features.RasterImport;

namespace Honua.Server.Features.Migration;

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
[JsonSerializable(typeof(MigrationCoverageStyleDiagnostic))]
[JsonSerializable(typeof(MigrationCoverageStyleDiagnostic[]))]
[JsonSerializable(typeof(OgcCoverageImportTarget))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, OgcCoverageImportTarget>))]
[JsonSerializable(typeof(MigrationSourceInventoryArtifact))]
[JsonSerializable(typeof(MigrationManifestArtifact))]
internal sealed partial class OgcWcsImportJsonContext : JsonSerializerContext
{
}
