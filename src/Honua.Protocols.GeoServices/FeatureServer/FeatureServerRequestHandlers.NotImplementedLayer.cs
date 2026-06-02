// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.ServiceDefaults;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Layer-level FeatureServer operations that Honua exposes as thin, spec-shaped
/// protocol adapters even though the underlying data model is not yet implemented:
/// layer asset storage (<c>hasAssets</c>, <c>queryAssets</c>, <c>cleanupAssets</c>,
/// <c>uploadAssets</c>), 3D geometry (<c>convert3D</c>, <c>query3D</c>), and
/// layer-metadata document updates (<c>metadata/update</c>).
///
/// Each handler resolves the (service, layer) pair through the shared validation
/// pipeline and enforces the appropriate access scope, then returns the Esri
/// specification's response shape with honest, empty content. Operations that would
/// require a store Honua does not have (asset upload, 3D conversion/query, metadata
/// persistence) return spec-compliant errors rather than fabricating success.
/// </summary>
internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleHasAssets(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var (validation, accessError) = await ResolveLayerForReadAsync(serviceId, layerId, context, "hasAssets");
        if (accessError != null)
        {
            return accessError;
        }

        _ = validation;
        return Results.Json(
            new HasAssetsResponse { HasAssets = false },
            FeatureServerJsonContext.Default.HasAssetsResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleQueryAssets(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var (validation, accessError) = await ResolveLayerForReadAsync(serviceId, layerId, context, "queryAssets");
        if (accessError != null)
        {
            return accessError;
        }

        _ = validation;
        return Results.Json(
            new QueryAssetsResponse(),
            FeatureServerJsonContext.Default.QueryAssetsResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleCleanupAssets(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        // cleanupAssets is a maintenance operation; require write access so it mirrors
        // the authorization of a real asset-cleanup. With no asset store, it is a no-op.
        var accessError = await RequireLayerWriteAccessBeforeBodyAsync(
            serviceId, layerId, context, GetTimeoutAwareCancellationToken(context));
        if (accessError != null)
        {
            return accessError;
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "cleanupAssets",
            HonuaTelemetry.Protocols.FeatureServer,
            layerId.ToString(CultureInfo.InvariantCulture),
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        scope.SetSuccess(0);

        return Results.Json(
            new CleanupAssetsResponse(),
            FeatureServerJsonContext.Default.CleanupAssetsResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleUploadAssets(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var accessError = await RequireLayerWriteAccessBeforeBodyAsync(
            serviceId, layerId, context, GetTimeoutAwareCancellationToken(context));
        if (accessError != null)
        {
            return accessError;
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "uploadAssets",
            HonuaTelemetry.Protocols.FeatureServer,
            layerId.ToString(CultureInfo.InvariantCulture),
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        scope.WithTag("honua.result", "not_supported");

        return StandardErrorHelpers.CreateBadRequest(
            context,
            "Asset uploads are not supported by this layer.",
            ["This server does not provide a layer asset store; uploadAssets has nowhere to persist assets."]);
    }

    private static async Task<IResult> HandleConvert3D(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var (validation, accessError) = await ResolveLayerForReadAsync(serviceId, layerId, context, "convert3D");
        if (accessError != null)
        {
            return accessError;
        }

        _ = validation;
        return StandardErrorHelpers.CreateBadRequest(
            context,
            "3D conversion is not supported by this layer.",
            ["This server does not provide a 3D geometry conversion pipeline for FeatureServer layers."]);
    }

    private static async Task<IResult> HandleQuery3D(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var (validation, accessError) = await ResolveLayerForReadAsync(serviceId, layerId, context, "query3D");
        if (accessError != null)
        {
            return accessError;
        }

        _ = validation;
        return StandardErrorHelpers.CreateBadRequest(
            context,
            "3D queries are not supported by this layer.",
            ["This server does not expose a 3D query surface for FeatureServer layers."]);
    }

    private static async Task<IResult> HandleUpdateMetadata(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        // metadata/update mutates the layer's metadata document. Honua does not expose
        // metadata persistence through the FeatureServer protocol surface (metadata is
        // authored through the Admin API), so enforce write access for parity and then
        // honestly reject rather than fabricating a success the server cannot deliver.
        var accessError = await RequireLayerWriteAccessBeforeBodyAsync(
            serviceId, layerId, context, GetTimeoutAwareCancellationToken(context));
        if (accessError != null)
        {
            return accessError;
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "metadata.update",
            HonuaTelemetry.Protocols.FeatureServer,
            layerId.ToString(CultureInfo.InvariantCulture),
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        scope.WithTag("honua.result", "not_supported");

        return StandardErrorHelpers.CreateBadRequest(
            context,
            "Updating layer metadata is not supported through the FeatureServer protocol.",
            ["Layer metadata is authored through the Honua Admin API; the FeatureServer metadata/update operation is read-through only."]);
    }

    /// <summary>
    /// Resolves the (service, layer) pair through the shared V2 validation pipeline and
    /// enforces read access. Returns the validation result plus an error
    /// <see cref="IResult"/> when resolution or access fails (the validation result is
    /// then <see langword="default"/>).
    /// </summary>
    private static async Task<(FeatureServerResourceValidationHelpers.ServiceLayerValidationV2Result Validation, IResult? Error)>
        ResolveLayerForReadAsync(
            string serviceId,
            int layerId,
            HttpContext context,
            string operation)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            operation,
            HonuaTelemetry.Protocols.FeatureServer,
            layerId.ToString(CultureInfo.InvariantCulture),
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!validationResult.IsValid)
        {
            return (default, validationResult.ErrorResult!);
        }

        var service = validationResult.Service!;
        var resource = validationResult.Resource!;
        var accessError = AccessPolicyHelpers.RequireResourceAccess(context, resource, service);
        if (accessError != null)
        {
            return (default, accessError);
        }

        scope.SetSuccess(0);
        return (validationResult, null);
    }
}
