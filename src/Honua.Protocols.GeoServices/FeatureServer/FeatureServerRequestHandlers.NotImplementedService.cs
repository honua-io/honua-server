// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.ServiceDefaults;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Service-level FeatureServer operations that Honua exposes as thin, spec-shaped
/// protocol adapters even though the underlying data model is not yet implemented
/// (contingent values, shared editing templates, author-defined HTML pop-ups, and
/// the service-level image resource). Each handler validates the service and access
/// policy through the shared validation/access pipeline, then returns the
/// specification's response shape with empty/honest content so Esri SDK clients
/// (ArcGIS Pro, the ArcGIS Maps SDKs, ArcGIS API for Python/JS) parse the document
/// and observe that no such configuration exists, rather than receiving a fabricated
/// success or an opaque 404.
/// </summary>
internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleQueryContingentValues(
        string serviceId,
        HttpContext context)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "queryContingentValues",
            HonuaTelemetry.Protocols.FeatureServer,
            "service",
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var formatError = ValidateJsonOnlyFormat(context);
        if (formatError != null)
        {
            return formatError;
        }

        var accessError = await RequireServiceReadAccessAsync(serviceId, context);
        if (accessError != null)
        {
            return accessError;
        }

        scope.SetSuccess(0);
        return Results.Json(
            new QueryContingentValuesResponse(),
            FeatureServerJsonContext.Default.QueryContingentValuesResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleSharedTemplates(
        string serviceId,
        HttpContext context)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "sharedTemplates",
            HonuaTelemetry.Protocols.FeatureServer,
            "service",
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var formatError = ValidateJsonOnlyFormat(context);
        if (formatError != null)
        {
            return formatError;
        }

        var accessError = await RequireServiceReadAccessAsync(serviceId, context);
        if (accessError != null)
        {
            return accessError;
        }

        scope.SetSuccess(0);
        return Results.Json(
            new SharedTemplatesResponse(),
            FeatureServerJsonContext.Default.SharedTemplatesResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleSharedTemplatesQuery(
        string serviceId,
        HttpContext context)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "sharedTemplates.query",
            HonuaTelemetry.Protocols.FeatureServer,
            "service",
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var accessError = await RequireServiceReadAccessAsync(serviceId, context);
        if (accessError != null)
        {
            return accessError;
        }

        scope.SetSuccess(0);
        return Results.Json(
            new SharedTemplatesResponse(),
            FeatureServerJsonContext.Default.SharedTemplatesResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleSharedTemplatesAdd(
        string serviceId,
        HttpContext context)
        => await RejectSharedTemplateMutationAsync(serviceId, "add", context);

    private static async Task<IResult> HandleSharedTemplatesUpdate(
        string serviceId,
        HttpContext context)
        => await RejectSharedTemplateMutationAsync(serviceId, "update", context);

    private static async Task<IResult> HandleSharedTemplatesDelete(
        string serviceId,
        HttpContext context)
        => await RejectSharedTemplateMutationAsync(serviceId, "delete", context);

    private static async Task<IResult> RejectSharedTemplateMutationAsync(
        string serviceId,
        string operation,
        HttpContext context)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            $"sharedTemplates.{operation}",
            HonuaTelemetry.Protocols.FeatureServer,
            "service",
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        // Resolve the service (and enforce write access) so callers receive a 404 for
        // unknown services and a 401/403 for unauthorized callers, exactly as a real
        // mutation would. We then honestly reject the operation: Honua does not persist
        // shared editing templates, so there is nothing to add/update/delete.
        var accessError = await RequireServiceWriteAccessBeforeBodyAsync(
            serviceId, context, GetTimeoutAwareCancellationToken(context));
        if (accessError != null)
        {
            return accessError;
        }

        scope.WithTag("honua.result", "not_supported");
        return StandardErrorHelpers.CreateBadRequest(
            context,
            "Shared template editing is not supported by this service.",
            ["This server does not persist shared editing templates; the sharedTemplates collection is always empty."]);
    }

    private static async Task<IResult> HandleServiceHtmlPopup(
        string serviceId,
        HttpContext context)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "htmlPopup",
            HonuaTelemetry.Protocols.FeatureServer,
            "service",
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var accessError = await RequireServiceReadAccessAsync(serviceId, context);
        if (accessError != null)
        {
            return accessError;
        }

        scope.SetSuccess(0);
        return Results.Json(
            new HtmlPopupResponse(),
            FeatureServerJsonContext.Default.HtmlPopupResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleServiceImage(
        string serviceId,
        HttpContext context)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "image",
            HonuaTelemetry.Protocols.FeatureServer,
            "service",
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var accessError = await RequireServiceReadAccessAsync(serviceId, context);
        if (accessError != null)
        {
            return accessError;
        }

        // The Esri image resource streams an author-attached image for a feature. Honua
        // has no per-feature image asset store at the service level, so there is no image
        // to return. Respond honestly (rather than fabricating a placeholder image).
        scope.WithTag("honua.result", "not_supported");
        return StandardErrorHelpers.CreateNotFound(
            context,
            "No image resource is available for this feature service.",
            ["This server does not store author-attached feature images served by the FeatureServer image resource."]);
    }

    /// <summary>
    /// Resolves the service through the shared V2 validation pipeline and enforces
    /// read access. Returns an error <see cref="IResult"/> on failure or
    /// <see langword="null"/> when the caller is permitted to read the service.
    /// </summary>
    private static async Task<IResult?> RequireServiceReadAccessAsync(
        string serviceId,
        HttpContext context)
    {
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!serviceValidationResult.IsValid)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;
        var snapshotProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var allPairs = snapshot.Index.PublicationsByService[service.Metadata.Id]
            .Select(pub => (Publication: pub, Resource: snapshot.ResolveResource(pub)))
            .Where(pair => pair.Resource is not null)
            .Select(pair => (pair.Publication, Resource: pair.Resource!))
            .ToArray();

        return AccessPolicyHelpers.RequireAnyResourceAccess(
            context,
            allPairs.Select(pair => pair.Resource),
            service);
    }

    /// <summary>
    /// Validates that the requested output format (the <c>f</c> query parameter) is a
    /// JSON variant. Returns an error <see cref="IResult"/> for unsupported formats or
    /// <see langword="null"/> when the format is acceptable.
    /// </summary>
    private static IResult? ValidateJsonOnlyFormat(HttpContext context)
    {
        var requestedFormat = context.Request.Query.TryGetValue("f", out var formatValue)
            ? formatValue.ToString()
            : null;
        if (!TryValidateOutputFormat(requestedFormat, JsonOnlyFormats, out _, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [formatError ?? "Output format is not supported."]);
        }

        return null;
    }
}
