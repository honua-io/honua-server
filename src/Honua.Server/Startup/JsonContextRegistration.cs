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
                Honua.Protocols.Ogc.Api.Tiles.OgcTilesJsonContext.Default,
                Honua.Server.Features.Admin.Models.SecureConnectionJsonContext.Default,
                Honua.Server.Features.Admin.Models.LayerPublishingJsonContext.Default,
                Honua.Server.Features.Admin.Models.ServiceSettingsJsonContext.Default,
                Honua.Core.Features.Metadata.Domain.V2.MetadataReleaseJsonContext.Default,
                Honua.Server.Features.Admin.Models.MetadataPrevalidationJsonContext.Default,
                Honua.Core.Features.Publishing.Content.Domain.ContentPublicationJsonContext.Default,
                Honua.Server.Features.Temporal.TemporalHistoryApiJsonContext.Default,
                Honua.Server.Features.Admin.Models.DeployControlJsonContext.Default,
                Honua.Infrastructure.Monitoring.MetricsJsonContext.Default,
                Honua.Import.FileImport.ImportJsonContext.Default,
                Honua.Import.RasterImport.RasterImportJsonContext.Default,
                Honua.Migration.GeoservicesImportApiJsonContext.Default,
                Honua.Migration.OgcWfsImportJsonContext.Default,
                Honua.Migration.OgcCoverageImportJsonContext.Default,
                Honua.Migration.OgcWcsImportJsonContext.Default,
                Honua.Migration.OgcTileCacheExportJsonContext.Default,
                Honua.Server.Features.Admin.OperationsProgressJsonContext.Default,
                Honua.Server.Features.Admin.FeatureEventReplayJsonContext.Default,
                Honua.Server.Features.Mobile.Auth.MobileAuthJsonContext.Default,
                Honua.Server.Features.Mobile.Diagnostics.MobileExceptionIngestionJsonContext.Default,
                Honua.Server.Features.Mobile.FieldCollection.FieldCollectionSyncJsonContext.Default,
                Honua.Core.Features.Forms.Packages.FormPackageJsonContext.Default,
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
                Honua.Server.Features.Console.Models.ConsoleJsonContext.Default,
                Honua.Server.Features.Admin.Share.ShareAdminJsonContext.Default,
                Honua.Server.Features.Studio.Models.StudioApiJsonContext.Default,
                Honua.Core.Features.Studio.Domain.StudioJsonContext.Default,
                Honua.Ai.AnalysisContent.AnalysisContentApiJsonContext.Default,
                Honua.Server.Features.Capabilities.Models.CapabilityManifestJsonContext.Default,
                Honua.Server.Features.WorkflowPackages.WorkflowPackagesJsonContext.Default,
                Honua.Server.Features.Admin.Models.AdminApiKeyJsonContext.Default,
                Honua.Server.Features.Admin.Models.SceneDatasetJsonContext.Default,
                Honua.Server.Features.Admin.Models.SceneGenerationJsonContext.Default,
                Honua.Protocols.Scene.Models.PublicSceneDiscoveryJsonContext.Default,
                Honua.Server.Features.Admin.Models.RateLimitJsonContext.Default,
                Honua.Server.Features.Admin.Models.TableDiscoveryJsonContext.Default,
                Honua.Server.Features.Admin.Models.ExternalServiceDiscoveryJsonContext.Default,
                Honua.Server.Features.Admin.Models.AdminAuthJsonContext.Default,
                Honua.Server.Features.Admin.Models.ConfigurationJsonContext.Default,
                Honua.Server.Features.Admin.Models.LicenseAdminJsonContext.Default,
                Honua.Infrastructure.Licensing.LicenseFileJsonContext.Default,
                Honua.Server.Features.Admin.Models.IdentityAdminJsonContext.Default,
                Honua.Server.Features.Admin.Models.CacheAdminJsonContext.Default,
                Honua.Server.Features.Admin.Models.GeocodingAdminJsonContext.Default,
                Honua.Server.Features.Admin.Models.FeatureOverviewJsonContext.Default,
                Honua.Server.Features.Admin.Models.ComplianceAdminJsonContext.Default,
                Honua.Server.Features.Admin.Models.CacheOperationsJsonContext.Default,
                Honua.Server.Features.Admin.Models.StreamingOperationsJsonContext.Default,
                Honua.Server.Features.Admin.Models.GeocodingOperationsJsonContext.Default,
                Honua.PackageReview.PackageReviewJsonContext.Default,
                Honua.Server.Features.CloudDemo.CloudDemoJsonContext.Default,
                Honua.Server.Features.HealthCheck.HealthJsonContext.Default,
                Honua.Core.Features.Validation.Contracts.ValidationContractJsonContext.Default,
                Honua.Infrastructure.Models.ProblemJsonContext.Default,
                Honua.Infrastructure.Middleware.LimitsEnforcementJsonContext.Default,
                Honua.Infrastructure.Security.CspViolationJsonContext.Default,
                Honua.Protocols.GeoServices.GeometryService.Models.GeometryServiceJsonContext.Default,
                Honua.Protocols.GeoServices.NAServer.Models.NAServerJsonContext.Default,
                Honua.Io.Export.ExportJsonContext.Default,
                Honua.Protocols.Stac.StacJsonContext.Default,
                Honua.Server.Features.Protocols.Cog.CogJsonContext.Default,
                Honua.Server.Features.Protocols.Coverages.Multidimensional.MultidimensionalCoverageJsonContext.Default,
                Honua.Server.Features.Protocols.Zarr.ZarrJsonContext.Default,
                Honua.Server.Features.Protocols.SpatialAnalytics.Models.SpatialAnalyticsJsonContext.Default,
                Honua.Server.Features.Collaboration.Sessions.CollaborationSessionJsonContext.Default,
                Honua.Server.Features.Collaboration.FeatureLocks.FeatureLockJsonContext.Default,
                Honua.Core.Features.Authorization.Domain.OperatorAuthorizationJsonContext.Default,
                Honua.Protocols.Ogc.Api.Processes.OgcProcessesJsonContext.Default);
        });

        return services;
    }
}
