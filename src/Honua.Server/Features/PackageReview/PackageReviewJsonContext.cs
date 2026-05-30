// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.PackageReview.Domain;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.PackageReview;

/// <summary>
/// Source-generated JSON context for package-review HTTP and MCP adapters.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class PackageReviewJsonContext : JsonSerializerContext
{
}
