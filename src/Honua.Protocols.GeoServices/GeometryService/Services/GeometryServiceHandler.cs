// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.GeoServices.GeometryService.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Protocols.GeoServices.GeometryService.Services;

/// <summary>
/// Orchestrates geometry service operations: parse input, invoke PostGIS, format output.
/// </summary>
internal sealed class GeometryServiceHandler(
    IGeometryOperationService operationService,
    IGeometryConverter geometryConverter,
    SpatialReferenceResolver spatialReferenceResolver,
    ILogger<GeometryServiceHandler> logger)
{
    private readonly record struct MeasurementSpatialContext(bool IsGeographic, double MetersPerNativeUnit);

    private const int MaxGeometriesPerRequestUpperBound = 1000;
    private const int MaxGeometryJsonLengthUpperBound = 10_000_000;
    private const double MeanEarthRadiusMeters = 6_371_000d;
    private const string InvalidGeometryInputMessage = "Invalid geometry input.";
    private const string InvalidSpatialReferenceMessage =
        "must be a valid spatial reference WKID, EPSG code, CRS URI, WKT string, or spatial reference object.";

    private readonly IGeometryOperationService _operationService = operationService
        ?? throw new ArgumentNullException(nameof(operationService));
    private readonly IGeometryConverter _geometryConverter = geometryConverter
        ?? throw new ArgumentNullException(nameof(geometryConverter));
    private readonly SpatialReferenceResolver _spatialReferenceResolver = spatialReferenceResolver
        ?? throw new ArgumentNullException(nameof(spatialReferenceResolver));
    private readonly ILogger<GeometryServiceHandler> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));
    private static readonly Dictionary<string, double> _areaUnitDivisors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esriSquareInches"] = 0.00064516,
        ["esriSquareMillimeters"] = 0.000001,
        ["esriSquareCentimeters"] = 0.0001,
        ["esriSquareDecimeters"] = 0.01,
        ["esriSquareMeters"] = 1.0,
        ["esriSquareKilometers"] = 1_000_000.0,
        ["esriSquareFeet"] = 0.09290304,
        ["esriSquareYards"] = 0.83612736,
        ["esriSquareMiles"] = 2_589_988.110336,
        ["esriAres"] = 100.0,
        ["esriHectares"] = 10_000.0,
        ["esriAcres"] = 4_046.8564224
    };

    public async Task<IResult> HandleBufferAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "buffer", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            // Parse geometries
            var (geomStrings, geomType, geomError) = GeometryServiceRequestParser.ParseGeometries(
                GeometryServiceRequestParser.GetValue(values, "geometries"),
                requestLimits.MaxGeometriesPerRequest,
                requestLimits.MaxGeometryJsonLength);
            if (geomError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", geomError);
                return CreateError(context, 400, geomError);
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", "No geometries provided");
                return CreateError(context, 400, "Parameter 'geometries' must contain at least one geometry.");
            }

            // Parse spatial references
            var (inSr, inSrError) = await ResolveRequiredSpatialReferenceAsync(values, "inSR", ct);
            if (inSrError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", inSrError);
                return CreateError(context, 400, inSrError);
            }

            var (outSr, outSrError) = await ResolveOptionalSpatialReferenceAsync(values, "outSR", ct);
            if (outSrError is not null)
            {
                return CreateError(context, 400, outSrError);
            }

            var (bufferSr, bufferSrError) = await ResolveOptionalSpatialReferenceAsync(values, "bufferSR", ct);
            if (bufferSrError is not null)
            {
                return CreateError(context, 400, bufferSrError);
            }

            // Parse distances
            var (distances, distError) = GeometryServiceRequestParser.ParseDoubleArray(
                GeometryServiceRequestParser.GetValue(values, "distances"),
                requestLimits.MaxGeometriesPerRequest);
            if (distError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", distError);
                return CreateError(context, 400, distError);
            }

            if (distances.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", "No distances provided");
                return CreateError(context, 400, "Parameter 'distances' is required and must contain at least one value.");
            }

            var unit = GeometryServiceRequestParser.GetValue(values, "unit");
            var unionResults = GeometryServiceRequestParser.ParseBool(
                GeometryServiceRequestParser.GetValue(values, "unionResults"));
            var geodesic = GeometryServiceRequestParser.ParseBool(
                GeometryServiceRequestParser.GetValue(values, "geodesic"));
            var distanceUnitToMetersFactor = await ResolveDistanceUnitToMetersFactorAsync(
                context.RequestServices,
                bufferSr ?? outSr ?? inSr!.Value,
                unit,
                ct).ConfigureAwait(false);

            var parameters = new BufferParameters
            {
                GeometryJsonStrings = geomStrings,
                GeometryType = geomType,
                InSR = inSr!.Value,
                OutSR = outSr.HasValue && outSr.Value > 0 ? outSr.Value : null,
                BufferSR = bufferSr.HasValue && bufferSr.Value > 0 ? bufferSr.Value : null,
                Distances = distances,
                Unit = unit,
                DistanceUnitToMetersFactor = distanceUnitToMetersFactor,
                UnionResults = unionResults,
                Geodesic = geodesic
            };

            // Geodesic buffers always execute in geographic space (effectively EPSG:4326);
            // a client-supplied bufferSR would be silently ignored, so reject the conflict
            // up front rather than producing surprising output.
            if (parameters.Geodesic
                && parameters.BufferSR.HasValue
                && parameters.BufferSR.Value != parameters.InSR
                && parameters.BufferSR.Value != 4326)
            {
                const string conflictMessage =
                    "Parameter 'bufferSR' is not compatible with 'geodesic=true'. "
                    + "Geodesic buffers execute in geographic coordinates; omit 'bufferSR' or set geodesic=false.";
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", conflictMessage);
                return CreateError(context, 400, conflictMessage);
            }

            GeometryServiceLog.RequestParsed(_logger, "buffer", parameters.GeometryJsonStrings.Length, parameters.GeometryType);

            return await ExecuteBufferAsync(parameters, scope, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "buffer", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the buffer operation.");
        }
    }

    public async Task<IResult> HandleSimplifyAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "simplify", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            // Parse geometries
            var (geomStrings, geomType, geomError) = GeometryServiceRequestParser.ParseGeometries(
                GeometryServiceRequestParser.GetValue(values, "geometries"),
                requestLimits.MaxGeometriesPerRequest,
                requestLimits.MaxGeometryJsonLength);
            if (geomError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", geomError);
                return CreateError(context, 400, geomError);
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", "No geometries provided");
                return CreateError(context, 400, "Parameter 'geometries' must contain at least one geometry.");
            }

            // ArcGIS simplify uses "sr" not "inSR"
            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", srError);
                return CreateError(context, 400, srError);
            }

            var parameters = new SimplifyParameters
            {
                GeometryJsonStrings = geomStrings,
                GeometryType = geomType,
                SR = sr!.Value
            };

            GeometryServiceLog.RequestParsed(_logger, "simplify", parameters.GeometryJsonStrings.Length, parameters.GeometryType);

            return await ExecuteSimplifyAsync(parameters, scope, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "simplify", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the simplify operation.");
        }
    }

    public async Task<IResult> HandleProjectAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "project", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "project", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            // Parse geometries
            var (geomStrings, geomType, geomError) = GeometryServiceRequestParser.ParseGeometries(
                GeometryServiceRequestParser.GetValue(values, "geometries"),
                requestLimits.MaxGeometriesPerRequest,
                requestLimits.MaxGeometryJsonLength);
            if (geomError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "project", geomError);
                return CreateError(context, 400, geomError);
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "project", "No geometries provided");
                return CreateError(context, 400, "Parameter 'geometries' must contain at least one geometry.");
            }

            // Parse spatial references
            var (inSr, inSrError) = await ResolveRequiredSpatialReferenceAsync(values, "inSR", ct);
            if (inSrError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "project", inSrError);
                return CreateError(context, 400, inSrError);
            }

            var (outSr, outSrError) = await ResolveRequiredSpatialReferenceAsync(values, "outSR", ct);
            if (outSrError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "project", outSrError);
                return CreateError(context, 400, outSrError);
            }

            var parameters = new ProjectParameters
            {
                GeometryJsonStrings = geomStrings,
                GeometryType = geomType,
                InSR = inSr!.Value,
                OutSR = outSr!.Value
            };

            GeometryServiceLog.RequestParsed(_logger, "project", parameters.GeometryJsonStrings.Length, parameters.GeometryType);

            return await ExecuteProjectAsync(parameters, scope, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "project", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "project", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the project operation.");
        }
    }

    public async Task<IResult> HandleUnionAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "union", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "union", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            var (geomStrings, geomType, geomError) = GeometryServiceRequestParser.ParseGeometries(
                GeometryServiceRequestParser.GetValue(values, "geometries"),
                requestLimits.MaxGeometriesPerRequest,
                requestLimits.MaxGeometryJsonLength);
            if (geomError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "union", geomError);
                return CreateError(context, 400, geomError);
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "union", "No geometries provided");
                return CreateError(context, 400, "Parameter 'geometries' must contain at least one geometry.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "union", srError);
                return CreateError(context, 400, srError);
            }

            var parameters = new UnionParameters
            {
                GeometryJsonStrings = geomStrings,
                GeometryType = geomType,
                SR = sr!.Value
            };

            GeometryServiceLog.RequestParsed(_logger, "union", parameters.GeometryJsonStrings.Length, parameters.GeometryType);
            return await ExecuteUnionAsync(parameters, scope, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "union", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "union", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the union operation.");
        }
    }

    public async Task<IResult> HandleIntersectAsync(HttpContext context, CancellationToken ct)
    {
        return await HandleBinaryGeometryOperationAsync(
            context,
            operationName: "intersect",
            operation: (target, other, srid, token) => _operationService.IntersectAsync(target, other, srid, token),
            ct);
    }

    public async Task<IResult> HandleClipAsync(HttpContext context, CancellationToken ct)
    {
        return await HandleBinaryGeometryOperationAsync(
            context,
            operationName: "clip",
            operation: (target, other, srid, token) => _operationService.ClipAsync(target, other, srid, token),
            ct);
    }

    public async Task<IResult> HandleDifferenceAsync(HttpContext context, CancellationToken ct)
    {
        return await HandleBinaryGeometryOperationAsync(
            context,
            operationName: "difference",
            operation: (target, other, srid, token) => _operationService.DifferenceAsync(target, other, srid, token),
            ct);
    }

    public async Task<IResult> HandleAreaAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "area", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "area", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            var polygons = GetPreferredValue(values, "polygons", "geometries");
            if (string.IsNullOrWhiteSpace(polygons))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "area", "Missing polygons");
                return CreateError(context, 400, "Parameter 'polygons' is required.");
            }

            var (geomStrings, _, geomError) = GeometryServiceRequestParser.ParseGeometries(
                polygons,
                requestLimits.MaxGeometriesPerRequest,
                requestLimits.MaxGeometryJsonLength);
            if (geomError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "area", geomError);
                return CreateError(context, 400, geomError.Replace("'geometries'", "'polygons'", StringComparison.Ordinal));
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "area", "No geometries provided");
                return CreateError(context, 400, "Parameter 'polygons' must contain at least one geometry.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "area", srError);
                return CreateError(context, 400, srError);
            }

            var (calculationType, calculationError) = ResolveMeasurementCalculationType(values);
            if (calculationError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "area", calculationError);
                return CreateError(context, 400, calculationError);
            }

            var parameters = new MeasurementParameters
            {
                GeometryJsonStrings = geomStrings,
                SR = sr!.Value,
                AreaUnit = GeometryServiceRequestParser.GetValue(values, "areaUnit"),
                LengthUnit = GeometryServiceRequestParser.GetValue(values, "lengthUnit"),
                CalculationType = calculationType
            };

            var spatialContext = await ResolveMeasurementSpatialContextAsync(
                context.RequestServices,
                parameters.SR,
                ct).ConfigureAwait(false);

            return await ExecuteAreasAndLengthsAsync(parameters, spatialContext, scope, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "area", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "area", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the area operation.");
        }
    }

    public async Task<IResult> HandleLengthAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "length", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "length", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            var polylines = GetPreferredValue(values, "polylines", "geometries");
            if (string.IsNullOrWhiteSpace(polylines))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "length", "Missing polylines");
                return CreateError(context, 400, "Parameter 'polylines' is required.");
            }

            var (geomStrings, _, geomError) = GeometryServiceRequestParser.ParseGeometries(
                polylines,
                requestLimits.MaxGeometriesPerRequest,
                requestLimits.MaxGeometryJsonLength);
            if (geomError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "length", geomError);
                return CreateError(context, 400, geomError.Replace("'geometries'", "'polylines'", StringComparison.Ordinal));
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "length", "No geometries provided");
                return CreateError(context, 400, "Parameter 'polylines' must contain at least one geometry.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "length", srError);
                return CreateError(context, 400, srError);
            }

            var (calculationType, calculationError) = ResolveMeasurementCalculationType(values);
            if (calculationError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "length", calculationError);
                return CreateError(context, 400, calculationError);
            }

            var parameters = new MeasurementParameters
            {
                GeometryJsonStrings = geomStrings,
                SR = sr!.Value,
                LengthUnit = GeometryServiceRequestParser.GetValue(values, "lengthUnit"),
                CalculationType = calculationType
            };

            var spatialContext = await ResolveMeasurementSpatialContextAsync(
                context.RequestServices,
                parameters.SR,
                ct).ConfigureAwait(false);

            return await ExecuteLengthAsync(parameters, spatialContext, scope, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "length", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "length", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the length operation.");
        }
    }

    private async Task<IResult> ExecuteBufferAsync(BufferParameters parameters, HonuaTelemetryScope scope, CancellationToken ct)
    {
        // bufferSR cascade: buffer in bufferSR ?? outSR ?? inSR, then project to outSR ?? inSR
        var bufferSrid = parameters.BufferSR ?? parameters.OutSR ?? parameters.InSR;
        var outputSrid = parameters.OutSR ?? parameters.InSR;
        var bufferedGeometries = new List<byte[]>();

        foreach (var requestedDistance in parameters.Distances)
        {
            var distance = requestedDistance * parameters.DistanceUnitToMetersFactor;
            var bufferedAtDistance = new List<byte[]>(parameters.GeometryJsonStrings.Length);

            foreach (var geometryJson in parameters.GeometryJsonStrings)
            {
                var wkb = _geometryConverter.ConvertGeoServicesJsonToWkb(geometryJson);

                // Project to bufferSR if needed before buffering (non-geodesic only)
                if (!parameters.Geodesic && bufferSrid != parameters.InSR)
                {
                    wkb = await _operationService.ProjectAsync(wkb, parameters.InSR, bufferSrid, ct).ConfigureAwait(false);
                }

                var result = await _operationService.BufferAsync(
                    wkb,
                    parameters.Geodesic ? parameters.InSR : bufferSrid,
                    distance,
                    parameters.Geodesic,
                    ct).ConfigureAwait(false);

                // Project to output SR. For geodesic, the result is in SRID 4326.
                var resultSrid = parameters.Geodesic ? 4326 : bufferSrid;
                if (resultSrid != outputSrid)
                {
                    result = await _operationService.ProjectAsync(result, resultSrid, outputSrid, ct).ConfigureAwait(false);
                }

                bufferedAtDistance.Add(result);
            }

            if (parameters.UnionResults)
            {
                var unionResult = bufferedAtDistance.Count == 1
                    ? bufferedAtDistance[0]
                    : await _operationService.UnionAsync(
                        bufferedAtDistance.ToArray(),
                        outputSrid,
                        ct).ConfigureAwait(false);
                bufferedGeometries.Add(unionResult);
                continue;
            }

            bufferedGeometries.AddRange(bufferedAtDistance);
        }

        var response = ConvertToResponse(bufferedGeometries, outputSrid, "esriGeometryPolygon");
        GeometryServiceLog.BufferOperationCompleted(
            _logger, parameters.GeometryJsonStrings.Length, parameters.Distances[0], parameters.Geodesic);
        scope.SetSuccess(bufferedGeometries.Count);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceResponse, contentType: "application/json");
    }

    private async Task<IResult> ExecuteSimplifyAsync(SimplifyParameters parameters, HonuaTelemetryScope scope, CancellationToken ct)
    {
        var simplifiedGeometries = new List<byte[]>();

        foreach (var geomJson in parameters.GeometryJsonStrings)
        {
            var wkb = _geometryConverter.ConvertGeoServicesJsonToWkb(geomJson);
            var result = await _operationService.MakeValidAsync(wkb, parameters.SR, ct).ConfigureAwait(false);
            simplifiedGeometries.Add(result);
        }

        var response = ConvertToResponse(simplifiedGeometries, parameters.SR, parameters.GeometryType);
        GeometryServiceLog.SimplifyOperationCompleted(_logger, parameters.GeometryJsonStrings.Length);
        scope.SetSuccess(simplifiedGeometries.Count);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceResponse, contentType: "application/json");
    }

    private async Task<IResult> ExecuteProjectAsync(ProjectParameters parameters, HonuaTelemetryScope scope, CancellationToken ct)
    {
        var projectedGeometries = new List<byte[]>();

        foreach (var geomJson in parameters.GeometryJsonStrings)
        {
            var wkb = _geometryConverter.ConvertGeoServicesJsonToWkb(geomJson);
            var result = await _operationService.ProjectAsync(wkb, parameters.InSR, parameters.OutSR, ct).ConfigureAwait(false);
            projectedGeometries.Add(result);
        }

        var response = ConvertToResponse(projectedGeometries, parameters.OutSR, parameters.GeometryType);
        GeometryServiceLog.ProjectOperationCompleted(_logger, parameters.GeometryJsonStrings.Length, parameters.InSR, parameters.OutSR);
        scope.SetSuccess(projectedGeometries.Count);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceResponse, contentType: "application/json");
    }

    private async Task<IResult> ExecuteUnionAsync(UnionParameters parameters, HonuaTelemetryScope scope, CancellationToken ct)
    {
        var wkbs = new byte[parameters.GeometryJsonStrings.Length][];
        for (var i = 0; i < parameters.GeometryJsonStrings.Length; i++)
        {
            wkbs[i] = _geometryConverter.ConvertGeoServicesJsonToWkb(parameters.GeometryJsonStrings[i]);
        }

        var unionResult = await _operationService.UnionAsync(wkbs, parameters.SR, ct).ConfigureAwait(false);
        var response = ConvertToResponse(new List<byte[]> { unionResult }, parameters.SR, parameters.GeometryType);
        GeometryServiceLog.UnionOperationCompleted(_logger, parameters.GeometryJsonStrings.Length);
        scope.SetSuccess(1);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceResponse, contentType: "application/json");
    }

    private async Task<IResult> HandleBinaryGeometryOperationAsync(
        HttpContext context,
        string operationName,
        Func<byte[], byte[], int, CancellationToken, Task<byte[]>> operation,
        CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            operationName, HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, operationName, parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            var (geomStrings, geomType, geomError) = GeometryServiceRequestParser.ParseGeometries(
                GeometryServiceRequestParser.GetValue(values, "geometries"),
                requestLimits.MaxGeometriesPerRequest,
                requestLimits.MaxGeometryJsonLength);
            if (geomError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, operationName, geomError);
                return CreateError(context, 400, geomError);
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, operationName, "No geometries provided");
                return CreateError(context, 400, "Parameter 'geometries' must contain at least one geometry.");
            }

            var (operatorGeometry, operatorError) = GeometryServiceRequestParser.ParseSingleGeometry(
                GeometryServiceRequestParser.GetValue(values, "geometry"),
                maxGeometryJsonLength: requestLimits.MaxGeometryJsonLength);
            if (operatorError is not null || string.IsNullOrWhiteSpace(operatorGeometry))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, operationName, operatorError ?? "Missing geometry");
                return CreateError(context, 400, operatorError ?? "Parameter 'geometry' is required.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, operationName, srError);
                return CreateError(context, 400, srError);
            }

            var parameters = new BinaryGeometryOperationParameters
            {
                GeometryJsonStrings = geomStrings,
                GeometryType = geomType,
                OperatorGeometryJson = operatorGeometry,
                SR = sr!.Value
            };

            GeometryServiceLog.RequestParsed(_logger, operationName, parameters.GeometryJsonStrings.Length, parameters.GeometryType);
            return await ExecuteBinaryOperationAsync(parameters, operationName, operation, scope, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, operationName, ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, operationName, ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, $"An internal error occurred during the {operationName} operation.");
        }
    }

    private async Task<IResult> ExecuteBinaryOperationAsync(
        BinaryGeometryOperationParameters parameters,
        string operationName,
        Func<byte[], byte[], int, CancellationToken, Task<byte[]>> operation,
        HonuaTelemetryScope scope,
        CancellationToken ct)
    {
        var operatorWkb = _geometryConverter.ConvertGeoServicesJsonToWkb(parameters.OperatorGeometryJson);
        var results = new List<byte[]>(parameters.GeometryJsonStrings.Length);

        foreach (var geomJson in parameters.GeometryJsonStrings)
        {
            var targetWkb = _geometryConverter.ConvertGeoServicesJsonToWkb(geomJson);
            var result = await operation(targetWkb, operatorWkb, parameters.SR, ct).ConfigureAwait(false);
            results.Add(result);
        }

        var response = ConvertToResponse(results, parameters.SR, parameters.GeometryType);
        GeometryServiceLog.BinaryOperationCompleted(_logger, operationName, parameters.GeometryJsonStrings.Length);
        scope.SetSuccess(results.Count);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceResponse, contentType: "application/json");
    }

    private async Task<IResult> ExecuteAreasAndLengthsAsync(
        MeasurementParameters parameters,
        MeasurementSpatialContext spatialContext,
        HonuaTelemetryScope scope,
        CancellationToken ct)
    {
        var areas = new double[parameters.GeometryJsonStrings.Length];
        var lengths = new double[parameters.GeometryJsonStrings.Length];

        for (var i = 0; i < parameters.GeometryJsonStrings.Length; i++)
        {
            var wkb = _geometryConverter.ConvertGeoServicesJsonToWkb(parameters.GeometryJsonStrings[i]);
            var geometry = new WKBReader().Read(wkb);

            if (parameters.CalculationType == MeasurementCalculationType.Planar)
            {
                lengths[i] = ConvertPlanarLengthFromNativeUnits(
                    geometry.Length,
                    spatialContext,
                    parameters.LengthUnit);
                areas[i] = ConvertPlanarAreaFromNativeUnits(
                    ApplyPlanarAreaOrientation(geometry.Area, parameters.GeometryJsonStrings[i]),
                    spatialContext,
                    parameters.AreaUnit);
                continue;
            }

            var measurementGeometry = await ProjectForGeodeticMeasurementAsync(wkb, parameters.SR, ct).ConfigureAwait(false);
            var measurementBoundary = new WKBWriter().Write(new WKBReader().Read(measurementGeometry).Boundary);
            var area = await _operationService.AreaAsync(measurementGeometry, 4326, ct).ConfigureAwait(false);
            var perimeter = await _operationService.LengthAsync(
                measurementBoundary,
                4326,
                ct).ConfigureAwait(false);

            areas[i] = ConvertAreaFromMetersSquared(area, parameters.AreaUnit);
            lengths[i] = ConvertLengthFromMeters(perimeter, parameters.LengthUnit);
        }

        var response = new GeometryServiceAreasAndLengthsResponse { Areas = areas, Lengths = lengths };
        GeometryServiceLog.MeasurementOperationCompleted(_logger, "area", areas.Length);
        scope.SetSuccess(areas.Length);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceAreasAndLengthsResponse, contentType: "application/json");
    }

    private async Task<IResult> ExecuteLengthAsync(
        MeasurementParameters parameters,
        MeasurementSpatialContext spatialContext,
        HonuaTelemetryScope scope,
        CancellationToken ct)
    {
        var values = new double[parameters.GeometryJsonStrings.Length];

        for (var i = 0; i < parameters.GeometryJsonStrings.Length; i++)
        {
            var wkb = _geometryConverter.ConvertGeoServicesJsonToWkb(parameters.GeometryJsonStrings[i]);
            if (parameters.CalculationType == MeasurementCalculationType.Planar)
            {
                var geometry = new WKBReader().Read(wkb);
                values[i] = ConvertPlanarLengthFromNativeUnits(
                    geometry.Length,
                    spatialContext,
                    parameters.LengthUnit);
                continue;
            }

            var measurementGeometry = await ProjectForGeodeticMeasurementAsync(wkb, parameters.SR, ct).ConfigureAwait(false);
            var length = await _operationService.LengthAsync(measurementGeometry, 4326, ct).ConfigureAwait(false);
            values[i] = ConvertLengthFromMeters(length, parameters.LengthUnit);
        }

        var response = new GeometryServiceLengthResponse { Lengths = values };
        GeometryServiceLog.MeasurementOperationCompleted(_logger, "length", values.Length);
        scope.SetSuccess(values.Length);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceLengthResponse, contentType: "application/json");
    }

    private static async Task<MeasurementSpatialContext> ResolveMeasurementSpatialContextAsync(
        IServiceProvider serviceProvider,
        int srid,
        CancellationToken ct)
    {
        var crsRegistry = serviceProvider.GetService<ICrsRegistry>();
        var crsDefinition = crsRegistry is null
            ? null
            : await crsRegistry.ResolveBySridAsync(srid, ct).ConfigureAwait(false);
        var isGeographic = crsDefinition?.IsGeographic ?? SpatialReference.Create(srid).IsGeographic;
        var metersPerNativeUnit = ResolveMetersPerNativeUnit(crsDefinition?.Wkt, isGeographic);
        return new MeasurementSpatialContext(isGeographic, metersPerNativeUnit);
    }

    private static async Task<double> ResolveDistanceUnitToMetersFactorAsync(
        IServiceProvider serviceProvider,
        int srid,
        string? unit,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(unit))
        {
            return GeometryServiceRequestParser.GetUnitMultiplier(unit);
        }

        var spatialContext = await ResolveMeasurementSpatialContextAsync(serviceProvider, srid, ct).ConfigureAwait(false);
        return spatialContext.MetersPerNativeUnit;
    }

    private static (int MaxGeometriesPerRequest, int MaxGeometryJsonLength) ResolveRequestLimits(HttpContext context)
    {
        var limitsOptions = context.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<LimitsOptions>>();

        return (
            MaxGeometriesPerRequest: Math.Clamp(
                limitsOptions?.Value.Query.MaxRecordCount ?? MaxGeometriesPerRequestUpperBound,
                1,
                MaxGeometriesPerRequestUpperBound),
            MaxGeometryJsonLength: (int)Math.Clamp(
                limitsOptions?.Value.Geometry.MaxGeometrySize ?? MaxGeometryJsonLengthUpperBound,
                1024L,
                MaxGeometryJsonLengthUpperBound));
    }

    private async Task<(int? Srid, string? Error)> ResolveSpatialReferenceAsync(
        IReadOnlyDictionary<string, StringValues> values,
        string parameterName,
        bool required,
        CancellationToken ct)
    {
        var raw = GeometryServiceRequestParser.GetValue(values, parameterName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return required
                ? (null, $"Parameter '{parameterName}' must be a valid spatial reference WKID, EPSG code, CRS URI, WKT string, or spatial reference object.")
                : (null, null);
        }

        var srid = await _spatialReferenceResolver.ResolveSridAsync(raw, null, ct).ConfigureAwait(false);
        if (!srid.HasValue || srid.Value <= 0)
        {
            return (null, $"Parameter '{parameterName}' must be a valid spatial reference WKID, EPSG code, CRS URI, WKT string, or spatial reference object.");
        }

        return (srid, null);
    }

    private Task<(int? Srid, string? Error)> ResolveRequiredSpatialReferenceAsync(
        IReadOnlyDictionary<string, StringValues> values,
        string parameterName,
        CancellationToken ct)
        => ResolveSpatialReferenceAsync(values, parameterName, required: true, ct);

    private Task<(int? Srid, string? Error)> ResolveOptionalSpatialReferenceAsync(
        IReadOnlyDictionary<string, StringValues> values,
        string parameterName,
        CancellationToken ct)
        => ResolveSpatialReferenceAsync(values, parameterName, required: false, ct);

    private GeometryServiceResponse ConvertToResponse(List<byte[]> wkbs, int srid, string? geometryType)
    {
        var geometryElements = new JsonElement[wkbs.Count];

        for (var i = 0; i < wkbs.Count; i++)
        {
            geometryElements[i] = _geometryConverter.ConvertWkbToGeoServicesGeometry(wkbs[i], srid);
        }

        return new GeometryServiceResponse
        {
            GeometryType = geometryType,
            SpatialReference = new GeometryServiceSpatialReference { Wkid = srid, LatestWkid = srid },
            Geometries = geometryElements
        };
    }

    private static double ResolveMetersPerNativeUnit(string? wkt, bool isGeographic)
    {
        var unitMatches = System.Text.RegularExpressions.Regex.Matches(
            wkt ?? string.Empty,
            @"(?:UNIT|LENGTHUNIT)\s*\[[^,\]]+,\s*([0-9.eE+-]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (unitMatches.Count > 0)
        {
            var matchIndex = isGeographic ? 0 : unitMatches.Count - 1;
            if (double.TryParse(
                unitMatches[matchIndex].Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var unitFactor)
                && unitFactor > 0)
            {
                return isGeographic
                    ? MeanEarthRadiusMeters * unitFactor
                    : unitFactor;
            }
        }

        return isGeographic ? MeanEarthRadiusMeters * (Math.PI / 180d) : 1.0;
    }

    private static (MeasurementCalculationType CalculationType, string? Error) ResolveMeasurementCalculationType(
        IReadOnlyDictionary<string, StringValues> values)
    {
        var rawCalculationType = GeometryServiceRequestParser.GetValue(values, "calculationType");
        if (!string.IsNullOrWhiteSpace(rawCalculationType))
        {
            if (string.Equals(rawCalculationType, "planar", StringComparison.OrdinalIgnoreCase))
            {
                return (MeasurementCalculationType.Planar, null);
            }

            if (string.Equals(rawCalculationType, "geodesic", StringComparison.OrdinalIgnoreCase))
            {
                return (MeasurementCalculationType.Geodesic, null);
            }

            if (string.Equals(rawCalculationType, "preserveShape", StringComparison.OrdinalIgnoreCase))
            {
                return (MeasurementCalculationType.PreserveShape, null);
            }

            return (MeasurementCalculationType.Planar, "Parameter 'calculationType' must be one of: planar, geodesic, preserveShape.");
        }

        return GeometryServiceRequestParser.ParseBool(GeometryServiceRequestParser.GetValue(values, "geodesic"))
            ? (MeasurementCalculationType.Geodesic, null)
            : (MeasurementCalculationType.Planar, null);
    }

    private static string? GetPreferredValue(IReadOnlyDictionary<string, StringValues> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = GeometryServiceRequestParser.GetValue(values, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private async Task<byte[]> ProjectForGeodeticMeasurementAsync(byte[] wkb, int srid, CancellationToken ct)
    {
        return srid == 4326
            ? wkb
            : await _operationService.ProjectAsync(wkb, srid, 4326, ct).ConfigureAwait(false);
    }

    private static double ConvertPlanarLengthFromNativeUnits(
        double nativeLength,
        MeasurementSpatialContext spatialContext,
        string? requestedUnit)
    {
        if (string.IsNullOrWhiteSpace(requestedUnit))
        {
            return nativeLength;
        }

        return ConvertLengthFromMeters(nativeLength * spatialContext.MetersPerNativeUnit, requestedUnit);
    }

    private static double ConvertPlanarAreaFromNativeUnits(
        double nativeArea,
        MeasurementSpatialContext spatialContext,
        string? requestedUnit)
    {
        if (string.IsNullOrWhiteSpace(requestedUnit))
        {
            return nativeArea;
        }

        return ConvertAreaFromMetersSquared(
            nativeArea * spatialContext.MetersPerNativeUnit * spatialContext.MetersPerNativeUnit,
            requestedUnit);
    }

    private static double ConvertLengthFromMeters(double meters, string? requestedUnit)
    {
        var divisor = GeometryServiceRequestParser.GetUnitMultiplier(requestedUnit);
        return meters / divisor;
    }

    private static double ConvertAreaFromMetersSquared(double squareMeters, string? requestedUnit)
    {
        var divisor = GetAreaUnitDivisor(requestedUnit);
        return squareMeters / divisor;
    }

    private static double GetAreaUnitDivisor(string? unit)
    {
        var normalized = NormalizeAreaUnit(unit);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return 1.0;
        }

        if (_areaUnitDivisors.TryGetValue(normalized, out var divisor))
        {
            return divisor;
        }

        var linearUnitMultiplier = GeometryServiceRequestParser.GetUnitMultiplier(normalized);
        return linearUnitMultiplier * linearUnitMultiplier;
    }

    private static string? NormalizeAreaUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            return null;
        }

        var trimmed = unit.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (!document.RootElement.TryGetProperty("areaUnit", out var areaUnitElement))
            {
                return trimmed;
            }

            return areaUnitElement.ValueKind switch
            {
                JsonValueKind.String => areaUnitElement.GetString(),
                JsonValueKind.Number => areaUnitElement.ToString(),
                _ => trimmed
            };
        }
        catch (JsonException)
        {
            return trimmed;
        }
    }

    private static double ApplyPlanarAreaOrientation(double areaMagnitude, string geometryJson)
    {
        var signedArea = ComputeShoelaceSignedArea(geometryJson);
        return Math.Sign(signedArea) switch
        {
            0 => areaMagnitude,
            _ => areaMagnitude * -Math.Sign(signedArea)
        };
    }

    private static double ComputeShoelaceSignedArea(string geometryJson)
    {
        using var document = JsonDocument.Parse(geometryJson);
        if (!document.RootElement.TryGetProperty("rings", out var ringsElement) ||
            ringsElement.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        double total = 0;
        foreach (var ringElement in ringsElement.EnumerateArray())
        {
            if (ringElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var coordinates = ringElement.EnumerateArray()
                .Where(point => point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2)
                .Select(point => (X: point[0].GetDouble(), Y: point[1].GetDouble()))
                .ToArray();
            if (coordinates.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < coordinates.Length; i++)
            {
                var current = coordinates[i];
                var next = coordinates[(i + 1) % coordinates.Length];
                total += (current.X * next.Y) - (next.X * current.Y);
            }
        }

        return total / 2d;
    }

    private static IResult CreateError(HttpContext context, int code, string message)
    {
        var errorResponse = code switch
        {
            StatusCodes.Status400BadRequest => StandardErrorResponse.BadRequest(message),
            StatusCodes.Status404NotFound => StandardErrorResponse.NotFound(message),
            StatusCodes.Status415UnsupportedMediaType => new StandardErrorResponse(
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported Media Type",
                message),
            StatusCodes.Status409Conflict => StandardErrorResponse.Conflict(message),
            StatusCodes.Status503ServiceUnavailable => StandardErrorResponse.ServiceUnavailable(message),
            _ => StandardErrorResponse.InternalServerError(message)
        };

        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    private static IResult CreateRequestParameterError(HttpContext context, string? parseError)
    {
        if (GeometryServiceRequestParser.TryGetUnsupportedMediaType(parseError, out var receivedContentType))
        {
            return CreateError(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                $"Unsupported Content-Type '{receivedContentType ?? "(missing)"}'. Use application/json or application/x-www-form-urlencoded.");
        }

        return CreateError(context, StatusCodes.Status400BadRequest, parseError ?? "Request parameters are required.");
    }
}
