// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Studio.Domain;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Studio.Models;

/// <summary>
/// Source-generated JSON context for the Studio package lifecycle API.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<StudioPackageFamilyCapabilities>))]
[JsonSerializable(typeof(ApiResponse<StudioPackageDraft>))]
[JsonSerializable(typeof(ApiResponse<StudioValidationSummary>))]
[JsonSerializable(typeof(ApiResponse<StudioPreviewPlan>))]
[JsonSerializable(typeof(ApiResponse<StudioContentVersion>))]
[JsonSerializable(typeof(ApiResponse<StudioContentVersionListResponse>))]
[JsonSerializable(typeof(ApiResponse<StudioPackageDraftListResponse>))]
[JsonSerializable(typeof(ApiResponse<StudioVersionComparison>))]
[JsonSerializable(typeof(ApiResponse<StudioPublicationRequest>))]
[JsonSerializable(typeof(ApiResponse<StudioRollbackRequest>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(CreateStudioPackageDraftRequest))]
[JsonSerializable(typeof(UpdateStudioPackageDraftRequest))]
[JsonSerializable(typeof(SaveStudioContentVersionRequest))]
[JsonSerializable(typeof(CompareStudioContentVersionsRequest))]
[JsonSerializable(typeof(CreateStudioPublicationRequest))]
[JsonSerializable(typeof(CreateStudioRollbackRequest))]
[JsonSerializable(typeof(StudioContentVersionListResponse))]
[JsonSerializable(typeof(StudioPackageDraftListResponse))]
[JsonSerializable(typeof(StudioPackageDraftSummary))]
[JsonSerializable(typeof(StudioPackageFamilyCapabilities))]
[JsonSerializable(typeof(StudioPackageDraft))]
[JsonSerializable(typeof(StudioValidationSummary))]
[JsonSerializable(typeof(StudioPreviewPlan))]
[JsonSerializable(typeof(StudioContentVersion))]
[JsonSerializable(typeof(StudioVersionComparison))]
[JsonSerializable(typeof(StudioPublicationRequest))]
[JsonSerializable(typeof(StudioRollbackRequest))]
internal sealed partial class StudioApiJsonContext : JsonSerializerContext
{
}
