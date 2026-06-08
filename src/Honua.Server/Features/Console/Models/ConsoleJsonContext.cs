// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.TypeSidecars;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Console.Models;

/// <summary>
/// JSON source generation context for Console content/RBAC API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<ConsoleSessionContext>))]
[JsonSerializable(typeof(ApiResponse<ConsoleContentItem>))]
[JsonSerializable(typeof(ApiResponse<ConsoleContentListResponse>))]
[JsonSerializable(typeof(ApiResponse<ConsoleActionCheckResponse>))]
[JsonSerializable(typeof(ApiResponse<ConsoleProvenanceChainResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
// Share access projection, public-link, and embed contracts (#1215).
[JsonSerializable(typeof(ApiResponse<ConsoleShareProjection>))]
[JsonSerializable(typeof(ApiResponse<ConsolePublicLinkToken>))]
[JsonSerializable(typeof(ApiResponse<ConsolePublicLinkListResponse>))]
[JsonSerializable(typeof(ApiResponse<ConsolePublicLinkResolutionResponse>))]
[JsonSerializable(typeof(ApiResponse<ConsoleEmbedToken>))]
[JsonSerializable(typeof(ApiResponse<ConsoleEmbedRedeemResponse>))]
[JsonSerializable(typeof(ApiResponse<ConsoleShareDependencyClosureResponse>))]
// Open-data DCAT + STAC publication API (#1214).
[JsonSerializable(typeof(ApiResponse<ConsoleOpenDataPageResponse>))]
[JsonSerializable(typeof(ApiResponse<ConsoleOpenDataPage>))]
[JsonSerializable(typeof(ApiResponse<ConsoleOpenDataEligibilityResponse>))]
[JsonSerializable(typeof(ApiResponse<ConsoleDcatExportResponse>))]
[JsonSerializable(typeof(ApiResponse<ConsoleStacPublicationState>))]
[JsonSerializable(typeof(ApiResponse<ConsoleOpenDataValidationResult>))]
[JsonSerializable(typeof(UpdateOpenDataPageRequest))]
[JsonSerializable(typeof(ConsoleOpenDataPageResponse))]
[JsonSerializable(typeof(ConsoleOpenDataPage))]
[JsonSerializable(typeof(ConsoleOpenDataEligibilityResponse))]
[JsonSerializable(typeof(ConsoleDcatExportResponse))]
[JsonSerializable(typeof(ConsoleOpenDataValidationResult))]
[JsonSerializable(typeof(ConsoleStacPublicationState))]
[JsonSerializable(typeof(DcatCatalog))]
[JsonSerializable(typeof(SchemaOrgDataset))]
[JsonSerializable(typeof(StacProjectionCatalog))]
[JsonSerializable(typeof(StacProjectionCollection))]
[JsonSerializable(typeof(StacProjectionItem))]
// Catalog discovery-endpoints registry read API (#1279).
[JsonSerializable(typeof(ApiResponse<CatalogDiscoveryRegistry>))]
[JsonSerializable(typeof(ApiResponse<CatalogEndpointDetail>))]
[JsonSerializable(typeof(ApiResponse<CatalogItem>))]
[JsonSerializable(typeof(CatalogDiscoveryRegistry))]
[JsonSerializable(typeof(CatalogEndpointDetail))]
[JsonSerializable(typeof(CatalogItem))]
// Console Access (RBAC) workspace-scoped read API (projects global admin roles; #1162).
[JsonSerializable(typeof(ApiResponse<ConsoleRbacOverview>))]
[JsonSerializable(typeof(ApiResponse<ConsoleTeamMembership>))]
[JsonSerializable(typeof(ConsoleRbacOverview))]
[JsonSerializable(typeof(ConsoleTeamMembership))]
// Console Access (RBAC) role authoring + audit write/read API (#1162).
[JsonSerializable(typeof(ConsoleRoleWriteRequest))]
[JsonSerializable(typeof(ApiResponse<ConsoleRbacRole>))]
[JsonSerializable(typeof(ApiResponse<ConsoleRoleAuditPage>))]
[JsonSerializable(typeof(ConsoleRbacRole))]
[JsonSerializable(typeof(ConsoleRoleAuditPage))]
[JsonSerializable(typeof(ConsoleRoleAuditEntry))]
[JsonSerializable(typeof(CreateConsoleContentItemRequest))]
[JsonSerializable(typeof(UpdateConsoleContentItemRequest))]
[JsonSerializable(typeof(PatchConsoleContentItemRequest))]
[JsonSerializable(typeof(ConsoleActionCheckRequest))]
[JsonSerializable(typeof(UpdateShareAccessRequest))]
[JsonSerializable(typeof(MintPublicLinkRequest))]
[JsonSerializable(typeof(SetEmbedRequest))]
[JsonSerializable(typeof(MintEmbedTokenRequest))]
[JsonSerializable(typeof(ConsoleContentItem))]
[JsonSerializable(typeof(ConsoleSessionContext))]
[JsonSerializable(typeof(ConsoleContentListResponse))]
[JsonSerializable(typeof(ConsoleActionCheckResponse))]
[JsonSerializable(typeof(ConsoleProvenanceChainResponse))]
[JsonSerializable(typeof(ConsoleShareProjection))]
[JsonSerializable(typeof(ConsolePublicLinkToken))]
[JsonSerializable(typeof(ConsoleEmbedToken))]
[JsonSerializable(typeof(ConsoleShareDependencyConflict))]
[JsonSerializable(typeof(ConsoleShareDependencyClosureResponse))]
[JsonSerializable(typeof(ConsolePublicLinkListResponse))]
[JsonSerializable(typeof(ConsolePublicLinkResolutionResponse))]
[JsonSerializable(typeof(ConsoleEmbedRedeemResponse))]
[JsonSerializable(typeof(ConsoleProvenanceRef))]
[JsonSerializable(typeof(ConsoleNavigationEntitlement))]
[JsonSerializable(typeof(ConsoleUserProfile))]
[JsonSerializable(typeof(ConsoleContentPage))]
// Per-itemType sidecar shapes, sourced for AOT-safe writes via SerializeToElement.
[JsonSerializable(typeof(ConsoleServiceTypeMetadata))]
[JsonSerializable(typeof(ConsoleLayerTypeMetadata))]
[JsonSerializable(typeof(ConsoleSavedMapTypeMetadata))]
[JsonSerializable(typeof(ConsoleDashboardTypeMetadata))]
[JsonSerializable(typeof(ConsoleReportTypeMetadata))]
[JsonSerializable(typeof(ConsoleGeneratedAppTypeMetadata))]
[JsonSerializable(typeof(ConsoleOpenDataTypeMetadata))]
internal sealed partial class ConsoleJsonContext : JsonSerializerContext
{
}
