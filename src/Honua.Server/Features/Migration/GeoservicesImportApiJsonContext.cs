// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Domain;
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
/// JSON serialization context for Geoservices import API types.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GeoservicesDiscoverRequest))]
[JsonSerializable(typeof(GeoservicesDiscoverResponse))]
[JsonSerializable(typeof(GeoservicesLayerSummary))]
[JsonSerializable(typeof(GeoservicesLayerSummary[]))]
[JsonSerializable(typeof(GeoservicesStartImportRequest))]
[JsonSerializable(typeof(GeoservicesImportJobResponse))]
[JsonSerializable(typeof(GeoservicesImportJobsResponse))]
[JsonSerializable(typeof(GeoservicesImportCancelResponse))]
[JsonSerializable(typeof(GeoservicesImportProgress))]
[JsonSerializable(typeof(GeoservicesImportProgress[]))]
[JsonSerializable(typeof(GeoservicesImportStatus))]
[JsonSerializable(typeof(GeoservicesServiceInfo))]
[JsonSerializable(typeof(GeoservicesLayerInfo))]
[JsonSerializable(typeof(GeoservicesLayerInfo[]))]
[JsonSerializable(typeof(GeoservicesFieldInfo))]
[JsonSerializable(typeof(GeoservicesFieldInfo[]))]
[JsonSerializable(typeof(EsriExtent))]
[JsonSerializable(typeof(GeoservicesCredentialRequest))]
[JsonSerializable(typeof(GeoservicesCredentialDescriptor))]
[JsonSerializable(typeof(GeoservicesImportRequest))]
[JsonSerializable(typeof(GeoservicesImportResult))]
// Add unified progress support
[JsonSerializable(typeof(IOperationProgress))]
[JsonSerializable(typeof(OperationType))]
[JsonSerializable(typeof(OperationStatus))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class GeoservicesImportApiJsonContext : JsonSerializerContext
{
}
