// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Rendering;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.Ogc.Classic.Wcs20;

internal sealed partial class Wcs20Handler
{
    internal readonly record struct WcsServiceLookup(
        MetadataV2GraphSnapshot Snapshot,
        Wcs20RouteScope Scope);

    private static async Task<(RasterRenderCapacityLimiter.Lease? Lease, IResult? Refusal)> TryAcquireCoverageRenderLeaseAsync(
        HttpContext context,
        (int Width, int Height) dimensions,
        CancellationToken cancellationToken)
    {
        var limiter = context.RequestServices.GetService<RasterRenderCapacityLimiter>();
        if (limiter is null)
        {
            return (null, null);
        }

        var renderLease = await limiter.TryAcquireAsync(dimensions.Width, dimensions.Height, cancellationToken)
            .ConfigureAwait(false);
        if (renderLease is not null)
        {
            return (renderLease, null);
        }

        context.Response.Headers.RetryAfter =
            RasterRenderCapacityLimiter.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return (null, Wcs20ErrorResults.CreateServiceUnavailable(RasterRenderCapacityLimiter.CapacityExceededMessage));
    }

    private async Task<IResult> ExportCoverageResultAsync(
        (WcsCoverageIdentifier CoverageId, WcsCoverage Coverage) target,
        (RasterQuery Query, HonuaTelemetryScope Telemetry) execution,
        CancellationToken cancellationToken)
    {
        var result = await _coverageBackend.ExportImageAsync(
            target.Coverage.LayerId,
            target.Coverage.Raster.Id,
            execution.Query,
            cancellationToken).ConfigureAwait(false);

        execution.Telemetry
            .WithTag("honua.result.bytes", result.Data.Length)
            .WithTag("honua.result.content_type", result.ContentType);
        execution.Telemetry.SetSuccess(1);

        Wcs20Log.CoverageReturned(
            _logger,
            target.CoverageId.Raw,
            result.Data.Length,
            result.ContentType);

        return EncodedRasterResults.File(result.Data, result.ContentType);
    }

    private static async Task<ServiceResolutionResult> ResolveServiceAsync(
        HttpContext context,
        WcsServiceLookup lookup,
        CancellationToken cancellationToken)
    {
        var (snapshot, scope) = lookup;
        if (string.IsNullOrWhiteSpace(scope.ServiceId))
        {
            return new ServiceResolutionResult(
                null,
                Wcs20ErrorResults.CreateNotFound(
                    Wcs20Utilities.ExceptionCodes.NoApplicableCode,
                    "Service was not found."));
        }

        var validated = ValidateWcsService(context, snapshot, scope.ServiceId);
        if (validated.Error is not null)
        {
            return validated;
        }

        var service = validated.Service!;
        var serviceAccess = await AccessPolicyHelpers.EvaluateServiceQueryAccessAsync(
            context, service, cancellationToken).ConfigureAwait(false);
        if (!serviceAccess.IsAllowed &&
            LayerScopedWriteKey.IsScopeGovernedPrincipal(context.User) &&
            await HasLayerScopedReadableCoverageAsync(context, (snapshot, service), cancellationToken).ConfigureAwait(false))
        {
            serviceAccess = AccessDecision.Allowed();
        }

        return serviceAccess.IsAllowed
            ? new ServiceResolutionResult(service, null)
            : new ServiceResolutionResult(null, CreateAccessDeniedResult(context, serviceAccess));
    }

    private static ServiceResolutionResult ValidateWcsService(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        string serviceId)
    {
        if (!snapshot.Index.ServicesById.TryGetValue(serviceId, out var service))
        {
            service = FindServiceForProtocol(snapshot, serviceId, WcsProtocolName)
                ?? snapshot.FindService(serviceId);
        }

        var serviceResult = ServiceResourceValidationHelpers.ValidateResolvedServicePath(context,
            service is not null && service.IsRoutable()
                ? ResourceValidationResult.Success(service)
                : ResourceValidationResult.NotFound<MetadataV2Service>($"Service '{serviceId}' was not found."));
        if (!serviceResult.IsValid)
        {
            return new ServiceResolutionResult(
                null,
                Wcs20ErrorResults.CreateNotFound(
                    Wcs20Utilities.ExceptionCodes.NoApplicableCode,
                    serviceResult.ErrorMessage!));
        }

        service = serviceResult.Resource!;
        return IsProtocolEnabled(service, WcsProtocolName)
            ? new ServiceResolutionResult(service, null)
            : new ServiceResolutionResult(
                null,
                Wcs20ErrorResults.CreateNotFound(
                    Wcs20Utilities.ExceptionCodes.OperationNotSupported,
                    "WCS is not enabled for this service."));
    }

    private static async Task<bool> HasLayerScopedReadableCoverageAsync(
        HttpContext context,
        (MetadataV2GraphSnapshot Snapshot, MetadataV2Service Service) target,
        CancellationToken cancellationToken)
    {
        var (snapshot, service) = target;
        foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            if (!snapshot.IsRoutable(publication) ||
                snapshot.ResolveResource(publication) is not { } candidateResource ||
                !snapshot.ResolveStorageLayerId(publication, candidateResource).HasValue ||
                !TenantScopeHelpers.IsPublicationVisible(context, publication, candidateResource, service))
            {
                continue;
            }

            if (await AccessPolicyHelpers.IsResourceAccessibleAsync(
                    context, candidateResource, service, AuthorizationOperation.Query, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private static (WcsCoverageIdentifier[] Ids, WcsParameterError? Error) ParseCoverageIdTokens(StringValues values)
    {
        var parsed = new List<WcsCoverageIdentifier>();
        var seenLayerIds = new HashSet<int>();
        var seenRawIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rawCount = 0;
        foreach (var token in SplitCsvValues(values))
        {
            if (++rawCount > MaxDescribeCoverageIds)
            {
                return ([], new WcsParameterError(
                    Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                    $"COVERAGEID may specify at most {MaxDescribeCoverageIds} coverages per DescribeCoverage request.",
                    Wcs20Utilities.Parameters.CoverageId));
            }

            if (!TryParseCoverageId(token, out var coverageId))
            {
                return ([], new WcsParameterError(
                    Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                    "COVERAGEID values must be valid coverage identifiers.",
                    Wcs20Utilities.Parameters.CoverageId));
            }

            var isNew = coverageId.LayerId.HasValue
                ? seenLayerIds.Add(coverageId.LayerId.Value)
                : seenRawIds.Add(coverageId.Raw);
            if (isNew)
            {
                parsed.Add(coverageId);
            }
        }

        return parsed.Count == 0
            ? ([], new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.MissingParameterValue,
                "COVERAGEID is required.",
                Wcs20Utilities.Parameters.CoverageId))
            : (parsed.ToArray(), null);
    }
}
