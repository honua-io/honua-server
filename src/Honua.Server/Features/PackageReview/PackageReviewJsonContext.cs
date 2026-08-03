// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.PackageReview.Domain;
using Honua.Infrastructure.Models;

namespace Honua.PackageReview;

/// <summary>
/// Source-generated JSON context for package-review HTTP and MCP adapters.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true,
    WriteIndented = false)]
[JsonSerializable(typeof(PackageReviewRequest))]
[JsonSerializable(typeof(PackageReviewRequirements))]
[JsonSerializable(typeof(PackageReviewResponse))]
[JsonSerializable(typeof(PackageFinding))]
[JsonSerializable(typeof(PackageRequiredAction))]
[JsonSerializable(typeof(PackageAffectedArtifact))]
[JsonSerializable(typeof(PackageFindingEvidence))]
[JsonSerializable(typeof(PackagePreviewPlan))]
[JsonSerializable(typeof(PackagePreviewOperation))]
[JsonSerializable(typeof(PackageEstimate))]
[JsonSerializable(typeof(ApiResponse<PackageReviewResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(AnalysisPlanPackagePayload))]
[JsonSerializable(typeof(AnalysisPlanStepPackagePayload))]
[JsonSerializable(typeof(RasterSourceDescriptor))]
[JsonSerializable(typeof(PostgisRasterSourceDescriptor))]
[JsonSerializable(typeof(ObjectStoreCogRasterSourceDescriptor))]
[JsonSerializable(typeof(ObjectStoreZarrRasterSourceDescriptor))]
[JsonSerializable(typeof(StagedArtifactRasterSourceDescriptor))]
[JsonSerializable(typeof(InlineRasterSourceDescriptor))]
[JsonSerializable(typeof(RasterContentIdentity))]
[JsonSerializable(typeof(RasterChecksum))]
[JsonSerializable(typeof(RasterSecurityContextReference))]
[JsonSerializable(typeof(RasterSourceSelection))]
[JsonSerializable(typeof(RasterPixelWindow))]
[JsonSerializable(typeof(RasterTimeSelection))]
[JsonSerializable(typeof(RasterDimensionSlice))]
[JsonSerializable(typeof(Dictionary<string, RasterSourceDescriptor>))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class PackageReviewJsonContext : JsonSerializerContext
{
}
