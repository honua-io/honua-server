// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Import.Models;
using Honua.Server.Features.Infrastructure.Models;
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
/// JSON serialization context for GeoServer import API types.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
// API request/response models
[JsonSerializable(typeof(GeoServerDiscoveryApiRequest))]
[JsonSerializable(typeof(GeoServerImportApiRequest))]
[JsonSerializable(typeof(GeoServerImportApiOptions))]
[JsonSerializable(typeof(GeoServerImportJob))]
[JsonSerializable(typeof(GeoServerImportJob[]))]
[JsonSerializable(typeof(GeoServerImportJobStatus))]
[JsonSerializable(typeof(GeoServerImportJobResponse))]
[JsonSerializable(typeof(GeoServerImportJobsResponse))]
[JsonSerializable(typeof(GeoServerImportCancelResponse))]
// Core domain models
[JsonSerializable(typeof(GeoServerServiceInfo))]
[JsonSerializable(typeof(GeoServerWorkspaceInfo))]
[JsonSerializable(typeof(GeoServerWorkspaceInfo[]))]
[JsonSerializable(typeof(GeoServerDataStoreInfo))]
[JsonSerializable(typeof(GeoServerDataStoreInfo[]))]
[JsonSerializable(typeof(GeoServerCoverageStoreInfo))]
[JsonSerializable(typeof(GeoServerCoverageStoreInfo[]))]
[JsonSerializable(typeof(GeoServerLayerInfo))]
[JsonSerializable(typeof(GeoServerLayerInfo[]))]
[JsonSerializable(typeof(GeoServerLayerGroupInfo))]
[JsonSerializable(typeof(GeoServerLayerGroupInfo[]))]
[JsonSerializable(typeof(GeoServerLayerGroupEntry))]
[JsonSerializable(typeof(GeoServerLayerGroupEntry[]))]
[JsonSerializable(typeof(GeoServerStyleInfo))]
[JsonSerializable(typeof(GeoServerStyleInfo[]))]
[JsonSerializable(typeof(GeoServerBoundingBox))]
[JsonSerializable(typeof(GeoServerGlobalSettings))]
[JsonSerializable(typeof(GeoServerContactInfo))]
[JsonSerializable(typeof(GeoServerMigrationCompatibility))]
[JsonSerializable(typeof(GeoServerResourceCompatibility))]
[JsonSerializable(typeof(GeoServerResourceTypeCompatibility))]
[JsonSerializable(typeof(GeoServerResourceTypeCompatibility[]))]
[JsonSerializable(typeof(GeoServerCompatibilityLevel))]
[JsonSerializable(typeof(GeoServerImportProgress))]
[JsonSerializable(typeof(GeoServerImportResourceBreakdown))]
[JsonSerializable(typeof(GeoServerImportStatus))]
[JsonSerializable(typeof(GeoServerDiscoveryRequest))]
[JsonSerializable(typeof(GeoServerImportRequest))]
[JsonSerializable(typeof(GeoServerImportOptions))]
[JsonSerializable(typeof(GeoServerImportResult))]
[JsonSerializable(typeof(GeoServerImportedResource))]
[JsonSerializable(typeof(GeoServerImportedResource[]))]
[JsonSerializable(typeof(GeoServerFailedResource))]
[JsonSerializable(typeof(GeoServerFailedResource[]))]
[JsonSerializable(typeof(UnsupportedResourceBehavior))]
[JsonSerializable(typeof(MigrationApplyPlanArtifact))]
[JsonSerializable(typeof(MigrationApplyPlanSummary))]
[JsonSerializable(typeof(MigrationApplyPlanStep))]
[JsonSerializable(typeof(MigrationApplyPlanStep[]))]
[JsonSerializable(typeof(MigrationApplyExecutionArtifact))]
[JsonSerializable(typeof(MigrationApplyExecutionSummary))]
[JsonSerializable(typeof(MigrationApplyExecutionStepResult))]
[JsonSerializable(typeof(MigrationApplyExecutionStepResult[]))]
[JsonSerializable(typeof(MigrationManifestReviewItem))]
[JsonSerializable(typeof(MigrationManifestReviewItem[]))]
[JsonSerializable(typeof(MigrationCompatibilityAssessment))]
[JsonSerializable(typeof(MigrationSourceIdentity))]
// Collections and dictionaries
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(object))]
// Unified progress support
[JsonSerializable(typeof(IOperationProgress))]
[JsonSerializable(typeof(OperationType))]
[JsonSerializable(typeof(OperationStatus))]
internal sealed partial class GeoServerImportApiJsonContext : JsonSerializerContext
{
}
