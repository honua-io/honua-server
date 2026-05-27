// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Metadata handlers for FeatureServer endpoints. Ported to V2 metadata (issue #1035
/// cutover 92/N): the canonical (service, publication, resource) triple is resolved
/// through the V2 graph and responses are built via the V2 overloads on
/// <see cref="FeatureServerEndpoints"/> (see <c>FeatureServerUtilities.V2.cs</c>).
/// </summary>
internal static partial class FeatureServerEndpoints
{
    /// <summary>
    /// Handle service metadata requests
    /// </summary>
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

        var service = serviceValidationResult.Service!;

        // Materialize the visible publications/resources for this service. We need both
        // the policy-aware filter (deny anonymous/forbidden callers) and the metadata
        // builder's view to operate on the same set.
        var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var allPairs = snapshot.Index.PublicationsByService[service.Metadata.Id]
            .Select(pub => (Publication: pub, Resource: snapshot.ResolveResource(pub)))
            .Where(pair => pair.Resource is not null)
            .Select(pair => (pair.Publication, Resource: pair.Resource!))
            .ToArray();

        if (RequiresDefaultMetadataAuthenticationV2(
                context,
                service.AccessPolicy is not null,
                allPairs.Any(pair => pair.Resource.AccessPolicy is not null)))
        {
            return StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage);
        }

        var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
            context,
            allPairs.Select(pair => pair.Resource),
            service);
        if (accessError != null)
        {
            return accessError;
        }

        var visiblePairs = FilterAccessibleLayersV2(context, service, allPairs);

        return await GetServiceMetadataAsync(
            context,
            service,
            visiblePairs,
            snapshot,
            limitsOptions.Value.Query,
            logger).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets service metadata asynchronously
    /// </summary>
    private static Task<IResult> GetServiceMetadataAsync(
        HttpContext context,
        MetadataV2Service service,
        IReadOnlyList<(MetadataV2Publication Publication, MetadataV2Resource Resource)> publications,
        MetadataV2GraphSnapshot snapshot,
        QueryLimits limits,
        ILogger logger)
    {
        try
        {
            FeatureServerLog.ServiceMetadataRequested(logger, service.Metadata.Name);

            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var supportsAttachmentUploads = HasAttachmentSurface(context.RequestServices);
            FeatureServerResponse response = MapServiceToResponseV2(
                service,
                publications,
                snapshot,
                limits,
                supportsGeobufOutput: featureReader is IGeobufFeatureStore,
                supportsAttachmentUploads: supportsAttachmentUploads);

            FeatureServerLog.ServiceMetadataReturned(logger, service.Metadata.Name, response.Layers.Length);

            return Task.FromResult(Results.Json(response, FeatureServerJsonContext.Default.FeatureServerResponse,
                contentType: "application/json"));
        }
        catch (Exception ex)
        {
            FeatureServerLog.ServiceMetadataFailed(logger, service.Metadata.Name, ex.Message, ex);
            return Task.FromResult(StandardErrorHelpers.CreateInternalServerError(
                context,
                "Service metadata retrieval failed"));
        }
    }

    /// <summary>
    /// Handle layer metadata requests
    /// </summary>
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

        var accessError = AccessPolicyHelpers.RequireResourceAccess(context, resource, service);
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

    /// <summary>
    /// Fetches layer metadata asynchronously
    /// </summary>
    private static async Task<IResult> FetchLayerMetadataAsync(
        HttpContext context,
        string serviceId,
        MetadataV2Service service,
        MetadataV2Publication publication,
        MetadataV2Resource resource,
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
            var supportsAttachmentUploads = HasAttachmentSurface(context.RequestServices);
            var timeInfo = await BuildTimeInfoV2Async(
                resource,
                publication,
                snapshot,
                featureReader,
                cancellationToken).ConfigureAwait(false);

            var drawingInfo = ResolveDrawingInfoV2(resource, snapshot);

            var extrusionValidationErrors = ValidateExtrusionInfoV2(resource, out var extrusionInfo);
            if (extrusionValidationErrors.Count > 0)
            {
                return StandardErrorHelpers.CreateUnprocessableEntity(
                    context,
                    "Layer extrusion metadata is invalid.",
                    extrusionValidationErrors);
            }

            LayerResponse response = MapLayerToResponseV2(
                service,
                resource,
                publication,
                snapshot,
                limits,
                timeInfo,
                drawingInfo: drawingInfo,
                extrusionInfo: extrusionInfo,
                supportsGeobufOutput: featureReader is IGeobufFeatureStore,
                supportsAttachmentUploads: supportsAttachmentUploads);

            FeatureServerLog.LayerMetadataReturned(logger, serviceId, resolvedLayerId, resource.Metadata.Name);

            return Results.Json(response, FeatureServerJsonContext.Default.LayerResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
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

    private static IReadOnlyList<string> ValidateExtrusionInfoV2(
        MetadataV2Resource resource,
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
        MetadataV2Resource resource,
        MetadataV2GraphSnapshot snapshot)
    {
        if (TryResolveDrawingInfoExtension(resource, out var extensionDrawingInfo))
        {
            return extensionDrawingInfo;
        }

        foreach (var styleResourceId in resource.StyleResourceIds ?? Array.Empty<string>())
        {
            if (!snapshot.Index.ResourcesById.TryGetValue(styleResourceId, out var styleResource)
                || styleResource.Type != MetadataV2ResourceType.Style
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

        return null;
    }

    private static bool TryResolveDrawingInfoExtension(
        MetadataV2Resource resource,
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

    /// <summary>
    /// V2 variant of <c>RequiresDefaultMetadataAuthentication</c>. Identical contract:
    /// in production, require authentication when neither the service nor any layer
    /// declares an explicit access policy (so unconfigured services don't leak metadata
    /// to anonymous callers). In non-production environments, anonymous access is
    /// allowed so dev/test loops can hit metadata without setting up identity.
    /// </summary>
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
