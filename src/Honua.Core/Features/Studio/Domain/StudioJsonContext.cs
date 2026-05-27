// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Studio.Domain;

/// <summary>
/// Source-generated JSON context for Studio package lifecycle domain models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(StudioPackageEnvelope))]
[JsonSerializable(typeof(StudioPackageBinding))]
[JsonSerializable(typeof(StudioPackageDependency))]
[JsonSerializable(typeof(StudioValidationSummary))]
[JsonSerializable(typeof(StudioValidationDiagnostic))]
[JsonSerializable(typeof(StudioPublicationIntent))]
[JsonSerializable(typeof(StudioProvenanceRef))]
[JsonSerializable(typeof(StudioPackageFamilyDescriptor))]
[JsonSerializable(typeof(StudioPackageFamilyCapabilities))]
[JsonSerializable(typeof(StudioPackageDraft))]
[JsonSerializable(typeof(StudioContentVersion))]
[JsonSerializable(typeof(StudioContentItemPointers))]
[JsonSerializable(typeof(StudioPublicationRequest))]
[JsonSerializable(typeof(StudioVersionComparison))]
[JsonSerializable(typeof(StudioPreviewPlan))]
[JsonSerializable(typeof(StudioRollbackRequest))]
[JsonSerializable(typeof(StudioPackageBinding[]))]
[JsonSerializable(typeof(StudioPackageDependency[]))]
[JsonSerializable(typeof(StudioProvenanceRef[]))]
[JsonSerializable(typeof(StudioValidationDiagnostic[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class StudioJsonContext : JsonSerializerContext
{
}
