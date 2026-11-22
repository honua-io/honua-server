// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleGetServiceMetadata(HttpContext context)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.ServiceMetadata, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var requestedFormat = context.Request.Query.TryGetValue("f", out var formatValue)
            ? formatValue.ToString()
            : null;

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out string? serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        IOptions<LimitsOptions> limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        ILoggerFactory loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("Honua.Server.FeatureServerEndpoints");

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId!,
            context,
            logger,
            cancellationToken).ConfigureAwait(false);
        if (!serviceValidationResult.IsValid)
        {
            return serviceValidationResult.ErrorResult!;
        }

        // Validate output format only after the service is confirmed to exist, so a
        // missing service returns 404 rather than a misleading 400 (e.g. f=html).
        if (!TryValidateOutputFormat(requestedFormat, JsonOnlyFormats, out _, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [formatError ?? "Output format is not supported."]);
        }

        var service = serviceValidationResult.Service!;

        // Materialize the visible publications/resources for this service. We need both
        // the policy-aware filter (deny anonymous/forbidden callers) and the metadata
        // builder's view to operate on the same set.
        var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var allPairs = GetRoutableFeaturePublicationsV2(service, snapshot, context);

        if (RequiresDefaultMetadataAuthenticationV2(
                context,
                service.AccessPolicy is not null,
                allPairs.Any(pair => pair.Resource.AccessPolicy is not null)))
        {
            return StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage);
        }

        var (visiblePairs, accessError) = await AccessPolicyHelpers.FilterAccessibleResourcesAsync(
            context,
            allPairs,
            static pair => pair.Resource,
            service,
            AuthorizationOperation.Metadata,
            cancellationToken).ConfigureAwait(false);
        if (accessError != null)
        {
            return accessError;
        }

        return await GetServiceMetadataAsync(
            context,
            service,
            visiblePairs,
            snapshot,
            limitsOptions.Value.Query,
            logger,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> GetServiceMetadataAsync(
        HttpContext context,
        MetadataV2Service service,
        IReadOnlyList<(MetadataV2Layer Publication, MetadataV2Object Resource)> publications,
        MetadataV2GraphSnapshot snapshot,
        QueryLimits limits,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            FeatureServerLog.ServiceMetadataRequested(logger, service.Metadata.Name);

            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var supportsGeobufOutput = featureReader is IGeobufFeatureStore;
            var supportsAttachmentUploads = HasAttachmentSurface(context.RequestServices);
            var branchVersioningEnabled = await HasBranchVersionedPublicationsAsync(
                context, service, publications, snapshot, cancellationToken).ConfigureAwait(false);
            var versionManagementEnabled = IsVersionManagementAvailable(context, branchVersioningEnabled);
            var offlineSyncEnabled = CapabilityFlagOptions.IsExperimentalEnabled(
                context.RequestServices.GetRequiredService<IConfiguration>(),
                "sync.offline");
            var versionManagementServerUrl = GeoServicesServiceUrls.RequestedLocation(
                context, service.EffectivePublicName, "VersionManagementServer");
            var maskedPublications = await HideMaskedFieldsAsync(context, publications, cancellationToken).ConfigureAwait(false);

            var cacheKey = FeatureServerMetadataCacheHelper.BuildServiceMetadataKey(new ServiceMetadataCacheKeyInput
            {
                Snapshot = snapshot,
                Service = service,
                Publications = maskedPublications,
                Limits = limits,
                SupportsGeobufOutput = supportsGeobufOutput,
                SupportsAttachmentUploads = supportsAttachmentUploads,
                BranchVersioningEnabled = branchVersioningEnabled,
                VersionManagementEnabled = versionManagementEnabled,
                OfflineSyncEnabled = offlineSyncEnabled,
                VersionManagementServerUrl = versionManagementServerUrl
            });

            var result = await MetadataDocumentByteCache.GetOrBuildJsonAsync(
                context,
                new JsonMetadataCacheRequest<FeatureServerResponse>
                {
                    CacheKey = cacheKey,
                    TypeInfo = FeatureServerJsonContext.Default.FeatureServerResponse,
                    BuildAsync = () => Task.FromResult(MapServiceToResponseV2(
                        service,
                        maskedPublications,
                        snapshot,
                        limits,
                        supportsGeobufOutput: supportsGeobufOutput,
                        supportsAttachmentUploads: supportsAttachmentUploads,
                        branchVersioningEnabled: branchVersioningEnabled,
                        versionManagementEnabled: versionManagementEnabled,
                        offlineSyncEnabled: offlineSyncEnabled,
                        versionManagementServerUrl: versionManagementServerUrl))
                }).ConfigureAwait(false);

            FeatureServerLog.ServiceMetadataReturned(logger, service.Metadata.Name, maskedPublications.Count);
            return result;
        }
        // Intentionally generic: this is a top-level protocol request handler; any
        // unexpected failure (parsing bugs, provider errors, etc.) must map to a
        // generic 500 rather than crash the host or leak internals to the client.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FeatureServerLog.ServiceMetadataFailed(logger, service.Metadata.Name, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "Service metadata retrieval failed");
        }
    }

    // The experimental VersionManagementServer gate does not disable FeatureServer gdbVersion queries or replication.
    internal static bool IsBranchVersioningAvailable(HttpContext context)
    {
        // Resolve defensively: the IVersionManager registration constructs a provider-specific manager
        // (e.g. PostgresVersionManager) that itself depends on IDatabaseConnectionProvider. When that
        // dependency is absent — read-only/non-database test hosts or providers without a connection
        // provider — the factory throws rather than returning null. Treat any resolution failure as
        // "versioning unavailable" so the DEFAULT (non-versioned) metadata stays byte-identical.
        IVersionManager? versionManager;
        try
        {
            versionManager = context.RequestServices.GetService<IVersionManager>();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (versionManager is not { SupportsVersioning: true })
        {
            return false;
        }

        return Honua.Infrastructure.Licensing.LicenseGate.IsEntitlementActive(
            context.RequestServices, FeatureCatalog.BranchVersioningKey);
    }

    internal static bool IsVersionManagementAvailable(HttpContext context, bool branchVersioningAvailable)
        => branchVersioningAvailable && CapabilityFlagOptions.IsExperimentalEnabled(
            context.RequestServices.GetRequiredService<IConfiguration>(), "versioning.branch");

    private static async Task<IResult> HandleLayerMetadata(HttpContext context)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.LayerMetadata, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var requestedFormat = context.Request.Query.TryGetValue("f", out var formatValue)
            ? formatValue.ToString()
            : null;
        if (!TryValidateOutputFormat(requestedFormat, JsonOnlyFormats, out _, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [formatError ?? "Output format is not supported."]);
        }

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out string? serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var layerError = RouteValidationHelpers.ValidateLayerId(context, out int layerId);
        if (layerError is not null)
        {
            return layerError;
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        IOptions<LimitsOptions> limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        ILoggerFactory loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("Honua.Server.FeatureServerEndpoints");

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
            resourceValidator,
            serviceId!,
            layerId,
            context,
            logger,
            cancellationToken).ConfigureAwait(false);
        if (!validationResult.IsValid)
        {
            return validationResult.ErrorResult!;
        }

        var service = validationResult.Service!;
        var publication = validationResult.Publication!;
        var resource = validationResult.Resource!;
        if (RequiresDefaultMetadataAuthenticationV2(
                context,
                service.AccessPolicy is not null,
                resource.AccessPolicy is not null))
        {
            return StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage);
        }

        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, resource, AuthorizationOperation.Metadata, service, cancellationToken).ConfigureAwait(false);
        if (accessError != null)
        {
            return accessError;
        }

        var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        return await FetchLayerMetadataAsync(
            context,
            serviceId!,
            service,
            publication,
            resource,
            snapshot,
            limitsOptions.Value.Query,
            logger,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> FetchLayerMetadataAsync(
        HttpContext context,
        string serviceId,
        MetadataV2Service service,
        MetadataV2Layer publication,
        MetadataV2Object resource,
        MetadataV2GraphSnapshot snapshot,
        QueryLimits limits,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolvedLayerId = publication.LayerIndex
                ?? snapshot.ResolveStorageLayerId(resource)
                ?? -1;
            FeatureServerLog.LayerMetadataRequested(logger, serviceId, resolvedLayerId);

            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var supportsGeobufOutput = featureReader is IGeobufFeatureStore;
            var supportsAttachmentUploads = HasAttachmentSurface(context.RequestServices);
            var offlineSyncEnabled = CapabilityFlagOptions.IsExperimentalEnabled(
                context.RequestServices.GetRequiredService<IConfiguration>(),
                "sync.offline");
            var extrusionValidationErrors = ValidateExtrusionInfoV2(resource, out var extrusionInfo);
            if (extrusionValidationErrors.Count > 0)
            {
                return StandardErrorHelpers.CreateUnprocessableEntity(
                    context,
                    "Layer extrusion metadata is invalid.",
                    extrusionValidationErrors);
            }

            var schemaResource = await HideMaskedFieldsAsync(context, resource, cancellationToken).ConfigureAwait(false);
            var branchVersioningEnabled = await IsPublicationBranchVersioningAvailableAsync(
                context, service, resource, publication, snapshot, cancellationToken).ConfigureAwait(false);
            var (metadataResource, accessibleRelatedIds) = await ResolveAuthorizedRelationshipsResourceAsync(
                context,
                new RelationshipAccessContext(service, resource, schemaResource, snapshot),
                cancellationToken).ConfigureAwait(false);

            var cacheKey = FeatureServerMetadataCacheHelper.BuildLayerMetadataKey(new LayerMetadataCacheKeyInput
            {
                Snapshot = snapshot,
                Service = service,
                Publication = publication,
                SchemaResource = schemaResource,
                Limits = limits,
                SupportsGeobufOutput = supportsGeobufOutput,
                SupportsAttachmentUploads = supportsAttachmentUploads,
                BranchVersioningEnabled = branchVersioningEnabled,
                OfflineSyncEnabled = offlineSyncEnabled,
                AccessibleRelatedResourceIds = accessibleRelatedIds
            });

            var result = await MetadataDocumentByteCache.GetOrBuildJsonAsync(
                context,
                new JsonMetadataCacheRequest<LayerResponse>
                {
                    CacheKey = cacheKey,
                    TypeInfo = FeatureServerJsonContext.Default.LayerResponse,
                    BuildAsync = async () =>
                    {
                        var timeInfo = GeoServicesWithheldFields.WithoutWithheldTimeFields(schemaResource, await BuildTimeInfoV2Async(
                            resource,
                            publication,
                            snapshot,
                            featureReader,
                            cancellationToken).ConfigureAwait(false));
                        var drawingInfo = GeoServicesWithheldFields.WithoutWithheldReferences(
                            ResolveDrawingInfoV2(resource, snapshot), schemaResource) ?? BuildDefaultDrawingInfoV2(resource);
                        var popupInfo = GeoServicesWithheldFields.WithoutWithheldReferences(ResolvePopupInfoV2(resource), schemaResource);

                        // Edit constraints (composite ownership) come from every relationship, including ones the
                        // caller cannot see; only the advertised relationship list is filtered.
                        return MapLayerToResponseV2(
                            service,
                            schemaResource,
                            publication,
                            snapshot,
                            limits,
                            timeInfo,
                            drawingInfo: drawingInfo,
                            popupInfo: popupInfo,
                            extrusionInfo: WithoutWithheldExtrusion(schemaResource, extrusionInfo),
                            supportsGeobufOutput: supportsGeobufOutput,
                            supportsAttachmentUploads: supportsAttachmentUploads,
                            branchVersioningEnabled: branchVersioningEnabled,
                            offlineSyncEnabled: offlineSyncEnabled,
                            authorizedRelationships: BuildRelationshipResponseV2(metadataResource, snapshot, service));
                    }
                }).ConfigureAwait(false);

            FeatureServerLog.LayerMetadataReturned(logger, serviceId, resolvedLayerId, resource.Metadata.Name);
            return result;
        }
        // Intentionally generic: this is a top-level protocol request handler; any
        // unexpected failure (parsing bugs, provider errors, etc.) must map to a
        // generic 500 rather than crash the host or leak internals to the client.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var resolvedLayerId = publication.LayerIndex
                ?? snapshot.ResolveStorageLayerId(resource)
                ?? -1;
            FeatureServerLog.LayerMetadataFailed(logger, serviceId, resolvedLayerId, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "Layer metadata retrieval failed");
        }
    }

    private readonly record struct RelationshipAccessContext(
        MetadataV2Service Service,
        MetadataV2Object Resource,
        MetadataV2Object SchemaResource,
        MetadataV2GraphSnapshot Snapshot);

    private static async Task<(MetadataV2Object MetadataResource, IReadOnlyList<string> AccessibleRelatedIds)>
        ResolveAuthorizedRelationshipsResourceAsync(
            HttpContext context,
            RelationshipAccessContext input,
            CancellationToken cancellationToken)
    {
        if (input.Resource.Relationships.Count == 0)
        {
            return (input.SchemaResource, Array.Empty<string>());
        }

        var allPairs = GetRoutableFeaturePublicationsV2(input.Service, input.Snapshot, context);
        var access = await AccessPolicyHelpers.EvaluateResourceAccessSetAsync(
            context, allPairs.Select(pair => pair.Resource), input.Service,
            AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false);
        var accessibleResources = FilterAccessibleLayersV2(access, input.Snapshot, allPairs)
            .Select(pair => pair.Resource.Metadata.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filtered = new List<MetadataV2Relationship>(input.Resource.Relationships.Count);
        var relatedIds = new List<string>(input.Resource.Relationships.Count);
        foreach (var relationship in input.Resource.Relationships)
        {
            if (accessibleResources.Contains(relationship.RelatedResourceId))
            {
                filtered.Add(relationship);
                relatedIds.Add(relationship.RelatedResourceId);
            }
        }

        relatedIds.Sort(StringComparer.OrdinalIgnoreCase);
        return (input.SchemaResource with { Relationships = filtered }, relatedIds);
    }

    // Field names the caller's masks withhold are left out of schema listings, as hidden fields are.
    private static Task<MetadataV2Object> HideMaskedFieldsAsync(
        HttpContext context, MetadataV2Object resource, CancellationToken cancellationToken)
        => GeoServicesWithheldFields.HideMaskedFieldsAsync(context, resource, cancellationToken);

    private static async Task<IReadOnlyList<(MetadataV2Layer Publication, MetadataV2Object Resource)>> HideMaskedFieldsAsync(
        HttpContext context,
        IReadOnlyList<(MetadataV2Layer Publication, MetadataV2Object Resource)> publications,
        CancellationToken cancellationToken)
    {
        var visible = new List<(MetadataV2Layer Publication, MetadataV2Object Resource)>(publications.Count);
        foreach (var (publication, resource) in publications)
        {
            visible.Add((publication, await HideMaskedFieldsAsync(context, resource, cancellationToken).ConfigureAwait(false)));
        }

        return visible;
    }

    // Extrusion over a withheld field would name it and draw its values as heights.
    private static FeatureServerExtrusionInfo? WithoutWithheldExtrusion(
        MetadataV2Object schemaResource, FeatureServerExtrusionInfo? extrusionInfo)
        => schemaResource.IsHiddenSchemaField(extrusionInfo?.HeightField)
           || schemaResource.IsHiddenSchemaField(extrusionInfo?.BaseHeightField)
            ? null
            : extrusionInfo;

    private static IReadOnlyList<string> ValidateExtrusionInfoV2(
        MetadataV2Object resource,
        out FeatureServerExtrusionInfo? extrusionInfo)
    {
        extrusionInfo = null;

        if (resource.Extrusion is null)
        {
            return Array.Empty<string>();
        }

        var errors = MetadataV2ExtrusionValidator.Validate(resource.Extrusion, resource.SchemaFields);
        if (errors.Count > 0)
        {
            return errors;
        }

        MetadataV2VerticalUnits.TryNormalize(resource.Extrusion.Unit, out var normalizedUnit);
        extrusionInfo = new FeatureServerExtrusionInfo
        {
            Enabled = true,
            HeightField = resource.Extrusion.HeightField,
            BaseHeightField = resource.Extrusion.BaseHeightField,
            Unit = normalizedUnit,
            DefaultHeight = resource.Extrusion.DefaultHeight,
            MaterialHint = resource.Extrusion.MaterialHint
        };
        return Array.Empty<string>();
    }

    private static JsonElement? ResolveDrawingInfoV2(
        MetadataV2Object resource,
        MetadataV2GraphSnapshot snapshot)
    {
        if (TryResolveDrawingInfoExtension(resource, out var extensionDrawingInfo))
        {
            return extensionDrawingInfo;
        }

        foreach (var styleResourceId in resource.StyleResourceIds ?? Array.Empty<string>())
        {
            if (!snapshot.Index.ResourcesById.TryGetValue(styleResourceId, out var styleResource)
                || styleResource.Type != MetadataV2ObjectType.Style
                || styleResource.Style is null)
            {
                continue;
            }

            foreach (var encoding in styleResource.Style.Encodings ?? Array.Empty<MetadataV2StyleEncoding>())
            {
                if (!string.Equals(encoding.Encoding, "esri-drawing-info", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(encoding.Body))
                {
                    continue;
                }

                if (TryParseJsonElement(encoding.Body, out var drawingInfo))
                {
                    return drawingInfo;
                }
            }
        }

        // No author-supplied drawingInfo (extension or saved style): synthesize a
        // default simple renderer keyed by geometry type. Esri FeatureServers always
        // return drawingInfo for renderable layers, and Esri clients (ArcGIS Pro, the
        // ArcGIS Maps SDKs, ArcGIS API for Python, the JS API) read it to draw
        // features. Omitting it leaves those clients with no server symbology.
        return BuildDefaultDrawingInfoV2(resource);
    }

    private static JsonElement? BuildDefaultDrawingInfoV2(MetadataV2Object resource)
    {
        var geometryType = resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None;
        var symbol = BuildSimpleSymbol(geometryType);
        if (symbol is null)
        {
            return null;
        }

        var drawingInfo = new Dictionary<string, object?>
        {
            ["renderer"] = new Dictionary<string, object?>
            {
                ["type"] = "simple",
                ["symbol"] = symbol,
                ["label"] = string.Empty,
                ["description"] = string.Empty,
            },
            ["transparency"] = 0,
            ["labelingInfo"] = null,
        };

        return JsonSerializer.SerializeToElement(
            drawingInfo,
            FeatureServerJsonContext.Default.DictionaryStringObject);
    }

    private static bool TryResolveDrawingInfoExtension(
        MetadataV2Object resource,
        out JsonElement drawingInfo)
    {
        if (resource.Extensions is not null
            && (resource.Extensions.TryGetValue("geoservices:drawingInfo", out drawingInfo)
                || resource.Extensions.TryGetValue("drawingInfo", out drawingInfo))
            && drawingInfo.ValueKind != JsonValueKind.Null
            && drawingInfo.ValueKind != JsonValueKind.Undefined)
        {
            return true;
        }

        drawingInfo = default;
        return false;
    }

    private static JsonElement? ResolvePopupInfoV2(MetadataV2Object resource)
    {
        if (resource.Extensions is not null
            && (resource.Extensions.TryGetValue("geoservices:popupInfo", out var popupInfo)
                || resource.Extensions.TryGetValue("popupInfo", out popupInfo))
            && popupInfo.ValueKind != JsonValueKind.Null
            && popupInfo.ValueKind != JsonValueKind.Undefined)
        {
            return popupInfo;
        }

        return null;
    }

    private static bool TryParseJsonElement(string json, out JsonElement element)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            element = document.RootElement.Clone();
            return element.ValueKind != JsonValueKind.Null && element.ValueKind != JsonValueKind.Undefined;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    // In production, authentication is required when neither the service nor any layer declares an access policy, so an unconfigured service does not leak metadata; non-production still allows anonymous metadata reads.
    private static bool RequiresDefaultMetadataAuthenticationV2(
        HttpContext context,
        bool hasServicePolicy,
        bool hasLayerPolicy)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return false;
        }

        var environment = context.RequestServices.GetRequiredService<IHostEnvironment>();
        if (!environment.IsProduction())
        {
            return false;
        }

        return !hasServicePolicy && !hasLayerPolicy;
    }
}
