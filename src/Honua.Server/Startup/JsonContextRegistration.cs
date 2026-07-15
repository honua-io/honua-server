// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization.Metadata;

namespace Honua.Server.Startup;

/// <summary>
/// Registers source-generated <see cref="JsonTypeInfoResolver"/> entries with ASP.NET Core's
/// minimal-API JSON pipeline. Splitting this off keeps <c>Program.cs</c> manageable while
/// preserving the exact resolver order (the first matching context wins per type).
/// </summary>
internal static class JsonContextRegistration
{
    public static IServiceCollection AddHonuaJsonContexts(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine(
        Honua.Protocols.GeoServices.FeatureServer.Models.FeatureServerJsonContext.Default,
        Honua.Protocols.GeoServices.ImageServer.Models.ImageServerJsonContext.Default,
        Honua.Protocols.OData.Models.ODataJsonContext.Default,
        Honua.Protocols.Ogc.Api.Coverages.Models.OgcCoveragesJsonContext.Default,
        Honua.Protocols.Ogc.Api.Features.OgcJsonContext.Default,
        Honua.Protocols.Ogc.Api.Maps.Models.OgcMapsJsonContext.Default,
        Honua.Protocols.Ogc.Api.Records.OgcRecordsJsonContext.Default,
        Honua.Protocols.Ogc.Api.Styles.OgcStylesJsonContext.Default,
        Honua.Protocols.Ogc.Api.Tiles.OgcTilesJsonContext.Default,
        Honua.Server.Features.Admin.Models.SecureConnectionJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerPublishingJsonContext.Default,
        Honua.Server.Features.Admin.Models.ServiceSettingsJsonContext.Default,
        Honua.Core.Features.Metadata.Domain.V2.MetadataReleaseJsonContext.Default,
        Honua.Server.Features.Admin.Models.MetadataPrevalidationJsonContext.Default,
        Honua.Core.Features.Publishing.Content.Domain.ContentPublicationJsonContext.Default,
        Honua.Server.Features.Admin.Models.DeployControlJsonContext.Default,
        Honua.Server.Features.Admin.Models.CoordinatedReleaseJsonContext.Default,
        Honua.Infrastructure.Monitoring.MetricsJsonContext.Default,
        Honua.Infrastructure.Monitoring.OpsObservabilityJsonContext.Default,
        Honua.Import.FileImport.ImportJsonContext.Default,
        Honua.Import.RasterImport.RasterImportJsonContext.Default,
        Honua.Migration.GeoservicesImportApiJsonContext.Default,
        Honua.Migration.OgcWfsImportJsonContext.Default,
        Honua.Migration.OgcCoverageImportJsonContext.Default,
        Honua.Migration.OgcWcsImportJsonContext.Default,
        Honua.Server.Features.Admin.OperationsProgressJsonContext.Default,
        Honua.Server.Features.Admin.FeatureEventReplayJsonContext.Default,
        Honua.Server.Features.Mobile.Auth.MobileAuthJsonContext.Default,
        Honua.Server.Features.Mobile.Diagnostics.MobileExceptionIngestionJsonContext.Default,
        Honua.Server.Features.Mobile.FieldCollection.FieldCollectionSyncJsonContext.Default,
        Honua.Server.Features.Admin.TileOperations.TileOperationsJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerStyleJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerFieldConfigurationJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerAuthoringJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerValidationJsonContext.Default,
        Honua.Server.Features.Admin.Models.StyleSuggestionJsonContext.Default,
        Honua.Server.Features.Admin.Models.AlertAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.LicenseJsonContext.Default,
        Honua.Server.Features.Admin.Models.OidcProviderJsonContext.Default,
        Honua.Server.Features.Admin.Models.UserManagementJsonContext.Default,
        Honua.Server.Features.Admin.Models.RoleJsonContext.Default,
        Honua.Server.Features.Admin.Models.RlsPolicyJsonContext.Default,
        Honua.Server.Features.Admin.Models.FieldMaskPolicyJsonContext.Default,
        Honua.Server.Features.Admin.Models.ProposalJsonContext.Default,
        Honua.Server.Features.Console.Models.ConsoleJsonContext.Default,
        Honua.Server.Features.Console.Collaboration.Models.StudioMapCollaborationJsonContext.Default,
        Honua.Server.Features.Studio.Models.StudioApiJsonContext.Default,
        Honua.Core.Features.Studio.Domain.StudioJsonContext.Default,
        Honua.Ai.AnalysisContent.AnalysisContentApiJsonContext.Default,
        Honua.Server.Features.Capabilities.Models.CapabilityManifestJsonContext.Default,
        Honua.Server.Features.WorkflowPackages.WorkflowPackagesJsonContext.Default,
        Honua.Server.Features.Operations.OperationsJsonContext.Default,
        Honua.Server.Features.Admin.Models.AdminApiKeyJsonContext.Default,
        Honua.Server.Features.Admin.EmbedGovernance.Models.EmbedGovernanceJsonContext.Default,
        Honua.Server.Features.Admin.Models.OAuthClientJsonContext.Default,
        Honua.Server.Features.Admin.Models.SceneDatasetJsonContext.Default,
        Honua.Server.Features.Admin.Models.NetworkDatasetAdminJsonContext.Default,
        Honua.Server.Features.Admin.Routing.NetworkTopologyRebuildAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.SceneGenerationJsonContext.Default,
        Honua.Server.Features.Admin.Models.SceneBimIngestJsonContext.Default,
        Honua.Server.Features.Admin.Models.ScenePointCloudIngestJsonContext.Default,
        Honua.Protocols.Scene.Models.PublicSceneDiscoveryJsonContext.Default,
        Honua.Server.Features.Admin.Models.RateLimitJsonContext.Default,
        Honua.Server.Features.Admin.Models.TenantJsonContext.Default,
        Honua.Server.Features.Admin.Models.TableDiscoveryJsonContext.Default,
        Honua.Server.Features.Admin.Models.ExternalServiceDiscoveryJsonContext.Default,
        Honua.Server.Features.Admin.Models.AdminAuthJsonContext.Default,
        Honua.Server.Features.Admin.Models.ClientCertificateJsonContext.Default,
        Honua.Server.Features.Admin.Models.ConfigurationJsonContext.Default,
        Honua.Server.Features.Admin.Models.LicenseAdminJsonContext.Default,
        Honua.Infrastructure.Licensing.LicenseFileJsonContext.Default,
        Honua.Server.Features.Admin.Models.IdentityAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.CacheAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.GeocodingAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.FeatureOverviewJsonContext.Default,
        Honua.Server.Features.Admin.Models.CacheOperationsJsonContext.Default,
        Honua.Server.Features.Admin.Models.StreamingOperationsJsonContext.Default,
        Honua.Server.Features.Admin.Models.GeocodingOperationsJsonContext.Default,
        Honua.PackageReview.PackageReviewJsonContext.Default,
        Honua.Server.Features.CloudDemo.CloudDemoJsonContext.Default,
        Honua.Server.Features.HealthCheck.HealthJsonContext.Default,
        Honua.Infrastructure.Models.ProblemJsonContext.Default,
        Honua.Core.Features.Validation.Contracts.ValidationContractJsonContext.Default,
        Honua.Infrastructure.Authentication.ClientCertificates.ClientCertificateInfrastructureJsonContext.Default,
        Honua.Infrastructure.Middleware.LimitsEnforcementJsonContext.Default,
        Honua.Infrastructure.Security.CspViolationJsonContext.Default,
        Honua.Protocols.GeoServices.GeometryService.Models.GeometryServiceJsonContext.Default,
        Honua.Protocols.GeoServices.NAServer.Models.NAServerJsonContext.Default,
        Honua.Io.Export.ExportJsonContext.Default,
        Honua.Protocols.Stac.StacJsonContext.Default,
        Honua.Protocols.SensorThings.SensorThingsJsonContext.Default,
        Honua.Server.Features.Protocols.Cog.CogJsonContext.Default,
        Honua.Server.Features.Protocols.Coverages.Multidimensional.MultidimensionalCoverageJsonContext.Default,
        Honua.Server.Features.Protocols.Zarr.ZarrJsonContext.Default,
        Honua.Server.Features.Protocols.SpatialAnalytics.Models.SpatialAnalyticsJsonContext.Default,
        Honua.Server.Features.Protocols.Elevation.SceneAnalysisJsonContext.Default,
        Honua.Server.Features.Protocols.Elevation.VisibilityJsonContext.Default,
        Honua.Server.Features.Collaboration.Sessions.CollaborationSessionJsonContext.Default,
        Honua.Server.Features.Collaboration.FeatureLocks.FeatureLockJsonContext.Default,
        Honua.Server.Features.Collaboration.Operations.SavedMapOperationJsonContext.Default,
        Honua.Core.Features.Authorization.Domain.OperatorAuthorizationJsonContext.Default,
        Honua.Server.Features.Admin.ObservabilityJsonContext.Default,
        Honua.Server.Features.Admin.InvestigationJsonContext.Default,
        Honua.Server.Features.Admin.Share.ShareAdminJsonContext.Default,
        Honua.Protocols.Ogc.Api.Processes.OgcProcessesJsonContext.Default,
        // Temporal history slice 2-5 request/response bodies (#1166): registered for [FromBody]
        // deserialization through the HTTP JSON options chain.
        Honua.Server.Features.Temporal.TemporalHistorySlicesJsonContext.Default,
        // Data-enrichment admin DTOs (#2280): RegisterEnrichmentDatasetRequest /
        // UpdateEnrichmentDatasetRequest are [FromBody]-bound, so the source-generated context
        // must be part of the minimal-API resolver or the host aborts at startup.
        Honua.Server.Features.DataEnrichment.Models.EnrichmentJsonContext.Default);
        });

        return services;
    }
}
