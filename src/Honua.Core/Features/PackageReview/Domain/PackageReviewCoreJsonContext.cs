// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.PackageReview.Domain;

/// <summary>
/// Source-generated JSON context used by the Core package-review service.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(PackageReviewRequest))]
[JsonSerializable(typeof(PackageReviewRequirements))]
[JsonSerializable(typeof(PackageReviewContext))]
[JsonSerializable(typeof(PackageReviewResponse))]
[JsonSerializable(typeof(PackageFinding))]
[JsonSerializable(typeof(PackageRequiredAction))]
[JsonSerializable(typeof(PackageAffectedArtifact))]
[JsonSerializable(typeof(PackageFindingEvidence))]
[JsonSerializable(typeof(PackagePreviewPlan))]
[JsonSerializable(typeof(PackagePreviewOperation))]
[JsonSerializable(typeof(PackageEstimate))]
[JsonSerializable(typeof(PackageFamilyReviewResult))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class PackageReviewCoreJsonContext : JsonSerializerContext
{
}
