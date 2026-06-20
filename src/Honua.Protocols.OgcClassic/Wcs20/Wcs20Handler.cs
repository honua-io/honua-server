// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Services;
using Honua.Protocols.Ogc.Shared;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Primitives;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Protocols.Ogc.Classic.Wcs20;

/// <summary>
/// CITE conformance: 82/82 (WCS 2.0 `core` profile, 100% pass on trunk).
/// Authoritative status: <see href="../../../../../../docs/cite-status.md">docs/cite-status.md</see>.
/// </summary>
internal sealed class Wcs20Handler
{
    private static readonly XNamespace Wcs = Wcs20Utilities.WcsNamespace;
    private static readonly XNamespace Ows = Wcs20Utilities.OwsNamespace;
    private static readonly XNamespace Gml = Wcs20Utilities.GmlNamespace;
    private static readonly XNamespace Gmlcov = Wcs20Utilities.GmlcovNamespace;
    private static readonly XNamespace Swe = Wcs20Utilities.SweNamespace;
    private static readonly XNamespace XLink = Wcs20Utilities.XLinkNamespace;
    private static readonly XNamespace Xsi = Wcs20Utilities.XsiNamespace;
    private const string WcsProtocolName = "Wcs";

    private static readonly HashSet<string> _getCoverageAllowedParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        Wcs20Utilities.Parameters.Service,
        Wcs20Utilities.Parameters.Request,
        Wcs20Utilities.Parameters.Version,
        Wcs20Utilities.Parameters.CoverageId,
        Wcs20Utilities.Parameters.Format,
        Wcs20Utilities.Parameters.Subset,
        Wcs20Utilities.Parameters.SubsettingCrs,
        Wcs20Utilities.Parameters.OutputCrs,
        Wcs20Utilities.Parameters.BBox,
        Wcs20Utilities.Parameters.BBoxCrs,
        Wcs20Utilities.Parameters.RangeSubset,
        Wcs20Utilities.Parameters.ScaleSize,
        Wcs20Utilities.Parameters.ScaleFactor,
        Wcs20Utilities.Parameters.ScaleAxes,
        Wcs20Utilities.Parameters.ScaleExtent,
        Wcs20Utilities.Parameters.Interpolation,
        Wcs20Utilities.Parameters.DateTime,
        Wcs20Utilities.Parameters.Time,
    };

    private static readonly HashSet<string> _explicitlyUnsupportedGetCoverageParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "SIZE",
        "WIDTH",
        "HEIGHT",
        "RESOLUTION",
        "MEDIATYPE",
    };

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<Wcs20Handler> _logger;

    public Wcs20Handler(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ILogger<Wcs20Handler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal async Task<IResult> HandleAsync(HttpContext context, Wcs20RouteScope scope)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var operation = GetQueryValue(context.Request.Query, Wcs20Utilities.Parameters.Request);
        if (string.IsNullOrWhiteSpace(operation))
        {
            operation = Wcs20Utilities.Operations.GetCapabilities;
        }

        context.Items[RequestTelemetryClassifier.OperationItemKey] =
            "wcs." + operation.Trim().ToLowerInvariant();

        using var telemetry = HonuaTelemetryScope.StartFeature(
            operation,
            HonuaTelemetry.Protocols.Wcs20,
            scope.LayerId?.ToString(CultureInfo.InvariantCulture) ?? scope.ServiceId ?? "service");
        telemetry
            .WithTag(HonuaTelemetry.Tags.Operation, operation)
            .WithTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wcs20);
        if (scope.ServiceId is not null)
        {
            telemetry.WithTag(HonuaTelemetry.Tags.ServiceId, scope.ServiceId);
        }

        Wcs20Log.RequestReceived(_logger, operation, scope.DisplayName);

        try
        {
            var commonError = ValidateCommonParameters(context, operation);
            if (commonError is not null)
            {
                return commonError;
            }

            IResult result;
            if (string.Equals(operation, Wcs20Utilities.Operations.GetCapabilities, StringComparison.OrdinalIgnoreCase))
            {
                result = await HandleGetCapabilitiesAsync(context, scope, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(operation, Wcs20Utilities.Operations.DescribeCoverage, StringComparison.OrdinalIgnoreCase))
            {
                result = await HandleDescribeCoverageAsync(context, scope, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(operation, Wcs20Utilities.Operations.GetCoverage, StringComparison.OrdinalIgnoreCase))
            {
                result = await HandleGetCoverageAsync(context, scope, telemetry, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Wcs20Log.ValidationFailed(_logger, operation, "Unsupported WCS operation.");
                result = Wcs20ErrorResults.CreateNotImplemented(
                    Wcs20Utilities.ExceptionCodes.OperationNotSupported,
                    $"Unsupported WCS operation '{operation}'. Supported operations are GetCapabilities, DescribeCoverage, and GetCoverage.",
                    Wcs20Utilities.Parameters.Request);
            }

            Wcs20Log.RequestCompleted(_logger, operation, scope.DisplayName);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Wcs20Log.RequestFailed(_logger, ex, operation, scope.DisplayName);
            telemetry.RecordException(ex);
            return Wcs20ErrorResults.CreateInternalServerError("Failed to process WCS request.");
        }
    }

    private async Task<IResult> HandleGetCapabilitiesAsync(
        HttpContext context,
        Wcs20RouteScope scope,
        CancellationToken cancellationToken)
    {
        var acceptVersions = GetQueryValue(context.Request.Query, Wcs20Utilities.Parameters.AcceptVersions);
        if (!string.IsNullOrWhiteSpace(acceptVersions))
        {
            var versions = SplitCsv(acceptVersions);
            var anySupported = false;
            foreach (var candidate in versions)
            {
                if (OgcParameterValidator.TryParseVersion(candidate, Wcs20Utilities.SupportedVersions, out _, out _))
                {
                    anySupported = true;
                    break;
                }
            }

            if (versions.Length > 0 && !anySupported)
            {
                return Wcs20ErrorResults.CreateBadRequest(
                    Wcs20Utilities.ExceptionCodes.VersionNegotiationFailed,
                    $"Unsupported version. This service supports only WCS {Wcs20Utilities.Version}.",
                    Wcs20Utilities.Parameters.AcceptVersions);
            }
        }

        var acceptFormats = GetQueryValue(context.Request.Query, Wcs20Utilities.Parameters.AcceptFormats);
        if (!string.IsNullOrWhiteSpace(acceptFormats))
        {
            var formats = SplitCsv(acceptFormats);
            if (formats.Length > 0 &&
                !formats.Any(format => Wcs20Utilities.XmlFormats.Contains(format) || string.Equals(format, "*/*", StringComparison.Ordinal)))
            {
                return Wcs20ErrorResults.CreateBadRequest(
                    Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                    "GetCapabilities supports only XML response formats.",
                    Wcs20Utilities.Parameters.AcceptFormats);
            }
        }

        if (!TryParseSections(
                GetQueryValue(context.Request.Query, Wcs20Utilities.Parameters.Sections),
                out var sections,
                out var sectionsError))
        {
            return Wcs20ErrorResults.CreateBadRequest(
                Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                sectionsError ?? "Invalid sections parameter.",
                Wcs20Utilities.Parameters.Sections);
        }

        var coverageResult = await ResolveCoverageListAsync(context, scope, cancellationToken).ConfigureAwait(false);
        if (coverageResult.Error is not null)
        {
            return coverageResult.Error;
        }

        var document = BuildCapabilitiesDocument(context, coverageResult.Coverages, sections);
        return Xml(document);
    }

    private async Task<IResult> HandleDescribeCoverageAsync(
        HttpContext context,
        Wcs20RouteScope scope,
        CancellationToken cancellationToken)
    {
        if (!TryParseCoverageIds(context.Request.Query, out var coverageIds, out var coverageIdError))
        {
            return Wcs20ErrorResults.CreateBadRequest(
                coverageIdError.ExceptionCode,
                coverageIdError.Detail,
                Wcs20Utilities.Parameters.CoverageId);
        }

        var descriptions = new List<XElement>(coverageIds.Length);
        foreach (var coverageId in coverageIds)
        {
            var coverage = await ResolveCoverageAsync(context, scope, coverageId, cancellationToken).ConfigureAwait(false);
            if (coverage.Error is not null)
            {
                return coverage.Error;
            }

            if (coverage.Coverage is null)
            {
                Wcs20Log.CoverageNotFound(_logger, coverageId.Raw);
                return Wcs20ErrorResults.CreateNotFound(
                    Wcs20Utilities.ExceptionCodes.NoSuchCoverage,
                    $"Coverage '{coverageId.Raw}' was not found.",
                    Wcs20Utilities.Parameters.CoverageId);
            }

            if (!TryBuildCoverageDescription(coverage.Coverage.Value, out var description, out var metadataError))
            {
                return Wcs20ErrorResults.CreateInternalServerError(metadataError ?? "Coverage metadata is incomplete.");
            }

            descriptions.Add(description);
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Wcs + "CoverageDescriptions",
                RootNamespaceAttributes(),
                new XAttribute(Xsi + "schemaLocation",
                    $"{Wcs20Utilities.WcsNamespace} http://schemas.opengis.net/wcs/2.0/wcsAll.xsd"),
                descriptions));

        return Xml(document);
    }

    private async Task<IResult> HandleGetCoverageAsync(
        HttpContext context,
        Wcs20RouteScope scope,
        HonuaTelemetryScope telemetry,
        CancellationToken cancellationToken)
    {
        var unsupportedParameterError = ValidateGetCoverageParameters(context.Request.Query);
        if (unsupportedParameterError is not null)
        {
            return unsupportedParameterError;
        }

        if (!TryParseCoverageIds(context.Request.Query, out var coverageIds, out var coverageIdError))
        {
            return Wcs20ErrorResults.CreateBadRequest(
                coverageIdError.ExceptionCode,
                coverageIdError.Detail,
                Wcs20Utilities.Parameters.CoverageId);
        }

        if (coverageIds.Length != 1)
        {
            return Wcs20ErrorResults.CreateBadRequest(
                Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                "GetCoverage accepts exactly one COVERAGEID value.",
                Wcs20Utilities.Parameters.CoverageId);
        }

        var coverageId = coverageIds[0];
        var coverage = await ResolveCoverageAsync(context, scope, coverageId, cancellationToken).ConfigureAwait(false);
        if (coverage.Error is not null)
        {
            return coverage.Error;
        }

        if (coverage.Coverage is null)
        {
            Wcs20Log.CoverageNotFound(_logger, coverageId.Raw);
            return Wcs20ErrorResults.CreateNotFound(
                Wcs20Utilities.ExceptionCodes.NoSuchCoverage,
                $"Coverage '{coverageId.Raw}' was not found.",
                Wcs20Utilities.Parameters.CoverageId);
        }

        if (!TryParseCoverageFormat(
                GetQueryValue(context.Request.Query, Wcs20Utilities.Parameters.Format),
                out var outputFormat,
                out var formatContentType))
        {
            return Wcs20ErrorResults.CreateBadRequest(
                Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                "FORMAT must be one of image/tiff, image/png, or image/jpeg.",
                Wcs20Utilities.Parameters.Format);
        }

        if (!TryResolveCoverageQuery(context.Request.Query, coverage.Coverage.Value.Raster, outputFormat, out var query, out var queryError))
        {
            Wcs20Log.ValidationFailed(_logger, Wcs20Utilities.Operations.GetCoverage, queryError.Detail);
            return CreateGetCoverageParameterError(queryError);
        }

        if (!TryApplyTemporalSubset(context.Request.Query, coverage.Coverage.Value.Raster, out var temporalError))
        {
            Wcs20Log.ValidationFailed(_logger, Wcs20Utilities.Operations.GetCoverage, temporalError.Detail);
            return CreateGetCoverageParameterError(temporalError);
        }

        telemetry
            .WithTag(HonuaTelemetry.Tags.LayerId, coverage.Coverage.Value.LayerId)
            .WithTag("honua.coverage.id", coverageId.Raw)
            .WithTag("honua.output.format", formatContentType);

        var result = await _rasterStore.ExportImageAsync(
            coverage.Coverage.Value.LayerId,
            coverage.Coverage.Value.Raster.Id,
            query,
            cancellationToken).ConfigureAwait(false);

        telemetry
            .WithTag("honua.result.bytes", result.Data.Length)
            .WithTag("honua.result.content_type", result.ContentType);
        telemetry.SetSuccess(1);

        Wcs20Log.CoverageReturned(
            _logger,
            coverageId.Raw,
            result.Data.Length,
            result.ContentType);

        return Results.File(result.Data, result.ContentType);
    }

    private async Task<CoverageListResult> ResolveCoverageListAsync(
        HttpContext context,
        Wcs20RouteScope scope,
        CancellationToken cancellationToken)
    {
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        if (scope.IsLayerScoped)
        {
            var coverage = await ResolveLayerScopedCoverageAsync(
                    context,
                    snapshot,
                    scope.LayerId!.Value,
                    failOnAccessDenied: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (coverage.Error is not null)
            {
                return new CoverageListResult([], coverage.Error);
            }

            return coverage.Coverage is null
                ? new CoverageListResult(
                    [],
                    Wcs20ErrorResults.CreateNotFound(
                        Wcs20Utilities.ExceptionCodes.NoSuchCoverage,
                        $"Coverage '{scope.LayerId.Value.ToString(CultureInfo.InvariantCulture)}' was not found.",
                        Wcs20Utilities.Parameters.CoverageId))
                : new CoverageListResult([coverage.Coverage.Value], null);
        }

        var service = ResolveService(context, snapshot, scope);
        if (service.Error is not null)
        {
            return new CoverageListResult([], service.Error);
        }

        var coverages = new List<WcsCoverage>();
        // Walk publications on the WCS-enabled service, dedupe by resource (prefer
        // IsPrimary) so a resource exposed through multiple publications is listed once,
        // then keep only those backed by a raster the IRasterStore can serve.
        var byResource = new Dictionary<string, (MetadataV2Publication Publication, MetadataV2Resource Resource, int StorageLayerId)>(StringComparer.Ordinal);
        foreach (var publication in snapshot.Index.PublicationsByService[service.Service!.Metadata.Id])
        {
            var resource = snapshot.ResolveResource(publication);
            if (resource is null)
            {
                continue;
            }

            if (!AccessPolicyHelpers.IsResourceAccessible(context, resource, service.Service))
            {
                continue;
            }

            // Prefer publication.LayerIndex (the protocol-facing handle) and fall
            // back to the storage binding's StorageLayerId; matches the canonical
            // V2 resolution order used by the FeatureServer ports.
            var storageLayerId = publication.LayerIndex ?? snapshot.ResolveStorageLayerId(publication);
            if (!storageLayerId.HasValue)
            {
                continue;
            }

            if (!byResource.TryGetValue(resource.Metadata.Id, out var existing) ||
                (publication.IsPrimary && !existing.Publication.IsPrimary))
            {
                byResource[resource.Metadata.Id] = (publication, resource, storageLayerId.Value);
            }
        }

        foreach (var entry in byResource.Values.OrderBy(t => t.StorageLayerId))
        {
            var raster = await GetPrimaryRasterWithExtentAsync(entry.StorageLayerId, cancellationToken).ConfigureAwait(false);
            if (raster is null)
            {
                continue;
            }

            coverages.Add(new WcsCoverage(entry.Resource, entry.StorageLayerId, raster.Value, service.Service));
        }

        return new CoverageListResult(coverages, null);
    }

    private async Task<CoverageResolutionResult> ResolveCoverageAsync(
        HttpContext context,
        Wcs20RouteScope scope,
        WcsCoverageIdentifier coverageId,
        CancellationToken cancellationToken)
    {
        if (!coverageId.LayerId.HasValue)
        {
            return new CoverageResolutionResult(null, null);
        }

        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var layerId = coverageId.LayerId.Value;
        if (scope.IsLayerScoped)
        {
            if (layerId != scope.LayerId!.Value)
            {
                return new CoverageResolutionResult(null, null);
            }

            var result = await ResolveLayerScopedCoverageAsync(
                    context,
                    snapshot,
                    layerId,
                    failOnAccessDenied: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return new CoverageResolutionResult(result.Coverage, result.Error);
        }

        var service = ResolveService(context, snapshot, scope);
        if (service.Error is not null)
        {
            return new CoverageResolutionResult(null, service.Error);
        }

        if (service.Service is null)
        {
            return new CoverageResolutionResult(null, null);
        }

        var publication = snapshot.FindPublicationByLayerIndex(service.Service.Metadata.Id, layerId);
        if (publication is null)
        {
            return new CoverageResolutionResult(null, null);
        }

        var resource = snapshot.ResolveResource(publication);
        if (resource is null ||
            !AccessPolicyHelpers.IsResourceAccessible(context, resource, service.Service))
        {
            return new CoverageResolutionResult(null, null);
        }

        var storageLayerId = publication.LayerIndex ?? snapshot.ResolveStorageLayerId(publication);
        if (!storageLayerId.HasValue)
        {
            return new CoverageResolutionResult(null, null);
        }

        var raster = await GetPrimaryRasterWithExtentAsync(storageLayerId.Value, cancellationToken).ConfigureAwait(false);
        return raster is null
            ? new CoverageResolutionResult(null, null)
            : new CoverageResolutionResult(new WcsCoverage(resource, storageLayerId.Value, raster.Value, service.Service), null);
    }

    private async Task<LayerCoverageResult> ResolveLayerScopedCoverageAsync(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        int layerId,
        bool failOnAccessDenied,
        CancellationToken cancellationToken)
    {
        // Layer-scoped /rest/services/{id:int}/ImageServer/WCS routes don't carry a
        // logical V2 service — they're keyed by the int storage layer handle. Resolve
        // the resource directly from the storage-layer index, with a fallback to
        // publication.LayerIndex for fixtures/graphs that haven't migrated their
        // storage bindings (matches the resolution order used elsewhere in the V2 ports).
        if (!TryResolveResourceForLayer(snapshot, layerId, out var resource))
        {
            return new LayerCoverageResult(null, null);
        }

        var accessDecision = AccessPolicyHelpers.EvaluateAccess(context, resource.AccessPolicy, servicePolicy: null);
        if (!accessDecision.IsAllowed)
        {
            return failOnAccessDenied
                ? new LayerCoverageResult(null, CreateAccessDeniedResult(accessDecision))
                : new LayerCoverageResult(null, null);
        }

        var raster = await GetPrimaryRasterWithExtentAsync(layerId, cancellationToken).ConfigureAwait(false);
        return raster is null
            ? new LayerCoverageResult(null, null)
            : new LayerCoverageResult(new WcsCoverage(resource, layerId, raster.Value, null), null);
    }

    private static bool TryResolveResourceForLayer(MetadataV2GraphSnapshot snapshot, int layerId, out MetadataV2Resource resource)
    {
        if (snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var byBinding))
        {
            resource = byBinding;
            return true;
        }

        foreach (var publication in snapshot.Graph.Publications)
        {
            if (publication.LayerIndex == layerId)
            {
                var resolved = snapshot.ResolveResource(publication);
                if (resolved is not null)
                {
                    resource = resolved;
                    return true;
                }
            }
        }

        resource = default!;
        return false;
    }

    private static ServiceResolutionResult ResolveService(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        Wcs20RouteScope scope)
    {
        var serviceId = scope.ServiceId;
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return new ServiceResolutionResult(
                null,
                Wcs20ErrorResults.CreateNotFound(
                    Wcs20Utilities.ExceptionCodes.NoApplicableCode,
                    "Service was not found."));
        }

        // Resolve by id first (canonical), then by display name to cover the
        // ServiceId-as-name routing used by /ogc/services/{serviceId}/...
        if (!snapshot.Index.ServicesById.TryGetValue(serviceId, out var service))
        {
            service = snapshot.FindService(serviceId);
        }

        if (service is null)
        {
            return new ServiceResolutionResult(
                null,
                Wcs20ErrorResults.CreateNotFound(
                    Wcs20Utilities.ExceptionCodes.NoApplicableCode,
                    $"Service '{serviceId}' was not found."));
        }

        if (!IsProtocolEnabled(service, WcsProtocolName))
        {
            return new ServiceResolutionResult(
                null,
                Wcs20ErrorResults.CreateNotFound(
                    Wcs20Utilities.ExceptionCodes.OperationNotSupported,
                    "WCS is not enabled for this service."));
        }

        var serviceAccess = AccessPolicyHelpers.EvaluateAccess(context, layerPolicy: null, service.AccessPolicy);
        if (!serviceAccess.IsAllowed)
        {
            return new ServiceResolutionResult(null, CreateAccessDeniedResult(serviceAccess));
        }

        return new ServiceResolutionResult(service, null);
    }

    private static bool IsProtocolEnabled(MetadataV2Service service, string protocol)
        => service.Protocols.Any(enabled => string.Equals(enabled, protocol, StringComparison.OrdinalIgnoreCase));

    private async Task<RasterInfo?> GetPrimaryRasterWithExtentAsync(int layerId, CancellationToken cancellationToken)
    {
        var raster = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (raster is null)
        {
            return null;
        }

        if (raster.Value.Extent is null)
        {
            var extent = await _rasterStore.GetExtentAsync(layerId, raster.Value.Id, cancellationToken).ConfigureAwait(false);
            if (extent.HasValue)
            {
                raster = raster.Value with { Extent = extent };
            }
        }

        return raster;
    }

    private static IResult? ValidateCommonParameters(HttpContext context, string operation)
    {
        var service = GetQueryValue(context.Request.Query, Wcs20Utilities.Parameters.Service);
        if (!string.IsNullOrWhiteSpace(service) &&
            !string.Equals(service, Wcs20Utilities.ServiceType, StringComparison.OrdinalIgnoreCase))
        {
            return Wcs20ErrorResults.CreateBadRequest(
                Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                "SERVICE must be WCS when supplied.",
                Wcs20Utilities.Parameters.Service);
        }

        var version = GetQueryValue(context.Request.Query, Wcs20Utilities.Parameters.Version);
        if (!string.IsNullOrWhiteSpace(version) &&
            !OgcParameterValidator.TryParseVersion(version, Wcs20Utilities.SupportedVersions, out _, out _))
        {
            return Wcs20ErrorResults.CreateBadRequest(
                Wcs20Utilities.ExceptionCodes.VersionNegotiationFailed,
                $"Unsupported version '{version}'. This service supports only WCS {Wcs20Utilities.Version}.",
                Wcs20Utilities.Parameters.Version);
        }

        if (!Wcs20Utilities.ImplementedOperations.Contains(operation))
        {
            return null;
        }

        return null;
    }

    private static IResult? ValidateGetCoverageParameters(IQueryCollection query)
    {
        foreach (var parameter in query.Keys)
        {
            if (_getCoverageAllowedParameters.Contains(parameter))
            {
                continue;
            }

            if (_explicitlyUnsupportedGetCoverageParameters.Contains(parameter))
            {
                return Wcs20ErrorResults.CreateNotImplemented(
                    Wcs20Utilities.ExceptionCodes.OperationNotSupported,
                    $"{parameter} is not supported by this WCS GetCoverage implementation.",
                    parameter);
            }

            return Wcs20ErrorResults.CreateBadRequest(
                Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                $"Unsupported GetCoverage parameter '{parameter}'.",
                parameter);
        }

        return null;
    }

    private static IResult CreateAccessDeniedResult(AccessDecision decision)
        => decision.RequiresAuthentication
            ? Wcs20ErrorResults.CreateUnauthorized(AccessPolicyHelpers.AuthRequiredMessage)
            : Wcs20ErrorResults.CreateForbidden(AccessPolicyHelpers.AccessForbiddenMessage);

    private static XDocument BuildCapabilitiesDocument(
        HttpContext context,
        IReadOnlyCollection<WcsCoverage> coverages,
        IReadOnlySet<string>? sections)
    {
        var rootChildren = new List<object>();

        if (IncludesSection(sections, "ServiceIdentification"))
        {
            rootChildren.Add(new XElement(Ows + "ServiceIdentification",
                new XElement(Ows + "Title", "Honua WCS 2.0.1"),
                new XElement(Ows + "Abstract", "Honua Web Coverage Service for raster and coverage data."),
                new XElement(Ows + "Keywords",
                    new XElement(Ows + "Keyword", "WCS"),
                    new XElement(Ows + "Keyword", "coverage"),
                    new XElement(Ows + "Keyword", "raster")),
                new XElement(Ows + "ServiceType", "WCS"),
                new XElement(Ows + "ServiceTypeVersion", Wcs20Utilities.Version),
                new XElement(Ows + "Profile", "http://www.opengis.net/spec/WCS_protocol-binding_get-kvp/1.0"),
                new XElement(Ows + "Fees", "NONE"),
                new XElement(Ows + "AccessConstraints", "NONE")));
        }

        if (IncludesSection(sections, "ServiceProvider"))
        {
            rootChildren.Add(new XElement(Ows + "ServiceProvider",
                new XElement(Ows + "ProviderName", "Honua"),
                new XElement(Ows + "ProviderSite", new XAttribute(XLink + "href", "https://honua.io")),
                new XElement(Ows + "ServiceContact",
                    new XElement(Ows + "Role", "pointOfContact"))));
        }

        if (IncludesSection(sections, "OperationsMetadata"))
        {
            rootChildren.Add(BuildOperationsMetadata(context));
        }

        if (IncludesSection(sections, "ServiceMetadata"))
        {
            rootChildren.Add(new XElement(Wcs + "ServiceMetadata",
                new XElement(Wcs + "formatSupported", Wcs20Utilities.TiffContentType),
                new XElement(Wcs + "formatSupported", Wcs20Utilities.PngContentType),
                new XElement(Wcs + "formatSupported", Wcs20Utilities.JpegContentType)));
        }

        if (IncludesSection(sections, "Contents"))
        {
            rootChildren.Add(new XElement(Wcs + "Contents",
                coverages.Select(BuildCoverageSummary)));
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Wcs + "Capabilities",
                RootNamespaceAttributes(),
                new XAttribute("version", Wcs20Utilities.Version),
                new XAttribute(Xsi + "schemaLocation",
                    $"{Wcs20Utilities.WcsNamespace} http://schemas.opengis.net/wcs/2.0/wcsAll.xsd"),
                rootChildren));
    }

    private static XElement BuildOperationsMetadata(HttpContext context)
    {
        var endpoint = GetCurrentEndpointUrl(context);
        return new XElement(Ows + "OperationsMetadata",
            BuildOperation(Wcs20Utilities.Operations.GetCapabilities, endpoint,
                BuildAllowedValuesParameter("AcceptVersions", Wcs20Utilities.Version),
                BuildAllowedValuesParameter("AcceptFormats", Wcs20Utilities.XmlContentType, "text/xml")),
            BuildOperation(Wcs20Utilities.Operations.DescribeCoverage, endpoint,
                BuildAnyValueParameter(Wcs20Utilities.Parameters.CoverageId)),
            BuildOperation(Wcs20Utilities.Operations.GetCoverage, endpoint,
                BuildAnyValueParameter(Wcs20Utilities.Parameters.CoverageId),
                BuildAllowedValuesParameter(
                    Wcs20Utilities.Parameters.Format,
                    Wcs20Utilities.TiffContentType,
                    Wcs20Utilities.PngContentType,
                    Wcs20Utilities.JpegContentType)),
            BuildAllowedValuesParameter("service", Wcs20Utilities.ServiceType),
            BuildAllowedValuesParameter("version", Wcs20Utilities.Version));
    }

    private static XElement BuildOperation(string operationName, string endpoint, params XElement[] parameters)
        => new(Ows + "Operation",
            new XAttribute("name", operationName),
            new XElement(Ows + "DCP",
                new XElement(Ows + "HTTP",
                    new XElement(Ows + "Get",
                        new XAttribute(XLink + "href", endpoint)))),
            parameters);

    private static XElement BuildAllowedValuesParameter(string name, params string[] values)
        => new(Ows + "Parameter",
            new XAttribute("name", name),
            new XElement(Ows + "AllowedValues",
                values.Select(value => new XElement(Ows + "Value", value))));

    private static XElement BuildAnyValueParameter(string name)
        => new(Ows + "Parameter",
            new XAttribute("name", name),
            new XElement(Ows + "AnyValue"));

    private static XElement BuildCoverageSummary(WcsCoverage coverage)
    {
        var title = coverage.Resource.Metadata.Title ?? coverage.Resource.Metadata.Name;
        var children = new List<object>
        {
            new XElement(Ows + "Title", title)
        };

        if (!string.IsNullOrWhiteSpace(coverage.Resource.Metadata.Description))
        {
            children.Add(new XElement(Ows + "Abstract", coverage.Resource.Metadata.Description));
        }

        var resourceSrid = coverage.Resource.ReadSrid();
        if (TryResolveExtent(coverage.Raster, out var extent) &&
            (extent.Srid ?? coverage.Raster.Srid ?? resourceSrid) == 4326)
        {
            children.Add(new XElement(Ows + "WGS84BoundingBox",
                new XElement(Ows + "LowerCorner", FormatPosition(extent.XMin, extent.YMin)),
                new XElement(Ows + "UpperCorner", FormatPosition(extent.XMax, extent.YMax))));
        }

        children.Add(new XElement(Wcs + "CoverageId", FormatCoverageId(coverage.LayerId)));
        children.Add(new XElement(Wcs + "CoverageSubtype", "gmlcov:RectifiedGridCoverage"));

        return new XElement(Wcs + "CoverageSummary", children);
    }

    private static bool TryBuildCoverageDescription(
        WcsCoverage coverage,
        out XElement description,
        out string? error)
    {
        description = default!;
        error = null;

        if (!TryResolveExtent(coverage.Raster, out var extent))
        {
            error = "Coverage extent is not available.";
            return false;
        }

        var srid = coverage.Raster.Srid ?? extent.Srid ?? coverage.Resource.ReadSrid() ?? 0;
        if (srid <= 0)
        {
            error = "Coverage CRS is not available.";
            return false;
        }

        if (!TryResolveGridVectors(coverage.Raster, extent, out var origin, out var xVector, out var yVector))
        {
            error = "Coverage grid transform is not available.";
            return false;
        }

        var coverageId = FormatCoverageId(coverage.LayerId);
        var srsName = CreateEpsgUri(srid);
        description = new XElement(Wcs + "CoverageDescription",
            new XAttribute(Gml + "id", coverageId),
            new XElement(Gml + "boundedBy",
                new XElement(Gml + "Envelope",
                    new XAttribute("srsName", srsName),
                    new XAttribute("srsDimension", "2"),
                    new XAttribute("axisLabels", "x y"),
                    new XElement(Gml + "lowerCorner", FormatPosition(extent.XMin, extent.YMin)),
                    new XElement(Gml + "upperCorner", FormatPosition(extent.XMax, extent.YMax)))),
            new XElement(Wcs + "CoverageId", coverageId),
            new XElement(Gml + "domainSet",
                new XElement(Gml + "RectifiedGrid",
                    new XAttribute(Gml + "id", "grid_" + coverageId),
                    new XAttribute("dimension", "2"),
                    new XElement(Gml + "limits",
                        new XElement(Gml + "GridEnvelope",
                            new XElement(Gml + "low", "0 0"),
                            new XElement(Gml + "high",
                                $"{Math.Max(coverage.Raster.Width - 1, 0).ToString(CultureInfo.InvariantCulture)} {Math.Max(coverage.Raster.Height - 1, 0).ToString(CultureInfo.InvariantCulture)}"))),
                    new XElement(Gml + "axisLabels", "x y"),
                    new XElement(Gml + "origin",
                        new XElement(Gml + "Point",
                            new XAttribute(Gml + "id", "origin_" + coverageId),
                            new XAttribute("srsName", srsName),
                            new XElement(Gml + "pos", FormatPosition(origin.X, origin.Y)))),
                    new XElement(Gml + "offsetVector", new XAttribute("srsName", srsName), FormatPosition(xVector.X, xVector.Y)),
                    new XElement(Gml + "offsetVector", new XAttribute("srsName", srsName), FormatPosition(yVector.X, yVector.Y)))),
            new XElement(Gmlcov + "rangeType",
                new XElement(Swe + "DataRecord",
                    Enumerable.Range(1, Math.Max(coverage.Raster.BandCount, 1))
                        .Select(band => BuildBandField(coverage.Raster, band)))),
            new XElement(Wcs + "ServiceParameters",
                new XElement(Wcs + "CoverageSubtype", "gmlcov:RectifiedGridCoverage"),
                new XElement(Wcs + "nativeFormat", Wcs20Utilities.TiffContentType)));

        return true;
    }

    private static XElement BuildBandField(RasterInfo raster, int band)
    {
        var quantityChildren = new List<object>
        {
            new XElement(Swe + "label", "Band " + band.ToString(CultureInfo.InvariantCulture)),
            new XElement(Swe + "description", "Pixel type: " + raster.PixelType),
            new XElement(Swe + "uom", new XAttribute("code", "unity"))
        };

        if (raster.NoDataValue.HasValue)
        {
            quantityChildren.Insert(2,
                new XElement(Swe + "nilValues",
                    new XElement(Swe + "NilValues",
                        new XElement(Swe + "nilValue",
                            new XAttribute("reason", "http://www.opengis.net/def/nil/OGC/0/NoData"),
                            FormatDouble(raster.NoDataValue.Value)))));
        }

        return new XElement(Swe + "field",
            new XAttribute("name", "band" + band.ToString(CultureInfo.InvariantCulture)),
            new XElement(Swe + "Quantity",
                new XAttribute("definition", "http://www.opengis.net/def/property/OGC/0/coverage-value"),
                quantityChildren));
    }

    private static bool TryResolveCoverageQuery(
        IQueryCollection query,
        RasterInfo raster,
        RasterFormat outputFormat,
        out RasterQuery rasterQuery,
        out WcsParameterError error)
    {
        rasterQuery = new RasterQuery
        {
            OutputFormat = outputFormat
        };
        error = default;

        if (!TryResolveRequestCrs(query, raster, out var subsettingCrs, out var outputSrid, out error))
        {
            return false;
        }

        rasterQuery = rasterQuery with { OutputSrid = outputSrid };

        // SUBSET carries spatial trims (x/y axes), temporal slices (phenomenonTime),
        // and — for multidimensional coverages — additional named dimension axes
        // (e.g. a vertical/elevation axis). Temporal subsets are validated separately
        // against the coverage acquisition time; additional dimension axes are parsed
        // and validated here (#1872) before spatial parsing so that a well-formed
        // slice on an axis the coverage does not carry surfaces a precise
        // InvalidAxisLabel, and a malformed one surfaces InvalidSubsetting.
        var allSubsetValues = GetQueryValues(query, Wcs20Utilities.Parameters.Subset);
        if (!TryValidateAdditionalDimensionSubsets(allSubsetValues, raster, out error))
        {
            return false;
        }

        var spatialSubsetValues = FilterSpatialSubsets(allSubsetValues);
        var bbox = GetQueryValue(query, Wcs20Utilities.Parameters.BBox);
        if (spatialSubsetValues.Count > 0 && !string.IsNullOrWhiteSpace(bbox))
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                "Use either SUBSET or BBOX, not both.",
                Wcs20Utilities.Parameters.Subset);
            return false;
        }

        // The base grid for the Scaling extension is the source grid after any
        // SUBSET/BBOX trim. Default to the full native grid and refine it when a
        // spatial trim in the native CRS is supplied.
        var baseWidth = Math.Max(raster.Width, 1);
        var baseHeight = Math.Max(raster.Height, 1);

        if (spatialSubsetValues.Count > 0)
        {
            if (!TryParseSubsetEnvelope(spatialSubsetValues, raster, subsettingCrs, out var envelope, out error))
            {
                return false;
            }

            rasterQuery = rasterQuery with { ClipRegion = CreateClipRegion(envelope, subsettingCrs.Srid) };
            TryRefineBaseGrid(raster, subsettingCrs, envelope, ref baseWidth, ref baseHeight);
        }
        else if (!string.IsNullOrWhiteSpace(bbox))
        {
            if (!TryParseBboxEnvelope(bbox, subsettingCrs, out var envelope, out error))
            {
                return false;
            }

            rasterQuery = rasterQuery with { ClipRegion = CreateClipRegion(envelope, subsettingCrs.Srid) };
            TryRefineBaseGrid(raster, subsettingCrs, envelope, ref baseWidth, ref baseHeight);
        }

        if (!TryResolveRangeSubset(query, raster, out var bands, out error))
        {
            return false;
        }

        if (bands is { Length: > 0 })
        {
            rasterQuery = rasterQuery with { Bands = bands };
        }

        if (!TryResolveScaling(query, baseWidth, baseHeight, out var width, out var height, out error))
        {
            return false;
        }

        if (width.HasValue)
        {
            rasterQuery = rasterQuery with { OutputWidth = width.Value };
        }

        if (height.HasValue)
        {
            rasterQuery = rasterQuery with { OutputHeight = height.Value };
        }

        if (!TryResolveInterpolation(query, out var resampling, out error))
        {
            return false;
        }

        if (resampling.HasValue)
        {
            rasterQuery = rasterQuery with { ResamplingAlgorithm = resampling.Value };
        }

        return true;
    }

    // WCS 2.0 Interpolation extension: INTERPOLATION selects the resampling method
    // used when the coverage is scaled/reprojected. Accepts the OGC interpolation
    // method URIs, their trailing tokens, and common aliases, mapping each to the
    // shared raster ResamplingAlgorithm. Unknown methods are rejected with the
    // dedicated InterpolationMethodNotSupported exception code.
    private static bool TryResolveInterpolation(
        IQueryCollection query,
        out ResamplingAlgorithm? resampling,
        out WcsParameterError error)
    {
        resampling = null;
        error = default;

        var raw = GetQueryValue(query, Wcs20Utilities.Parameters.Interpolation);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var normalized = raw.Trim();
        var lastSlash = normalized.LastIndexOf('/');
        var token = lastSlash >= 0 && lastSlash < normalized.Length - 1
            ? normalized[(lastSlash + 1)..]
            : normalized;

        switch (token.ToLowerInvariant())
        {
            case "nearest":
            case "nearestneighbor":
            case "nearestneighbour":
                resampling = ResamplingAlgorithm.NearestNeighbor;
                return true;
            case "linear":
            case "bilinear":
                resampling = ResamplingAlgorithm.Bilinear;
                return true;
            case "cubic":
            case "bicubic":
            case "cubicconvolution":
                resampling = ResamplingAlgorithm.Bicubic;
                return true;
            case "lanczos":
                resampling = ResamplingAlgorithm.Lanczos;
                return true;
            default:
                error = new WcsParameterError(
                    Wcs20Utilities.ExceptionCodes.InterpolationMethodNotSupported,
                    $"INTERPOLATION method '{raw}' is not supported. Supported methods are nearest, linear, and cubic.",
                    Wcs20Utilities.Parameters.Interpolation);
                return false;
        }
    }

    // Returns the SUBSET values that target spatial axes, dropping temporal
    // (phenomenonTime/time/date/ansi) slices which are validated separately.
    private static StringValues FilterSpatialSubsets(StringValues subsetValues)
    {
        var spatial = new List<string?>();
        var anyTemporal = false;
        foreach (var value in subsetValues)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                spatial.Add(value);
                continue;
            }

            if (IsTemporalSubset(value))
            {
                anyTemporal = true;
                continue;
            }

            spatial.Add(value);
        }

        return anyTemporal ? new StringValues(spatial.ToArray()) : subsetValues;
    }

    private static bool IsTemporalSubset(string subsetValue)
    {
        var trimmed = subsetValue.Trim();
        var open = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (open <= 0)
        {
            return false;
        }

        return Wcs20Utilities.TemporalAxisLabels.Contains(trimmed[..open].Trim());
    }

    // Recognises the spatial subset axis labels handled by TryParseSubsetEnvelope
    // (the X/E/Long/Lon and Y/N/Lat aliases). Used to single out additional,
    // non-spatial dimension axes for separate validation.
    private static bool IsSpatialAxisLabel(string axisLabel)
        => axisLabel.ToLowerInvariant() switch
        {
            "x" or "e" or "long" or "lon" or "y" or "n" or "lat" => true,
            _ => false,
        };

    // #1872: WCS 2.0 additional-dimension subsetting beyond phenomenonTime.
    //
    // Coverages may declare named dimension axes in addition to the two spatial axes
    // and the temporal axis — for example a vertical/elevation axis on a
    // multidimensional (Zarr) coverage resolved through the same coordinate/time-axis
    // indexer the OGC API Coverages `datetime` path uses (#1790). This pass parses
    // and validates any SUBSET entry that targets neither a spatial axis nor the
    // temporal axis, and resolves it against the coverage's registered additional
    // dimension axes.
    //
    // The classic WCS GetCoverage path serves over the primary 2D raster via
    // IRasterStore, which carries no non-spatial dimension axes today, so the set of
    // registered additional axes is empty: any well-formed additional-dimension slice
    // therefore names an axis this coverage does not offer and yields InvalidAxisLabel,
    // while a malformed one yields InvalidSubsetting. When the multidimensional (Zarr)
    // read path is wired into classic WCS, the resolved axis index would be carried on
    // the canonical raster query here instead of being rejected. See the plan note on
    // ticket #1872.
    private static bool TryValidateAdditionalDimensionSubsets(
        StringValues subsetValues,
        RasterInfo raster,
        out WcsParameterError error)
    {
        error = default;

        // Registered additional (non-spatial, non-temporal) dimension axes for this
        // coverage. Empty for the primary-raster path; this is the extension point for
        // multidimensional coverages.
        var additionalAxes = ResolveAdditionalDimensionAxes(raster);

        foreach (var rawValue in subsetValues)
        {
            if (string.IsNullOrWhiteSpace(rawValue) || IsTemporalSubset(rawValue))
            {
                continue;
            }

            if (!TryParseSingleSubset(rawValue, out var normalizedAxis, out _, out _, out _))
            {
                // Could not parse axis(low) / axis(low,high). If the raw label is a
                // recognised spatial axis the spatial parser will surface the precise
                // error; otherwise treat the malformed value as invalid subsetting.
                if (LooksLikeSpatialAxis(rawValue))
                {
                    continue;
                }

                error = new WcsParameterError(
                    Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                    "SUBSET values must use axis(low) or axis(low,high) syntax.",
                    Wcs20Utilities.Parameters.Subset);
                return false;
            }

            if (IsSpatialAxisLabel(normalizedAxis))
            {
                // Spatial axes are handled by TryParseSubsetEnvelope.
                continue;
            }

            // A well-formed slice/trim on a named axis that is neither spatial nor
            // temporal. Resolve it against the coverage's additional dimension axes.
            if (!additionalAxes.Contains(normalizedAxis))
            {
                error = new WcsParameterError(
                    Wcs20Utilities.ExceptionCodes.InvalidAxisLabel,
                    $"Unsupported SUBSET axis label '{normalizedAxis}'. This coverage offers the spatial axes (x, y and their E/N/Long/Lat aliases)" +
                    (raster.AcquisitionDate is null ? "." : " and the temporal axis (phenomenonTime).") +
                    " No additional dimension axes are available for this coverage.",
                    Wcs20Utilities.Parameters.Subset);
                return false;
            }

            // A registered additional axis was matched but the multidimensional read
            // path is not wired into classic WCS yet; surface a precise, spec-correct
            // error rather than silently ignoring the requested slice (#1872).
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                $"SUBSET on the '{normalizedAxis}' dimension axis is recognised but not yet served by this coverage endpoint.",
                Wcs20Utilities.Parameters.Subset);
            return false;
        }

        return true;
    }

    // The set of additional (non-spatial, non-temporal) dimension axes a coverage
    // offers. The primary-raster IRasterStore path exposes none; this is the hook for
    // multidimensional (Zarr) coverages once their slice read path is wired into
    // classic WCS (#1872).
    private static ImmutableHashSet<string> ResolveAdditionalDimensionAxes(RasterInfo raster)
    {
        _ = raster;
        return ImmutableHashSet<string>.Empty;
    }

    // True when the trimmed SUBSET value's axis label (the text before the first
    // parenthesis) is a recognised spatial axis, regardless of whether the value
    // portion parses — lets the spatial parser own the precise spatial error message.
    private static bool LooksLikeSpatialAxis(string subsetValue)
    {
        var trimmed = subsetValue.Trim();
        var open = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (open <= 0)
        {
            return false;
        }

        return IsSpatialAxisLabel(trimmed[..open].Trim());
    }

    // WCS 2.0 temporal subsetting. The primary raster carries a single effective
    // acquisition instant (AcquisitionDate, falling back to CreatedAt). A
    // phenomenonTime slice or trim selects the coverage only when that instant
    // satisfies the requested window; otherwise the temporal window excludes the
    // coverage and we surface InvalidSubsetting per the WCS subsetting rules.
    private static bool TryApplyTemporalSubset(IQueryCollection query, RasterInfo raster, out WcsParameterError error)
    {
        error = default;

        var subsetValues = GetQueryValues(query, Wcs20Utilities.Parameters.Subset);
        var acquisition = raster.AcquisitionDate ?? raster.CreatedAt;

        foreach (var rawValue in subsetValues)
        {
            if (string.IsNullOrWhiteSpace(rawValue) || !IsTemporalSubset(rawValue))
            {
                continue;
            }

            if (!TryParseTemporalSubset(rawValue, out var low, out var high))
            {
                error = new WcsParameterError(
                    Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                    "Temporal SUBSET must use phenomenonTime(\"instant\") or phenomenonTime(\"start\",\"end\") with ISO 8601 timestamps.",
                    Wcs20Utilities.Parameters.Subset);
                return false;
            }

            if (acquisition < low || acquisition > high)
            {
                error = new WcsParameterError(
                    Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                    "The requested phenomenonTime window does not intersect the coverage acquisition time.",
                    Wcs20Utilities.Parameters.Subset);
                return false;
            }
        }

        // DATETIME (OGC API style) and TIME (classic) aliases select the same
        // single-acquisition coverage as SUBSET=phenomenonTime. They are additive
        // conveniences over the SUBSET temporal path and share its window semantics.
        if (!TryApplyTemporalAlias(query, Wcs20Utilities.Parameters.DateTime, acquisition, out error) ||
            !TryApplyTemporalAlias(query, Wcs20Utilities.Parameters.Time, acquisition, out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryApplyTemporalAlias(
        IQueryCollection query,
        string parameter,
        DateTimeOffset acquisition,
        out WcsParameterError error)
    {
        error = default;

        var raw = GetQueryValue(query, parameter);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!TryParseTemporalAlias(raw, out var low, out var high))
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                $"{parameter} must be an ISO 8601 instant or a start/end interval (low/high, '..' for open-ended).",
                parameter);
            return false;
        }

        if (acquisition < low || acquisition > high)
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                $"The requested {parameter} window does not intersect the coverage acquisition time.",
                parameter);
            return false;
        }

        return true;
    }

    // Parses a DATETIME/TIME alias value: an instant ("2024-01-01"), a closed or
    // half-open interval using the OGC '/' separator ("t0/t1", "../t1", "t0/.."),
    // or a comma-separated pair. Reuses the SUBSET temporal-bound parser so date-only
    // instants expand to a whole-day window consistently.
    private static bool TryParseTemporalAlias(string value, out DateTimeOffset low, out DateTimeOffset high)
    {
        low = default;
        high = default;

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var separator = trimmed.Contains('/', StringComparison.Ordinal) ? '/' : ',';
        var parts = trimmed.Split(separator);
        if (parts.Length is < 1 or > 2)
        {
            return false;
        }

        if (parts.Length == 1)
        {
            return TryParseTemporalBound(parts[0], isLowerBound: true, out low) &&
                   TryParseTemporalBound(parts[0], isLowerBound: false, out high) &&
                   high >= low;
        }

        return TryParseTemporalBound(parts[0], isLowerBound: true, out low) &&
               TryParseTemporalBound(parts[1], isLowerBound: false, out high) &&
               high >= low;
    }

    private static bool TryParseTemporalSubset(string subsetValue, out DateTimeOffset low, out DateTimeOffset high)
    {
        low = default;
        high = default;

        var trimmed = subsetValue.Trim();
        var open = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (open <= 0 || !trimmed.EndsWith(')'))
        {
            return false;
        }

        var inner = trimmed[(open + 1)..^1];
        var parts = SplitTopLevel(inner).ToArray();
        if (parts.Length is < 1 or > 2)
        {
            return false;
        }

        if (!TryParseTemporalBound(parts[0], isLowerBound: true, out low))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            // A slice selects the whole calendar resolution supplied (e.g. a bare
            // date covers the entire UTC day); reuse the upper bound for that span.
            return TryParseTemporalBound(parts[0], isLowerBound: false, out high) && high >= low;
        }

        if (!TryParseTemporalBound(parts[1], isLowerBound: false, out high) || high < low)
        {
            return false;
        }

        return true;
    }

    private static bool TryParseTemporalBound(string token, bool isLowerBound, out DateTimeOffset bound)
    {
        bound = default;
        var value = token.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "*", StringComparison.Ordinal) ||
            string.Equals(value, "..", StringComparison.Ordinal))
        {
            // Open-ended bound ('*' for WCS subset, '..' for OGC datetime intervals).
            bound = isLowerBound ? DateTimeOffset.MinValue : DateTimeOffset.MaxValue;
            return true;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            // A date-only token denotes the whole UTC day: the lower bound is the
            // start of the day, the upper bound the final instant before the next.
            if (!isLowerBound && IsDateOnly(value))
            {
                bound = parsed.AddDays(1).AddTicks(-1);
                return true;
            }

            bound = parsed;
            return true;
        }

        return false;
    }

    private static bool IsDateOnly(string value)
        => value.Length == 10 &&
           value[4] == '-' &&
           value[7] == '-' &&
           !value.Contains('T', StringComparison.Ordinal) &&
           !value.Contains(':', StringComparison.Ordinal);

    // Refines the Scaling-extension base grid to the trimmed pixel dimensions when
    // the spatial subset is expressed in the coverage native CRS. For non-native
    // subsetting CRSs the trimmed pixel count is not directly derivable here, so
    // the full native grid remains the base (scaling still applies uniformly).
    private static void TryRefineBaseGrid(
        RasterInfo raster,
        CrsDefinition subsettingCrs,
        Envelope clip,
        ref int baseWidth,
        ref int baseHeight)
    {
        if (!IsNativeSubsettingCrs(raster, subsettingCrs) || !TryResolveExtent(raster, out var extent))
        {
            return;
        }

        var fullWidth = extent.XMax - extent.XMin;
        var fullHeight = extent.YMax - extent.YMin;
        if (fullWidth <= 0 || fullHeight <= 0 || raster.Width <= 0 || raster.Height <= 0)
        {
            return;
        }

        var clipWidth = Math.Min(clip.MaxX, extent.XMax) - Math.Max(clip.MinX, extent.XMin);
        var clipHeight = Math.Min(clip.MaxY, extent.YMax) - Math.Max(clip.MinY, extent.YMin);
        if (clipWidth <= 0 || clipHeight <= 0)
        {
            return;
        }

        baseWidth = Math.Max(1, (int)Math.Round(raster.Width * (clipWidth / fullWidth), MidpointRounding.AwayFromZero));
        baseHeight = Math.Max(1, (int)Math.Round(raster.Height * (clipHeight / fullHeight), MidpointRounding.AwayFromZero));
    }

    // WCS 2.0 Scaling extension coordinator. At most one scaling operator may be
    // supplied per request (SCALESIZE, SCALEFACTOR, SCALEAXES, or SCALEEXTENT);
    // each resolves to explicit output width/height in pixels so the shared raster
    // export pipeline resamples the (possibly subset-trimmed) base grid uniformly.
    private static bool TryResolveScaling(
        IQueryCollection query,
        int baseWidth,
        int baseHeight,
        out int? width,
        out int? height,
        out WcsParameterError error)
    {
        width = null;
        height = null;
        error = default;

        var scaleSize = GetQueryValue(query, Wcs20Utilities.Parameters.ScaleSize);
        var scaleFactor = GetQueryValue(query, Wcs20Utilities.Parameters.ScaleFactor);
        var scaleAxes = GetQueryValue(query, Wcs20Utilities.Parameters.ScaleAxes);
        var scaleExtent = GetQueryValue(query, Wcs20Utilities.Parameters.ScaleExtent);

        var supplied = new[] { scaleSize, scaleFactor, scaleAxes, scaleExtent }
            .Count(value => !string.IsNullOrWhiteSpace(value));
        if (supplied > 1)
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                "Specify at most one of SCALESIZE, SCALEFACTOR, SCALEAXES, or SCALEEXTENT.",
                Wcs20Utilities.Parameters.ScaleSize);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(scaleFactor))
        {
            return TryResolveScaleFactor(scaleFactor, baseWidth, baseHeight, out width, out height, out error);
        }

        if (!string.IsNullOrWhiteSpace(scaleAxes))
        {
            return TryResolveScaleAxes(scaleAxes, baseWidth, baseHeight, out width, out height, out error);
        }

        if (!string.IsNullOrWhiteSpace(scaleExtent))
        {
            return TryResolveScaleExtent(scaleExtent, baseWidth, baseHeight, out width, out height, out error);
        }

        return TryResolveScaleSize(query, out width, out height, out error);
    }

    // WCS 2.0 Scaling extension (SCALEFACTOR): a single positive factor scaling
    // every spatial axis of the base grid uniformly, e.g. SCALEFACTOR=0.5.
    private static bool TryResolveScaleFactor(
        string value,
        int baseWidth,
        int baseHeight,
        out int? width,
        out int? height,
        out WcsParameterError error)
    {
        width = null;
        height = null;
        error = default;

        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) ||
            !CoverageScaling.TryResolveFactor(baseWidth, baseHeight, factor, out var resolved))
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                $"SCALEFACTOR '{value}' must be a positive number.",
                Wcs20Utilities.Parameters.ScaleFactor);
            return false;
        }

        width = resolved.Width;
        height = resolved.Height;
        return true;
    }

    // WCS 2.0 Scaling extension (SCALEAXES): per-axis positive factors, e.g.
    // SCALEAXES=x(0.5),y(2). Omitted axes keep their base size.
    private static bool TryResolveScaleAxes(
        string value,
        int baseWidth,
        int baseHeight,
        out int? width,
        out int? height,
        out WcsParameterError error)
    {
        width = null;
        height = null;
        error = default;

        double? xFactor = null;
        double? yFactor = null;
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseSingleSubset(token, out var axis, out var factor, out _, out var isSlice) || !isSlice)
            {
                error = CreateScaleAxesError(token);
                return false;
            }

            if (string.Equals(axis, "x", StringComparison.Ordinal))
            {
                if (xFactor.HasValue)
                {
                    error = CreateScaleAxesError("duplicate x axis");
                    return false;
                }

                xFactor = factor;
            }
            else if (string.Equals(axis, "y", StringComparison.Ordinal))
            {
                if (yFactor.HasValue)
                {
                    error = CreateScaleAxesError("duplicate y axis");
                    return false;
                }

                yFactor = factor;
            }
            else
            {
                error = CreateScaleAxesError(token);
                return false;
            }
        }

        if (!CoverageScaling.TryResolveAxes(baseWidth, baseHeight, xFactor, yFactor, out var resolved))
        {
            error = CreateScaleAxesError(value);
            return false;
        }

        width = resolved.Width;
        height = resolved.Height;
        return true;
    }

    // WCS 2.0 Scaling extension (SCALEEXTENT): explicit per-axis output grid sizes
    // using a grid-coordinate interval, e.g. SCALEEXTENT=x(0,511),y(0,255). The
    // span (high-low+1) becomes the output size for that axis; omitted axes keep
    // their base size.
    private static bool TryResolveScaleExtent(
        string value,
        int baseWidth,
        int baseHeight,
        out int? width,
        out int? height,
        out WcsParameterError error)
    {
        width = null;
        height = null;
        error = default;

        int? xSize = null;
        int? ySize = null;
        foreach (var token in SplitTopLevel(value))
        {
            if (!TryParseScaleExtentInterval(token, out var axis, out var size))
            {
                error = CreateScaleExtentError(token);
                return false;
            }

            if (string.Equals(axis, "x", StringComparison.Ordinal))
            {
                if (xSize.HasValue)
                {
                    error = CreateScaleExtentError("duplicate x axis");
                    return false;
                }

                xSize = size;
            }
            else if (string.Equals(axis, "y", StringComparison.Ordinal))
            {
                if (ySize.HasValue)
                {
                    error = CreateScaleExtentError("duplicate y axis");
                    return false;
                }

                ySize = size;
            }
            else
            {
                error = CreateScaleExtentError(token);
                return false;
            }
        }

        if (!CoverageScaling.TryResolveExtent(baseWidth, baseHeight, xSize, ySize, out var resolved))
        {
            error = CreateScaleExtentError(value);
            return false;
        }

        width = resolved.Width;
        height = resolved.Height;
        return true;
    }

    // SCALEEXTENT uses comma-separated axis(low,high) intervals. The caller
    // tokenises with SplitTopLevel so the comma inside the (low,high) pair is
    // preserved and each token is a complete axis interval.
    private static bool TryParseScaleExtentInterval(string token, out string axis, out int size)
    {
        axis = string.Empty;
        size = 0;

        if (!TryParseSingleSubset(token, out axis, out var low, out var high, out var isSlice) ||
            isSlice ||
            low != Math.Floor(low) ||
            high != Math.Floor(high) ||
            high < low)
        {
            return false;
        }

        var span = (long)high - (long)low + 1;
        if (span <= 0 || span > int.MaxValue)
        {
            return false;
        }

        size = (int)span;
        return true;
    }

    // Splits on commas that sit outside parentheses so axis intervals such as
    // x(0,511) survive intact while the axis separator comma still splits.
    private static IEnumerable<string> SplitTopLevel(string value)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == ',' && depth == 0)
            {
                var token = value[start..i].Trim();
                if (token.Length > 0)
                {
                    yield return token;
                }

                start = i + 1;
            }
        }

        var last = value[start..].Trim();
        if (last.Length > 0)
        {
            yield return last;
        }
    }

    private static WcsParameterError CreateScaleAxesError(string token)
        => new(
            Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
            $"SCALEAXES '{token}' must use axis(factor) with a positive factor on the x/E/Long or y/N/Lat axis.",
            Wcs20Utilities.Parameters.ScaleAxes);

    private static WcsParameterError CreateScaleExtentError(string token)
        => new(
            Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
            $"SCALEEXTENT '{token}' must use axis(low,high) grid intervals on the x/E/Long or y/N/Lat axis.",
            Wcs20Utilities.Parameters.ScaleExtent);

    // WCS 2.0 Scaling extension (SCALESIZE): sets an explicit output grid size per axis,
    // e.g. SCALESIZE=x(512),y(256). The x/E/Long axis maps to output width and y/N/Lat to
    // output height. Only the unambiguous explicit-size form is supported.
    private static bool TryResolveScaleSize(
        IQueryCollection query,
        out int? width,
        out int? height,
        out WcsParameterError error)
    {
        width = null;
        height = null;
        error = default;

        var values = GetQueryValues(query, Wcs20Utilities.Parameters.ScaleSize);
        foreach (var rawValue in values)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            foreach (var token in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryParseSingleSubset(token, out var axis, out var size, out _, out var isSlice) ||
                    !isSlice || size <= 0 || size != Math.Floor(size))
                {
                    error = CreateScaleSizeError(token);
                    return false;
                }

                var target = (int)size;
                if (string.Equals(axis, "x", StringComparison.Ordinal))
                {
                    if (width.HasValue)
                    {
                        error = CreateScaleSizeError("duplicate x axis");
                        return false;
                    }

                    width = target;
                }
                else if (string.Equals(axis, "y", StringComparison.Ordinal))
                {
                    if (height.HasValue)
                    {
                        error = CreateScaleSizeError("duplicate y axis");
                        return false;
                    }

                    height = target;
                }
                else
                {
                    error = CreateScaleSizeError(token);
                    return false;
                }
            }
        }

        return true;
    }

    private static WcsParameterError CreateScaleSizeError(string token)
        => new(
            Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
            $"SCALESIZE '{token}' must use axis(size) with a positive integer size on the x/E/Long or y/N/Lat axis.",
            Wcs20Utilities.Parameters.ScaleSize);

    // WCS 2.0 Range Subsetting extension: RANGESUBSET selects coverage range-type fields,
    // which DescribeCoverage advertises as band1..bandN. Accepts a comma list of field
    // names and band1:bandN intervals.
    private static bool TryResolveRangeSubset(
        IQueryCollection query,
        RasterInfo raster,
        out int[]? bands,
        out WcsParameterError error)
    {
        bands = null;
        error = default;

        var rangeSubset = GetQueryValue(query, Wcs20Utilities.Parameters.RangeSubset);
        if (string.IsNullOrWhiteSpace(rangeSubset))
        {
            return true;
        }

        var bandCount = Math.Max(raster.BandCount, 1);
        var selected = new List<int>();
        foreach (var token in rangeSubset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var intervalSeparator = token.IndexOf(':');
            if (intervalSeparator >= 0)
            {
                if (!TryParseBandField(token[..intervalSeparator], bandCount, out var low) ||
                    !TryParseBandField(token[(intervalSeparator + 1)..], bandCount, out var high) ||
                    low > high)
                {
                    error = CreateRangeSubsetError(token);
                    return false;
                }

                for (var band = low; band <= high; band++)
                {
                    selected.Add(band);
                }
            }
            else if (TryParseBandField(token, bandCount, out var band))
            {
                selected.Add(band);
            }
            else
            {
                error = CreateRangeSubsetError(token);
                return false;
            }
        }

        bands = selected.Count == 0 ? null : selected.ToArray();
        return true;
    }

    private static bool TryParseBandField(string field, int bandCount, out int band)
    {
        band = 0;
        var trimmed = field.Trim();
        if (!trimmed.StartsWith("band", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(trimmed.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out band))
        {
            return false;
        }

        return band >= 1 && band <= bandCount;
    }

    private static WcsParameterError CreateRangeSubsetError(string token)
        => new(
            Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
            $"RANGESUBSET field '{token}' is not a valid coverage band. Use band1..bandN.",
            Wcs20Utilities.Parameters.RangeSubset);

    private static bool TryResolveRequestCrs(
        IQueryCollection query,
        RasterInfo raster,
        out CrsDefinition subsettingCrs,
        out int? outputSrid,
        out WcsParameterError error)
    {
        subsettingCrs = default;
        outputSrid = null;
        error = default;

        var nativeSrid = raster.Extent?.Srid ?? raster.Srid;
        if (!nativeSrid.HasValue || nativeSrid.Value <= 0)
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.NoApplicableCode,
                "Coverage CRS is not available.",
                Wcs20Utilities.Parameters.CoverageId);
            return false;
        }

        var subsettingCrsValue = GetQueryValue(query, Wcs20Utilities.Parameters.SubsettingCrs);
        var bboxCrsValue = GetQueryValue(query, Wcs20Utilities.Parameters.BBoxCrs);
        if (!string.IsNullOrWhiteSpace(subsettingCrsValue) && !string.IsNullOrWhiteSpace(bboxCrsValue) &&
            !string.Equals(subsettingCrsValue, bboxCrsValue, StringComparison.OrdinalIgnoreCase))
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                "SUBSETTINGCRS and BBOXCRS must not conflict.",
                Wcs20Utilities.Parameters.SubsettingCrs);
            return false;
        }

        var effectiveSubsettingCrs = subsettingCrsValue ?? bboxCrsValue ?? nativeSrid.Value.ToString(CultureInfo.InvariantCulture);
        if (!SpatialReferenceHelpers.TryParseCrsDefinition(effectiveSubsettingCrs, out subsettingCrs))
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                "SUBSETTINGCRS must be a supported CRS identifier.",
                Wcs20Utilities.Parameters.SubsettingCrs);
            return false;
        }

        var outputCrsValue = GetQueryValue(query, Wcs20Utilities.Parameters.OutputCrs);
        if (!string.IsNullOrWhiteSpace(outputCrsValue))
        {
            if (!SpatialReferenceHelpers.TryParseCrsDefinition(outputCrsValue, out var outputCrs))
            {
                error = new WcsParameterError(
                    Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                    "OUTPUTCRS must be a supported CRS identifier.",
                    Wcs20Utilities.Parameters.OutputCrs);
                return false;
            }

            outputSrid = outputCrs.Srid;
        }

        return true;
    }

    private static bool TryParseSubsetEnvelope(
        StringValues subsetValues,
        RasterInfo raster,
        CrsDefinition subsettingCrs,
        out Envelope envelope,
        out WcsParameterError error)
    {
        envelope = default!;
        error = default;

        if (!TryResolveExtent(raster, out var extent))
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.NoApplicableCode,
                "Coverage extent is required when SUBSET is supplied.",
                Wcs20Utilities.Parameters.Subset);
            return false;
        }

        double? minX = null;
        double? maxX = null;
        double? minY = null;
        double? maxY = null;

        foreach (var subsetValue in subsetValues)
        {
            if (string.IsNullOrWhiteSpace(subsetValue))
            {
                continue;
            }

            if (!TryParseSingleSubset(subsetValue, out var axis, out var low, out var high, out var isSlice))
            {
                error = new WcsParameterError(
                    Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                    "SUBSET values must use axis(low) or axis(low,high) syntax.",
                    Wcs20Utilities.Parameters.Subset);
                return false;
            }

            if (string.Equals(axis, "x", StringComparison.Ordinal))
            {
                if (minX.HasValue)
                {
                    error = new WcsParameterError(
                        Wcs20Utilities.ExceptionCodes.InvalidAxisLabel,
                        "Duplicate X/E/Long subset axis.",
                        Wcs20Utilities.Parameters.Subset);
                    return false;
                }

                if (!isSlice && low >= high)
                {
                    error = new WcsParameterError(
                        Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                        "X/E/Long subset lower bound must be less than the upper bound.",
                        Wcs20Utilities.Parameters.Subset);
                    return false;
                }

                minX = low;
                maxX = high;
            }
            else if (string.Equals(axis, "y", StringComparison.Ordinal))
            {
                if (minY.HasValue)
                {
                    error = new WcsParameterError(
                        Wcs20Utilities.ExceptionCodes.InvalidAxisLabel,
                        "Duplicate Y/N/Lat subset axis.",
                        Wcs20Utilities.Parameters.Subset);
                    return false;
                }

                if (!isSlice && low >= high)
                {
                    error = new WcsParameterError(
                        Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                        "Y/N/Lat subset lower bound must be less than the upper bound.",
                        Wcs20Utilities.Parameters.Subset);
                    return false;
                }

                minY = low;
                maxY = high;
            }
            else
            {
                error = new WcsParameterError(
                    Wcs20Utilities.ExceptionCodes.InvalidAxisLabel,
                    $"Unsupported SUBSET axis label '{axis}'. Supported labels are x, y, E, N, Long, Lon, and Lat.",
                    Wcs20Utilities.Parameters.Subset);
                return false;
            }
        }

        var hasXAxis = minX.HasValue;
        var hasYAxis = minY.HasValue;
        if (!hasXAxis && !hasYAxis)
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                "SUBSET must include at least one supported axis.",
                Wcs20Utilities.Parameters.Subset);
            return false;
        }

        if (hasXAxis != hasYAxis && !IsNativeSubsettingCrs(raster, subsettingCrs))
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                "Single-axis SUBSET requires SUBSETTINGCRS or BBOXCRS to match the coverage native CRS.",
                Wcs20Utilities.Parameters.Subset);
            return false;
        }

        var resolvedMinX = minX ?? extent.XMin;
        var resolvedMaxX = maxX ?? extent.XMax;
        var resolvedMinY = minY ?? extent.YMin;
        var resolvedMaxY = maxY ?? extent.YMax;

        return TryCreateEnvelope(
            resolvedMinX,
            resolvedMinY,
            resolvedMaxX,
            resolvedMaxY,
            subsettingCrs,
            Wcs20Utilities.Parameters.Subset,
            out envelope,
            out error);
    }

    private static bool IsNativeSubsettingCrs(RasterInfo raster, CrsDefinition subsettingCrs)
    {
        var nativeSrid = raster.Extent?.Srid ?? raster.Srid;
        return nativeSrid == subsettingCrs.Srid;
    }

    private static bool TryParseSingleSubset(
        string subset,
        out string normalizedAxis,
        out double low,
        out double high,
        out bool isSlice)
    {
        normalizedAxis = string.Empty;
        low = 0;
        high = 0;
        isSlice = false;

        var trimmed = subset.Trim();
        var open = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (open <= 0 || !trimmed.EndsWith(')'))
        {
            return false;
        }

        var axis = trimmed[..open].Trim();
        var values = trimmed[(open + 1)..^1].Split(",", StringSplitOptions.TrimEntries);
        if (values.Length is < 1 or > 2 ||
            !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out low))
        {
            return false;
        }

        if (values.Length == 1)
        {
            high = low;
            isSlice = true;
        }
        else if (!double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out high))
        {
            return false;
        }

        normalizedAxis = axis.ToLowerInvariant() switch
        {
            "x" or "e" or "long" or "lon" => "x",
            "y" or "n" or "lat" => "y",
            _ => axis,
        };
        return true;
    }

    private static bool TryParseBboxEnvelope(
        string bbox,
        CrsDefinition subsettingCrs,
        out Envelope envelope,
        out WcsParameterError error)
    {
        envelope = default!;
        error = default;

        // Use the shared parameter kernel for tokenisation/coercion so all
        // protocols share one bbox parser. WCS 2.0 rejects the 3D form and
        // does not honour a trailing CRS suffix (CRS comes from SUBSETTINGCRS
        // / BBOXCRS), so enforce those constraints after the shared parse.
        if (!OgcParameterValidator.TryParseBbox(bbox, out var parsed, out _))
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                "BBOX must use xmin,ymin,xmax,ymax syntax.",
                Wcs20Utilities.Parameters.BBox);
            return false;
        }

        if (parsed.Is3D || parsed.CrsToken is not null)
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                "BBOX must use xmin,ymin,xmax,ymax syntax.",
                Wcs20Utilities.Parameters.BBox);
            return false;
        }

        return TryCreateEnvelope(
            parsed.MinX,
            parsed.MinY,
            parsed.MaxX,
            parsed.MaxY,
            subsettingCrs,
            Wcs20Utilities.Parameters.BBox,
            out envelope,
            out error);
    }

    private static bool TryCreateEnvelope(
        double minX,
        double minY,
        double maxX,
        double maxY,
        CrsDefinition crs,
        string locator,
        out Envelope envelope,
        out WcsParameterError error)
    {
        envelope = default!;
        error = default;

        var bbox = string.Join(
            ',',
            FormatDouble(minX),
            FormatDouble(minY),
            FormatDouble(maxX),
            FormatDouble(maxY));

        if (!RasterParsingHelpers.TryParseBoundingBox(
                bbox,
                AxisOrder.EastNorth,
                crs.IsGeographic,
                out var parsedMinX,
                out var parsedMinY,
                out var parsedMaxX,
                out var parsedMaxY))
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                "Spatial subset coordinates are invalid for the selected CRS.",
                locator);
            return false;
        }

        envelope = new Envelope(parsedMinX, parsedMaxX, parsedMinY, parsedMaxY);
        return true;
    }

    private static RasterClipRegion CreateClipRegion(Envelope envelope, int srid)
    {
        var geometry = new GeometryFactory().ToGeometry(envelope);
        var writer = new WKBWriter();
        return new RasterClipRegion
        {
            Geometry = writer.Write(geometry),
            Srid = srid
        };
    }

    private static bool TryParseCoverageFormat(
        string? format,
        out RasterFormat rasterFormat,
        out string contentType)
    {
        rasterFormat = RasterFormat.TIFF;
        contentType = Wcs20Utilities.TiffContentType;

        if (string.IsNullOrWhiteSpace(format))
        {
            return true;
        }

        // Delegate set-membership to the shared kernel so format whitelisting
        // is consistent across protocols. The switch below still owns the
        // protocol-specific mapping from token -> RasterFormat enum + content
        // type, which is intentionally WCS-2.0-specific.
        if (!OgcParameterValidator.TryParseFormat(format, Wcs20Utilities.CoverageFormatsList, out var normalized, out _))
        {
            return false;
        }

        switch (normalized.ToLowerInvariant())
        {
            case "image/tiff":
            case "image/geotiff":
            case "tiff":
            case "tif":
                rasterFormat = RasterFormat.TIFF;
                contentType = Wcs20Utilities.TiffContentType;
                return true;
            case "image/png":
            case "png":
                rasterFormat = RasterFormat.PNG;
                contentType = Wcs20Utilities.PngContentType;
                return true;
            case "image/jpeg":
            case "jpg":
            case "jpeg":
                rasterFormat = RasterFormat.JPEG;
                contentType = Wcs20Utilities.JpegContentType;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseCoverageIds(
        IQueryCollection query,
        out WcsCoverageIdentifier[] coverageIds,
        out WcsParameterError error)
    {
        coverageIds = [];
        error = default;

        var values = GetQueryValues(query, Wcs20Utilities.Parameters.CoverageId);
        if (values.Count == 0)
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.MissingParameterValue,
                "COVERAGEID is required.",
                Wcs20Utilities.Parameters.CoverageId);
            return false;
        }

        var parsed = new List<WcsCoverageIdentifier>();
        foreach (var token in SplitCsvValues(values))
        {
            if (!TryParseCoverageId(token, out var coverageId))
            {
                error = new WcsParameterError(
                    Wcs20Utilities.ExceptionCodes.InvalidParameterValue,
                    "COVERAGEID values must be valid coverage identifiers.",
                    Wcs20Utilities.Parameters.CoverageId);
                return false;
            }

            parsed.Add(coverageId);
        }

        if (parsed.Count == 0)
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.MissingParameterValue,
                "COVERAGEID is required.",
                Wcs20Utilities.Parameters.CoverageId);
            return false;
        }

        coverageIds = parsed.Distinct().ToArray();
        return true;
    }

    private static bool TryParseCoverageId(string token, out WcsCoverageIdentifier coverageId)
    {
        const string CoveragePrefix = "coverage_";

        coverageId = default;
        var value = token.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var layerIdCandidate = value;
        if (layerIdCandidate.StartsWith(CoveragePrefix, StringComparison.OrdinalIgnoreCase))
        {
            layerIdCandidate = layerIdCandidate[CoveragePrefix.Length..];
        }

        if (int.TryParse(layerIdCandidate, NumberStyles.None, CultureInfo.InvariantCulture, out var layerId) &&
            layerId >= 0)
        {
            coverageId = new WcsCoverageIdentifier(value, layerId);
            return true;
        }

        if (IsValidCoverageIdentifier(value))
        {
            coverageId = new WcsCoverageIdentifier(value, null);
            return true;
        }

        return false;
    }

    private static bool IsValidCoverageIdentifier(string value)
    {
        try
        {
            XmlConvert.VerifyNCName(value);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static IResult CreateGetCoverageParameterError(WcsParameterError error)
        => error.ExceptionCode is Wcs20Utilities.ExceptionCodes.InvalidAxisLabel or Wcs20Utilities.ExceptionCodes.InvalidSubsetting
            ? Wcs20ErrorResults.CreateNotFound(error.ExceptionCode, error.Detail, error.Locator)
            : Wcs20ErrorResults.CreateBadRequest(error.ExceptionCode, error.Detail, error.Locator);

    private static string FormatCoverageId(int layerId)
        => string.Concat("coverage_", layerId.ToString(CultureInfo.InvariantCulture));

    private static bool TryParseSections(string? value, out IReadOnlySet<string>? sections, out string? error)
    {
        sections = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in SplitCsv(value))
        {
            if (string.Equals(section, "All", StringComparison.OrdinalIgnoreCase))
            {
                sections = null;
                return true;
            }

            if (!IsKnownSection(section))
            {
                error = $"Unsupported capabilities section '{section}'.";
                return false;
            }

            parsed.Add(section);
        }

        sections = parsed;
        return true;
    }

    private static bool IsKnownSection(string section)
        => string.Equals(section, "ServiceIdentification", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(section, "ServiceProvider", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(section, "OperationsMetadata", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(section, "ServiceMetadata", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(section, "Contents", StringComparison.OrdinalIgnoreCase);

    private static bool IncludesSection(IReadOnlySet<string>? sections, string section)
        => sections is null || sections.Contains(section);

    private static bool TryResolveExtent(RasterInfo raster, out RasterExtent extent)
    {
        if (raster.Extent.HasValue)
        {
            extent = raster.Extent.Value;
            return true;
        }

        var geoTransform = raster.GeoTransform;
        if (geoTransform is { Length: >= 6 } &&
            raster.Width > 0 &&
            raster.Height > 0)
        {
            var x0 = geoTransform[0];
            var y0 = geoTransform[3];
            var x1 = geoTransform[0] + geoTransform[1] * raster.Width + geoTransform[2] * raster.Height;
            var y1 = geoTransform[3] + geoTransform[4] * raster.Width + geoTransform[5] * raster.Height;
            extent = new RasterExtent
            {
                XMin = Math.Min(x0, x1),
                YMin = Math.Min(y0, y1),
                XMax = Math.Max(x0, x1),
                YMax = Math.Max(y0, y1),
                Srid = raster.Srid
            };
            return true;
        }

        extent = default;
        return false;
    }

    private static bool TryResolveGridVectors(
        RasterInfo raster,
        RasterExtent extent,
        out Coordinate origin,
        out Coordinate xVector,
        out Coordinate yVector)
    {
        var geoTransform = raster.GeoTransform;
        if (geoTransform is { Length: >= 6 })
        {
            origin = new Coordinate(geoTransform[0], geoTransform[3]);
            xVector = new Coordinate(geoTransform[1], geoTransform[4]);
            yVector = new Coordinate(geoTransform[2], geoTransform[5]);
            return true;
        }

        if (raster.Width <= 0 || raster.Height <= 0)
        {
            origin = new Coordinate();
            xVector = new Coordinate();
            yVector = new Coordinate();
            return false;
        }

        origin = new Coordinate(extent.XMin, extent.YMax);
        xVector = new Coordinate((extent.XMax - extent.XMin) / raster.Width, 0);
        yVector = new Coordinate(0, -((extent.YMax - extent.YMin) / raster.Height));
        return true;
    }

    private static IEnumerable<XAttribute> RootNamespaceAttributes()
    {
        yield return new XAttribute(XNamespace.Xmlns + "wcs", Wcs20Utilities.WcsNamespace);
        yield return new XAttribute(XNamespace.Xmlns + "ows", Wcs20Utilities.OwsNamespace);
        yield return new XAttribute(XNamespace.Xmlns + "gml", Wcs20Utilities.GmlNamespace);
        yield return new XAttribute(XNamespace.Xmlns + "gmlcov", Wcs20Utilities.GmlcovNamespace);
        yield return new XAttribute(XNamespace.Xmlns + "swe", Wcs20Utilities.SweNamespace);
        yield return new XAttribute(XNamespace.Xmlns + "xlink", Wcs20Utilities.XLinkNamespace);
        yield return new XAttribute(XNamespace.Xmlns + "xsi", Wcs20Utilities.XsiNamespace);
    }

    private static string GetCurrentEndpointUrl(HttpContext context)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var path = context.Request.Path.HasValue ? context.Request.Path.Value : string.Empty;
        return string.Concat(baseUrl, path);
    }

    private static string CreateEpsgUri(int srid)
        => FormattableString.Invariant($"http://www.opengis.net/def/crs/EPSG/0/{srid}");

    private static string FormatPosition(double x, double y)
        => string.Concat(FormatDouble(x), " ", FormatDouble(y));

    private static string FormatDouble(double value)
        => XmlConvert.ToString(value);

    private static string[] SplitCsv(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> SplitCsvValues(IEnumerable<string?> values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var token in SplitCsv(value))
            {
                yield return token;
            }
        }
    }

    private static string? GetQueryValue(IQueryCollection query, string name)
    {
        var values = GetQueryValues(query, name);
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static StringValues GetQueryValues(IQueryCollection query, string name)
    {
        foreach (var pair in query)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return StringValues.Empty;
    }

    private static IResult Xml(XDocument document)
        => Results.Content(
            document.ToString(SaveOptions.DisableFormatting),
            Wcs20Utilities.XmlContentType,
            Encoding.UTF8);

    private readonly record struct CoverageListResult(IReadOnlyList<WcsCoverage> Coverages, IResult? Error);

    private readonly record struct CoverageResolutionResult(WcsCoverage? Coverage, IResult? Error);

    private readonly record struct LayerCoverageResult(WcsCoverage? Coverage, IResult? Error);

    private readonly record struct ServiceResolutionResult(MetadataV2Service? Service, IResult? Error);

    private readonly record struct WcsCoverage(MetadataV2Resource Resource, int LayerId, RasterInfo Raster, MetadataV2Service? Service);

    private readonly record struct WcsCoverageIdentifier(string Raw, int? LayerId);

    private readonly record struct WcsParameterError(string ExceptionCode, string Detail, string Locator);
}
