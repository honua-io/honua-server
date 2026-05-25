// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.OpenData.Domain;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.OpenData.Models;

/// <summary>
/// JSON source-generation context for open-data, DCAT, Schema.org, and STAC publication API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<OpenDataEligibility>))]
[JsonSerializable(typeof(ApiResponse<OpenDataPageAdminResponse>))]
[JsonSerializable(typeof(ApiResponse<OpenDataDcatStatusResponse>))]
[JsonSerializable(typeof(ApiResponse<OpenDataValidationSummary>))]
[JsonSerializable(typeof(ApiResponse<StacPublicationStatusResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(OpenDataPageUpdateRequest))]
[JsonSerializable(typeof(OpenDataPageAdminResponse))]
[JsonSerializable(typeof(OpenDataItemResponse))]
[JsonSerializable(typeof(OpenDataListResponse))]
[JsonSerializable(typeof(SchemaOrgDatasetResponse))]
[JsonSerializable(typeof(SchemaOrgDistributionResponse))]
[JsonSerializable(typeof(DcatCatalogResponse))]
[JsonSerializable(typeof(DcatDatasetResponse))]
[JsonSerializable(typeof(DcatDistributionResponse))]
[JsonSerializable(typeof(DcatSpatialResponse))]
[JsonSerializable(typeof(OpenDataDcatStatusResponse))]
[JsonSerializable(typeof(OpenDataDcatValidateRequest))]
[JsonSerializable(typeof(StacPublicationPublishRequest))]
[JsonSerializable(typeof(StacPublicationUpdateRequest))]
[JsonSerializable(typeof(StacPublicationStatusResponse))]
[JsonSerializable(typeof(OpenDataPageRecord))]
[JsonSerializable(typeof(OpenDataOrganization))]
[JsonSerializable(typeof(OpenDataContact))]
[JsonSerializable(typeof(OpenDataDistribution))]
[JsonSerializable(typeof(OpenDataSpatialExtent))]
[JsonSerializable(typeof(OpenDataTemporalExtent))]
[JsonSerializable(typeof(OpenDataIssue))]
[JsonSerializable(typeof(OpenDataEligibility))]
[JsonSerializable(typeof(OpenDataValidationResult))]
[JsonSerializable(typeof(OpenDataValidationSummary))]
[JsonSerializable(typeof(OpenDataStacPublicationRecord))]
[JsonSerializable(typeof(OpenDataStacPublicationStatus))]
[JsonSerializable(typeof(ConsoleProvenanceRef))]
internal sealed partial class OpenDataJsonContext : JsonSerializerContext
{
}
