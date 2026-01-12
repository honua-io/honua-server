// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Import;

/// <summary>
/// JSON serialization context for Esri import API types.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EsriDiscoverRequest))]
[JsonSerializable(typeof(EsriDiscoverResponse))]
[JsonSerializable(typeof(EsriLayerSummary))]
[JsonSerializable(typeof(EsriLayerSummary[]))]
[JsonSerializable(typeof(EsriStartImportRequest))]
[JsonSerializable(typeof(EsriImportJobResponse))]
[JsonSerializable(typeof(EsriImportJobsResponse))]
[JsonSerializable(typeof(EsriImportCancelResponse))]
[JsonSerializable(typeof(EsriImportProgress))]
[JsonSerializable(typeof(EsriImportProgress[]))]
[JsonSerializable(typeof(EsriImportStatus))]
[JsonSerializable(typeof(EsriServiceInfo))]
[JsonSerializable(typeof(EsriLayerInfo))]
[JsonSerializable(typeof(EsriLayerInfo[]))]
[JsonSerializable(typeof(EsriFieldInfo))]
[JsonSerializable(typeof(EsriFieldInfo[]))]
[JsonSerializable(typeof(EsriExtent))]
[JsonSerializable(typeof(EsriImportRequest))]
[JsonSerializable(typeof(EsriImportResult))]
// Add unified progress support
[JsonSerializable(typeof(IOperationProgress))]
[JsonSerializable(typeof(OperationType))]
[JsonSerializable(typeof(OperationStatus))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class EsriImportApiJsonContext : JsonSerializerContext
{
}
