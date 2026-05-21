// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Protocols.Ogc.Shared;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Primitives;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wcs20;

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
    };

    private static readonly HashSet<string> _explicitlyUnsupportedGetCoverageParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "RANGESUBSET",
        "SCALEFACTOR",
        "SCALEAXES",
        "SCALESIZE",
        "SCALEEXTENT",
        "SIZE",
        "WIDTH",
        "HEIGHT",
        "RESOLUTION",
        "INTERPOLATION",
        "MEDIATYPE",
        "DATETIME",
        "TIME",
    };

    private readonly ILayerCatalog _layerCatalog;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<Wcs20Handler> _logger;

    public Wcs20Handler(
        ILayerCatalog layerCatalog,
        IRasterStore rasterStore,
        ILogger<Wcs20Handler> logger)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
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

        telemetry
            .WithTag(HonuaTelemetry.Tags.LayerId, coverage.Coverage.Value.Layer.Id)
            .WithTag("honua.coverage.id", coverageId.Raw)
            .WithTag("honua.output.format", formatContentType);

        var result = await _rasterStore.ExportImageAsync(
            coverage.Coverage.Value.Layer.Id,
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
        if (scope.IsLayerScoped)
        {
            var coverage = await ResolveLayerScopedCoverageAsync(context, scope.LayerId!.Value, failOnAccessDenied: true, cancellationToken)
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

        var service = await ResolveServiceAsync(context, scope, cancellationToken).ConfigureAwait(false);
        if (service.Error is not null)
        {
            return new CoverageListResult([], service.Error);
        }

        var coverages = new List<WcsCoverage>();
        foreach (var layer in service.Service!.Layers.OrderBy(static layer => layer.Id))
        {
            if (!IsLayerWcsEnabled(layer) ||
                !AccessPolicyHelpers.IsLayerAccessible(context, layer, service.Service))
            {
                continue;
            }

            var raster = await GetPrimaryRasterWithExtentAsync(layer.Id, cancellationToken).ConfigureAwait(false);
            if (raster is null)
            {
                continue;
            }

            coverages.Add(new WcsCoverage(layer, raster.Value, service.Service));
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

        var layerId = coverageId.LayerId.Value;
        if (scope.IsLayerScoped)
        {
            if (layerId != scope.LayerId!.Value)
            {
                return new CoverageResolutionResult(null, null);
            }

            var result = await ResolveLayerScopedCoverageAsync(context, layerId, failOnAccessDenied: false, cancellationToken)
                .ConfigureAwait(false);
            return new CoverageResolutionResult(result.Coverage, result.Error);
        }

        var service = await ResolveServiceAsync(context, scope, cancellationToken).ConfigureAwait(false);
        if (service.Error is not null)
        {
            return new CoverageResolutionResult(null, service.Error);
        }

        if (service.Service is null)
        {
            return new CoverageResolutionResult(null, null);
        }

        var layer = service.Service.GetLayer(layerId);
        if (layer is null ||
            !IsLayerWcsEnabled(layer) ||
            !AccessPolicyHelpers.IsLayerAccessible(context, layer, service.Service))
        {
            return new CoverageResolutionResult(null, null);
        }

        var raster = await GetPrimaryRasterWithExtentAsync(layerId, cancellationToken).ConfigureAwait(false);
        return raster is null
            ? new CoverageResolutionResult(null, null)
            : new CoverageResolutionResult(new WcsCoverage(layer, raster.Value, service.Service), null);
    }

    private async Task<LayerCoverageResult> ResolveLayerScopedCoverageAsync(
        HttpContext context,
        int layerId,
        bool failOnAccessDenied,
        CancellationToken cancellationToken)
    {
        var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (layer is null || !IsLayerWcsEnabled(layer))
        {
            return new LayerCoverageResult(null, null);
        }

        var accessDecision = AccessPolicyHelpers.EvaluateAccess(context, layer.Metadata?.AccessPolicy, servicePolicy: null);
        if (!accessDecision.IsAllowed)
        {
            return failOnAccessDenied
                ? new LayerCoverageResult(null, CreateAccessDeniedResult(accessDecision))
                : new LayerCoverageResult(null, null);
        }

        var raster = await GetPrimaryRasterWithExtentAsync(layerId, cancellationToken).ConfigureAwait(false);
        return raster is null
            ? new LayerCoverageResult(null, null)
            : new LayerCoverageResult(new WcsCoverage(layer, raster.Value, null), null);
    }

    private async Task<ServiceResolutionResult> ResolveServiceAsync(
        HttpContext context,
        Wcs20RouteScope scope,
        CancellationToken cancellationToken)
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

        var service = await _layerCatalog.GetServiceAsync(serviceId, cancellationToken).ConfigureAwait(false);
        if (service is null)
        {
            return new ServiceResolutionResult(
                null,
                Wcs20ErrorResults.CreateNotFound(
                    Wcs20Utilities.ExceptionCodes.NoApplicableCode,
                    $"Service '{serviceId}' was not found."));
        }

        if (!ServiceProtocols.IsProtocolEnabled(service.Metadata, ServiceProtocols.Wcs))
        {
            return new ServiceResolutionResult(
                null,
                Wcs20ErrorResults.CreateNotFound(
                    Wcs20Utilities.ExceptionCodes.OperationNotSupported,
                    "WCS is not enabled for this service."));
        }

        var serviceAccess = AccessPolicyHelpers.EvaluateAccess(context, null, service.Metadata?.AccessPolicy);
        if (!serviceAccess.IsAllowed)
        {
            return new ServiceResolutionResult(null, CreateAccessDeniedResult(serviceAccess));
        }

        return new ServiceResolutionResult(service, null);
    }

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

    private static bool IsLayerWcsEnabled(LayerDefinition layer)
        => ServiceProtocols.IsProtocolEnabled(layer.Metadata, ServiceProtocols.Wcs);

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

    private static int? ResolveCoverageDescriptionSrid(WcsCoverage coverage)
    {
        if (coverage.Raster.Srid is { } rasterSrid && rasterSrid > 0)
        {
            return rasterSrid;
        }

        if (coverage.Raster.Extent is { } extent &&
            extent.Srid is { } extentSrid &&
            extentSrid > 0)
        {
            return extentSrid;
        }

        return coverage.Layer.SpatialReference.Wkid > 0
            ? coverage.Layer.SpatialReference.Wkid
            : null;
    }

    private static XElement BuildCoverageSummary(WcsCoverage coverage)
    {
        var children = new List<object>
        {
            new XElement(Ows + "Title", coverage.Layer.Name)
        };

        if (!string.IsNullOrWhiteSpace(coverage.Layer.Description))
        {
            children.Add(new XElement(Ows + "Abstract", coverage.Layer.Description));
        }

        if (TryResolveExtent(coverage.Raster, out var extent) &&
            (extent.Srid ?? coverage.Raster.Srid ?? coverage.Layer.SpatialReference.Wkid) == 4326)
        {
            children.Add(new XElement(Ows + "WGS84BoundingBox",
                new XElement(Ows + "LowerCorner", FormatPosition(extent.XMin, extent.YMin)),
                new XElement(Ows + "UpperCorner", FormatPosition(extent.XMax, extent.YMax))));
        }

        children.Add(new XElement(Wcs + "CoverageId", FormatCoverageId(coverage.Layer.Id)));
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

        var srid = coverage.Raster.Srid ?? extent.Srid ?? coverage.Layer.SpatialReference.Wkid;
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

        var coverageId = FormatCoverageId(coverage.Layer.Id);
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

        var subsetValues = GetQueryValues(query, Wcs20Utilities.Parameters.Subset);
        var bbox = GetQueryValue(query, Wcs20Utilities.Parameters.BBox);
        if (subsetValues.Count > 0 && !string.IsNullOrWhiteSpace(bbox))
        {
            error = new WcsParameterError(
                Wcs20Utilities.ExceptionCodes.InvalidSubsetting,
                "Use either SUBSET or BBOX, not both.",
                Wcs20Utilities.Parameters.Subset);
            return false;
        }

        if (subsetValues.Count > 0)
        {
            if (!TryParseSubsetEnvelope(subsetValues, raster, subsettingCrs, out var envelope, out error))
            {
                return false;
            }

            rasterQuery = rasterQuery with { ClipRegion = CreateClipRegion(envelope, subsettingCrs.Srid) };
        }
        else if (!string.IsNullOrWhiteSpace(bbox))
        {
            if (!TryParseBboxEnvelope(bbox, subsettingCrs, out var envelope, out error))
            {
                return false;
            }

            rasterQuery = rasterQuery with { ClipRegion = CreateClipRegion(envelope, subsettingCrs.Srid) };
        }

        return true;
    }

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

    private readonly record struct ServiceResolutionResult(ServiceDefinition? Service, IResult? Error);

    private readonly record struct WcsCoverage(LayerDefinition Layer, RasterInfo Raster, ServiceDefinition? Service);

    private readonly record struct WcsCoverageIdentifier(string Raw, int? LayerId);

    private readonly record struct WcsParameterError(string ExceptionCode, string Detail, string Locator);
}
