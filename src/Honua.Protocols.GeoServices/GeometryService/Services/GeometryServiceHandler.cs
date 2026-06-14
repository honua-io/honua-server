// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.GeometryService.Models;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Protocols.GeoServices.GeometryService.Services;

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
        // The GeoServices clip operation clips each input geometry to an "envelope"
        // parameter (Esri envelope JSON: {xmin,ymin,xmax,ymax,spatialReference}). This
        // differs from the other binary ops, which take a "geometry" operand. The shared
        // geometry converter already materializes an Esri envelope into a polygon, so we
        // reuse the binary-operation pipeline with the envelope as the clipping operand.
        return await HandleBinaryGeometryOperationAsync(
            context,
            operationName: "clip",
            operation: (target, other, srid, token) => _operationService.ClipAsync(target, other, srid, token),
            ct,
            operatorParameterName: "envelope");
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

    public async Task<IResult> HandleDistanceAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "distance", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "distance", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            var (geometry1, geom1Error) = GeometryServiceRequestParser.ParseSingleGeometry(
                GeometryServiceRequestParser.GetValue(values, "geometry1"),
                "geometry1",
                requestLimits.MaxGeometryJsonLength);
            if (geom1Error is not null || string.IsNullOrWhiteSpace(geometry1))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "distance", geom1Error ?? "Missing geometry1");
                return CreateError(context, 400, geom1Error ?? "Parameter 'geometry1' is required.");
            }

            var (geometry2, geom2Error) = GeometryServiceRequestParser.ParseSingleGeometry(
                GeometryServiceRequestParser.GetValue(values, "geometry2"),
                "geometry2",
                requestLimits.MaxGeometryJsonLength);
            if (geom2Error is not null || string.IsNullOrWhiteSpace(geometry2))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "distance", geom2Error ?? "Missing geometry2");
                return CreateError(context, 400, geom2Error ?? "Parameter 'geometry2' is required.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "distance", srError);
                return CreateError(context, 400, srError);
            }

            var parameters = new DistanceParameters
            {
                Geometry1Json = geometry1,
                Geometry2Json = geometry2,
                SR = sr!.Value,
                DistanceUnit = GeometryServiceRequestParser.GetValue(values, "distanceUnit"),
                Geodesic = GeometryServiceRequestParser.ParseBool(
                    GeometryServiceRequestParser.GetValue(values, "geodesic"))
            };

            var spatialContext = await ResolveMeasurementSpatialContextAsync(
                context.RequestServices,
                parameters.SR,
                ct).ConfigureAwait(false);

            return await ExecuteDistanceAsync(parameters, spatialContext, scope, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "distance", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "distance", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the distance operation.");
        }
    }

    public async Task<IResult> HandleRelationAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "relation", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "relation", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            var (geometries1, _, geom1Error) = GeometryServiceRequestParser.ParseGeometries(
                GeometryServiceRequestParser.GetValue(values, "geometries1"),
                requestLimits.MaxGeometriesPerRequest,
                requestLimits.MaxGeometryJsonLength);
            if (geom1Error is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "relation", geom1Error);
                return CreateError(context, 400, geom1Error.Replace("'geometries'", "'geometries1'", StringComparison.Ordinal));
            }

            var (geometries2, _, geom2Error) = GeometryServiceRequestParser.ParseGeometries(
                GeometryServiceRequestParser.GetValue(values, "geometries2"),
                requestLimits.MaxGeometriesPerRequest,
                requestLimits.MaxGeometryJsonLength);
            if (geom2Error is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "relation", geom2Error);
                return CreateError(context, 400, geom2Error.Replace("'geometries'", "'geometries2'", StringComparison.Ordinal));
            }

            if (geometries1.Length == 0 || geometries2.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "relation", "No geometries provided");
                return CreateError(context, 400, "Parameters 'geometries1' and 'geometries2' must each contain at least one geometry.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "relation", srError);
                return CreateError(context, 400, srError);
            }

            var relation = GeometryServiceRequestParser.GetValue(values, "relation");
            if (string.IsNullOrWhiteSpace(relation))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "relation", "Missing relation");
                return CreateError(context, 400, "Parameter 'relation' is required.");
            }

            var relationParam = GeometryServiceRequestParser.GetValue(values, "relationParam");
            if (RelationRequiresRelationParam(relation) && string.IsNullOrWhiteSpace(relationParam))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "relation", "Missing relationParam");
                return CreateError(
                    context,
                    400,
                    "Parameter 'relationParam' (a DE-9IM pattern) is required when 'relation' is 'esriGeometryRelationRelation'.");
            }

            if (!IsSupportedRelation(relation))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "relation", $"Unsupported relation '{relation}'");
                return CreateError(
                    context,
                    400,
                    $"Parameter 'relation' value '{relation}' is not supported.");
            }

            var parameters = new RelationParameters
            {
                Geometries1Json = geometries1,
                Geometries2Json = geometries2,
                SR = sr!.Value,
                Relation = relation,
                RelationParam = relationParam
            };

            return ExecuteRelation(parameters, scope);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "relation", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "relation", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the relation operation.");
        }
    }

    public async Task<IResult> HandleDensifyAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "densify", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "densify", parseError ?? "No parameters");
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
                GeometryServiceLog.InvalidGeometryInput(_logger, "densify", geomError);
                return CreateError(context, 400, geomError);
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "densify", "No geometries provided");
                return CreateError(context, 400, "Parameter 'geometries' must contain at least one geometry.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "densify", srError);
                return CreateError(context, 400, srError);
            }

            var (maxSegmentLength, segmentError) = ParseRequiredPositiveDouble(
                GeometryServiceRequestParser.GetValue(values, "maxSegmentLength"),
                "maxSegmentLength");
            if (segmentError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "densify", segmentError);
                return CreateError(context, 400, segmentError);
            }

            var parameters = new DensifyParameters
            {
                GeometryJsonStrings = geomStrings,
                GeometryType = geomType,
                SR = sr!.Value,
                MaxSegmentLength = maxSegmentLength
            };

            GeometryServiceLog.RequestParsed(_logger, "densify", parameters.GeometryJsonStrings.Length, parameters.GeometryType);
            return ExecuteDensify(parameters, scope);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "densify", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "densify", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the densify operation.");
        }
    }

    public async Task<IResult> HandleConvexHullAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "convexHull", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "convexHull", parseError ?? "No parameters");
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
                GeometryServiceLog.InvalidGeometryInput(_logger, "convexHull", geomError);
                return CreateError(context, 400, geomError);
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "convexHull", "No geometries provided");
                return CreateError(context, 400, "Parameter 'geometries' must contain at least one geometry.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "convexHull", srError);
                return CreateError(context, 400, srError);
            }

            var parameters = new ConvexHullParameters
            {
                GeometryJsonStrings = geomStrings,
                GeometryType = geomType,
                SR = sr!.Value
            };

            GeometryServiceLog.RequestParsed(_logger, "convexHull", parameters.GeometryJsonStrings.Length, parameters.GeometryType);
            return ExecuteConvexHull(parameters, scope);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "convexHull", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "convexHull", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the convexHull operation.");
        }
    }

    public async Task<IResult> HandleGeneralizeAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "generalize", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "generalize", parseError ?? "No parameters");
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
                GeometryServiceLog.InvalidGeometryInput(_logger, "generalize", geomError);
                return CreateError(context, 400, geomError);
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "generalize", "No geometries provided");
                return CreateError(context, 400, "Parameter 'geometries' must contain at least one geometry.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "generalize", srError);
                return CreateError(context, 400, srError);
            }

            var (maxDeviation, deviationError) = ParseRequiredPositiveDouble(
                GeometryServiceRequestParser.GetValue(values, "maxDeviation"),
                "maxDeviation");
            if (deviationError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "generalize", deviationError);
                return CreateError(context, 400, deviationError);
            }

            var parameters = new GeneralizeParameters
            {
                GeometryJsonStrings = geomStrings,
                GeometryType = geomType,
                SR = sr!.Value,
                MaxDeviation = maxDeviation
            };

            GeometryServiceLog.RequestParsed(_logger, "generalize", parameters.GeometryJsonStrings.Length, parameters.GeometryType);
            return ExecuteGeneralize(parameters, scope);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "generalize", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "generalize", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the generalize operation.");
        }
    }

    public async Task<IResult> HandleLabelPointsAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "labelPoints", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "labelPoints", parseError ?? "No parameters");
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
                GeometryServiceLog.InvalidGeometryInput(_logger, "labelPoints", "Missing polygons");
                return CreateError(context, 400, "Parameter 'polygons' is required.");
            }

            var (geomStrings, _, geomError) = GeometryServiceRequestParser.ParseGeometries(
                polygons,
                requestLimits.MaxGeometriesPerRequest,
                requestLimits.MaxGeometryJsonLength);
            if (geomError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "labelPoints", geomError);
                return CreateError(context, 400, geomError.Replace("'geometries'", "'polygons'", StringComparison.Ordinal));
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "labelPoints", "No geometries provided");
                return CreateError(context, 400, "Parameter 'polygons' must contain at least one geometry.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "labelPoints", srError);
                return CreateError(context, 400, srError);
            }

            var parameters = new LabelPointsParameters
            {
                GeometryJsonStrings = geomStrings,
                SR = sr!.Value
            };

            GeometryServiceLog.RequestParsed(_logger, "labelPoints", parameters.GeometryJsonStrings.Length, "esriGeometryPolygon");
            return ExecuteLabelPoints(parameters, scope);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "labelPoints", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "labelPoints", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the labelPoints operation.");
        }
    }

    public async Task<IResult> HandleCutAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "cut", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "cut", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            var (geomStrings, geomType, geomError) = GeometryServiceRequestParser.ParseGeometries(
                GeometryServiceRequestParser.GetValue(values, "target"),
                requestLimits.MaxGeometriesPerRequest,
                requestLimits.MaxGeometryJsonLength);
            if (geomError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "cut", geomError);
                return CreateError(context, 400, geomError.Replace("'geometries'", "'target'", StringComparison.Ordinal));
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "cut", "No target geometries provided");
                return CreateError(context, 400, "Parameter 'target' must contain at least one geometry.");
            }

            var (cutter, cutterError) = GeometryServiceRequestParser.ParseSingleGeometry(
                GeometryServiceRequestParser.GetValue(values, "cutter"),
                "cutter",
                requestLimits.MaxGeometryJsonLength);
            if (cutterError is not null || string.IsNullOrWhiteSpace(cutter))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "cut", cutterError ?? "Missing cutter");
                return CreateError(context, 400, cutterError ?? "Parameter 'cutter' is required.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "cut", srError);
                return CreateError(context, 400, srError);
            }

            var parameters = new CutParameters
            {
                TargetJsonStrings = geomStrings,
                GeometryType = geomType,
                CutterJson = cutter,
                SR = sr!.Value
            };

            GeometryServiceLog.RequestParsed(_logger, "cut", parameters.TargetJsonStrings.Length, parameters.GeometryType);
            return ExecuteCut(parameters, scope);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "cut", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "cut", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the cut operation.");
        }
    }

    public async Task<IResult> HandleTrimExtendAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "trimExtend", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "trimExtend", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            var (geomStrings, _, geomError) = GeometryServiceRequestParser.ParseGeometries(
                GeometryServiceRequestParser.GetValue(values, "polylines"),
                requestLimits.MaxGeometriesPerRequest,
                requestLimits.MaxGeometryJsonLength);
            if (geomError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "trimExtend", geomError);
                return CreateError(context, 400, geomError.Replace("'geometries'", "'polylines'", StringComparison.Ordinal));
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "trimExtend", "No polylines provided");
                return CreateError(context, 400, "Parameter 'polylines' must contain at least one geometry.");
            }

            var (trimExtendTo, trimError) = GeometryServiceRequestParser.ParseSingleGeometry(
                GeometryServiceRequestParser.GetValue(values, "trimExtendTo"),
                "trimExtendTo",
                requestLimits.MaxGeometryJsonLength);
            if (trimError is not null || string.IsNullOrWhiteSpace(trimExtendTo))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "trimExtend", trimError ?? "Missing trimExtendTo");
                return CreateError(context, 400, trimError ?? "Parameter 'trimExtendTo' is required.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "trimExtend", srError);
                return CreateError(context, 400, srError);
            }

            var parameters = new TrimExtendParameters
            {
                PolylineJsonStrings = geomStrings,
                TrimExtendToJson = trimExtendTo,
                SR = sr!.Value,
                ExtendHow = GeometryServiceRequestParser.GetValue(values, "extendHow")
            };

            GeometryServiceLog.RequestParsed(_logger, "trimExtend", parameters.PolylineJsonStrings.Length, "esriGeometryPolyline");
            return ExecuteTrimExtend(parameters, scope);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "trimExtend", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "trimExtend", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the trimExtend operation.");
        }
    }

    public async Task<IResult> HandleOffsetAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "offset", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "offset", parseError ?? "No parameters");
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
                GeometryServiceLog.InvalidGeometryInput(_logger, "offset", geomError);
                return CreateError(context, 400, geomError);
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "offset", "No geometries provided");
                return CreateError(context, 400, "Parameter 'geometries' must contain at least one geometry.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "offset", srError);
                return CreateError(context, 400, srError);
            }

            var offsetRaw = GeometryServiceRequestParser.GetValue(values, "offsetDistance");
            if (string.IsNullOrWhiteSpace(offsetRaw)
                || !double.TryParse(offsetRaw.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var offsetDistance)
                || double.IsNaN(offsetDistance) || double.IsInfinity(offsetDistance))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "offset", "Invalid offsetDistance");
                return CreateError(context, 400, "Parameter 'offsetDistance' is required and must be a number.");
            }

            var bevelRaw = GeometryServiceRequestParser.GetValue(values, "bevelRatio");
            var bevelRatio = 1.1;
            if (!string.IsNullOrWhiteSpace(bevelRaw)
                && double.TryParse(bevelRaw.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedBevel)
                && parsedBevel > 0)
            {
                bevelRatio = parsedBevel;
            }

            var parameters = new OffsetParameters
            {
                GeometryJsonStrings = geomStrings,
                GeometryType = geomType,
                SR = sr!.Value,
                OffsetDistance = offsetDistance,
                OffsetUnit = GeometryServiceRequestParser.GetValue(values, "offsetUnit"),
                OffsetHow = GeometryServiceRequestParser.GetValue(values, "offsetHow"),
                BevelRatio = bevelRatio
            };

            GeometryServiceLog.RequestParsed(_logger, "offset", parameters.GeometryJsonStrings.Length, parameters.GeometryType);
            return ExecuteOffset(parameters, scope);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "offset", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "offset", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the offset operation.");
        }
    }

    public async Task<IResult> HandleAutoCompleteAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "autoComplete", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "autoComplete", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            var polygonsRaw = GeometryServiceRequestParser.GetValue(values, "polygons");
            var (polygonStrings, _, polygonError) = string.IsNullOrWhiteSpace(polygonsRaw)
                ? ([], (string?)null, (string?)null)
                : GeometryServiceRequestParser.ParseGeometries(
                    polygonsRaw,
                    requestLimits.MaxGeometriesPerRequest,
                    requestLimits.MaxGeometryJsonLength);
            if (polygonError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "autoComplete", polygonError);
                return CreateError(context, 400, polygonError.Replace("'geometries'", "'polygons'", StringComparison.Ordinal));
            }

            var (polylineStrings, _, polylineError) = GeometryServiceRequestParser.ParseGeometries(
                GeometryServiceRequestParser.GetValue(values, "polylines"),
                requestLimits.MaxGeometriesPerRequest,
                requestLimits.MaxGeometryJsonLength);
            if (polylineError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "autoComplete", polylineError);
                return CreateError(context, 400, polylineError.Replace("'geometries'", "'polylines'", StringComparison.Ordinal));
            }

            if (polylineStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "autoComplete", "No polylines provided");
                return CreateError(context, 400, "Parameter 'polylines' must contain at least one geometry.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "autoComplete", srError);
                return CreateError(context, 400, srError);
            }

            var parameters = new AutoCompleteParameters
            {
                PolygonJsonStrings = polygonStrings,
                PolylineJsonStrings = polylineStrings,
                SR = sr!.Value
            };

            GeometryServiceLog.RequestParsed(_logger, "autoComplete", parameters.PolylineJsonStrings.Length, "esriGeometryPolygon");
            return ExecuteAutoComplete(parameters, scope);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "autoComplete", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "autoComplete", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the autoComplete operation.");
        }
    }

    public async Task<IResult> HandleReshapeAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "reshape", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            var requestLimits = ResolveRequestLimits(context);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "reshape", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            var (target, targetError) = GeometryServiceRequestParser.ParseSingleGeometry(
                GeometryServiceRequestParser.GetValue(values, "target"),
                "target",
                requestLimits.MaxGeometryJsonLength);
            if (targetError is not null || string.IsNullOrWhiteSpace(target))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "reshape", targetError ?? "Missing target");
                return CreateError(context, 400, targetError ?? "Parameter 'target' is required.");
            }

            var (reshaper, reshaperError) = GeometryServiceRequestParser.ParseSingleGeometry(
                GeometryServiceRequestParser.GetValue(values, "reshaper"),
                "reshaper",
                requestLimits.MaxGeometryJsonLength);
            if (reshaperError is not null || string.IsNullOrWhiteSpace(reshaper))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "reshape", reshaperError ?? "Missing reshaper");
                return CreateError(context, 400, reshaperError ?? "Parameter 'reshaper' is required.");
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "reshape", srError);
                return CreateError(context, 400, srError);
            }

            var parameters = new ReshapeParameters
            {
                TargetJson = target,
                ReshaperJson = reshaper,
                SR = sr!.Value
            };

            GeometryServiceLog.RequestParsed(_logger, "reshape", 1, null);
            return ExecuteReshape(parameters, scope);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "reshape", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "reshape", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the reshape operation.");
        }
    }

    public async Task<IResult> HandleFindTransformationsAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "findTransformations", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "findTransformations", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            var (inSr, inSrError) = await ResolveRequiredSpatialReferenceAsync(values, "inSR", ct);
            if (inSrError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "findTransformations", inSrError);
                return CreateError(context, 400, inSrError);
            }

            var (outSr, outSrError) = await ResolveRequiredSpatialReferenceAsync(values, "outSR", ct);
            if (outSrError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "findTransformations", outSrError);
                return CreateError(context, 400, outSrError);
            }

            var parameters = new FindTransformationsParameters
            {
                InSR = inSr!.Value,
                OutSR = outSr!.Value
            };

            return ExecuteFindTransformations(parameters, scope);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "findTransformations", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "findTransformations", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the findTransformations operation.");
        }
    }

    public async Task<IResult> HandleToGeoCoordinateStringAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "toGeoCoordinateString", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "toGeoCoordinateString", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "toGeoCoordinateString", srError);
                return CreateError(context, 400, srError);
            }

            var (conversionType, typeError) = ResolveGeoCoordinateConversionType(values);
            if (typeError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "toGeoCoordinateString", typeError);
                return CreateError(context, 400, typeError);
            }

            var (coordinates, coordError) = ParseCoordinatePairs(
                GeometryServiceRequestParser.GetValue(values, "coordinates"));
            if (coordError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "toGeoCoordinateString", coordError);
                return CreateError(context, 400, coordError);
            }

            if (coordinates.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "toGeoCoordinateString", "No coordinates provided");
                return CreateError(context, 400, "Parameter 'coordinates' must contain at least one coordinate.");
            }

            var (numOfDigits, digitsError) = ParseNumOfDigits(GeometryServiceRequestParser.GetValue(values, "numOfDigits"));
            if (digitsError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "toGeoCoordinateString", digitsError);
                return CreateError(context, 400, digitsError);
            }

            var parameters = new ToGeoCoordinateStringParameters
            {
                Coordinates = coordinates,
                SR = sr!.Value,
                ConversionType = conversionType,
                ConversionMode = GeometryServiceRequestParser.GetValue(values, "conversionMode"),
                NumOfDigits = numOfDigits,
                AddSpaces = !GeometryServiceRequestParser.ParseBool(
                    GeometryServiceRequestParser.GetValue(values, "rounding"))
            };

            return await ExecuteToGeoCoordinateStringAsync(parameters, scope, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "toGeoCoordinateString", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "toGeoCoordinateString", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the toGeoCoordinateString operation.");
        }
    }

    public async Task<IResult> HandleFromGeoCoordinateStringAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "fromGeoCoordinateString", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "fromGeoCoordinateString", parseError ?? "No parameters");
                return CreateRequestParameterError(context, parseError);
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(context, 400, formatError);
            }

            var (sr, srError) = await ResolveRequiredSpatialReferenceAsync(values, "sr", ct);
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "fromGeoCoordinateString", srError);
                return CreateError(context, 400, srError);
            }

            var (conversionType, typeError) = ResolveGeoCoordinateConversionType(values);
            if (typeError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "fromGeoCoordinateString", typeError);
                return CreateError(context, 400, typeError);
            }

            var (strings, stringsError) = ParseGeoCoordinateStrings(
                GeometryServiceRequestParser.GetValue(values, "strings"));
            if (stringsError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "fromGeoCoordinateString", stringsError);
                return CreateError(context, 400, stringsError);
            }

            if (strings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "fromGeoCoordinateString", "No strings provided");
                return CreateError(context, 400, "Parameter 'strings' must contain at least one grid reference string.");
            }

            var parameters = new FromGeoCoordinateStringParameters
            {
                Strings = strings,
                SR = sr!.Value,
                ConversionType = conversionType,
                ConversionMode = GeometryServiceRequestParser.GetValue(values, "conversionMode")
            };

            return await ExecuteFromGeoCoordinateStringAsync(parameters, scope, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "fromGeoCoordinateString", ex.Message);
            scope.RecordException(ex);
            return CreateError(context, 400, InvalidGeometryInputMessage);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "fromGeoCoordinateString", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(context, 500, "An internal error occurred during the fromGeoCoordinateString operation.");
        }
    }

    private async Task<IResult> ExecuteToGeoCoordinateStringAsync(
        ToGeoCoordinateStringParameters parameters,
        HonuaTelemetryScope scope,
        CancellationToken ct)
    {
        var strings = new string[parameters.Coordinates.Length];
        for (var i = 0; i < parameters.Coordinates.Length; i++)
        {
            var coordinate = parameters.Coordinates[i];
            var (longitude, latitude) = await ToWgs84Async(coordinate[0], coordinate[1], parameters.SR, ct)
                .ConfigureAwait(false);

            // USNG conventionally inserts spaces; MGRS conventionally omits them. The
            // grid scheme is identical, so only the formatting differs.
            var addSpaces = parameters.AddSpaces
                && !string.Equals(parameters.ConversionType, "MGRS", StringComparison.OrdinalIgnoreCase);

            strings[i] = MgrsConverter.ToMgrs(longitude, latitude, parameters.NumOfDigits, addSpaces);
        }

        var response = new GeometryServiceToGeoCoordinateStringResponse { Strings = strings };
        GeometryServiceLog.BinaryOperationCompleted(_logger, "toGeoCoordinateString", strings.Length);
        scope.SetSuccess(strings.Length);
        return Results.Json(
            response,
            GeometryServiceJsonContext.Default.GeometryServiceToGeoCoordinateStringResponse,
            contentType: "application/json");
    }

    private async Task<IResult> ExecuteFromGeoCoordinateStringAsync(
        FromGeoCoordinateStringParameters parameters,
        HonuaTelemetryScope scope,
        CancellationToken ct)
    {
        var coordinates = new double[parameters.Strings.Length][];
        for (var i = 0; i < parameters.Strings.Length; i++)
        {
            var (longitude, latitude) = MgrsConverter.FromMgrs(parameters.Strings[i]);
            var (x, y) = await FromWgs84Async(longitude, latitude, parameters.SR, ct).ConfigureAwait(false);
            coordinates[i] = [x, y];
        }

        var response = new GeometryServiceFromGeoCoordinateStringResponse { Coordinates = coordinates };
        GeometryServiceLog.BinaryOperationCompleted(_logger, "fromGeoCoordinateString", coordinates.Length);
        scope.SetSuccess(coordinates.Length);
        return Results.Json(
            response,
            GeometryServiceJsonContext.Default.GeometryServiceFromGeoCoordinateStringResponse,
            contentType: "application/json");
    }

    // MGRS / USNG are defined on geographic (longitude/latitude) coordinates, so
    // projected inputs are reprojected to WGS84 before grid encoding.
    private async Task<(double Longitude, double Latitude)> ToWgs84Async(double x, double y, int srid, CancellationToken ct)
    {
        if (srid == 4326)
        {
            return (x, y);
        }

        var pointWkb = new WKBWriter().Write(
            NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory().CreatePoint(new Coordinate(x, y)));
        var projectedWkb = await _operationService.ProjectAsync(pointWkb, srid, 4326, ct).ConfigureAwait(false);
        var projected = (Point)new WKBReader().Read(projectedWkb);
        return (projected.X, projected.Y);
    }

    private async Task<(double X, double Y)> FromWgs84Async(double longitude, double latitude, int srid, CancellationToken ct)
    {
        if (srid == 4326)
        {
            return (longitude, latitude);
        }

        var pointWkb = new WKBWriter().Write(
            NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory().CreatePoint(new Coordinate(longitude, latitude)));
        var projectedWkb = await _operationService.ProjectAsync(pointWkb, 4326, srid, ct).ConfigureAwait(false);
        var projected = (Point)new WKBReader().Read(projectedWkb);
        return (projected.X, projected.Y);
    }

    private static (string ConversionType, string? Error) ResolveGeoCoordinateConversionType(
        IReadOnlyDictionary<string, StringValues> values)
    {
        var raw = GeometryServiceRequestParser.GetValue(values, "conversionType");
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Esri defaults to MGRS when conversionType is omitted.
            return ("MGRS", null);
        }

        var normalized = raw.Trim();
        if (string.Equals(normalized, "MGRS", StringComparison.OrdinalIgnoreCase))
        {
            return ("MGRS", null);
        }

        if (string.Equals(normalized, "USNG", StringComparison.OrdinalIgnoreCase))
        {
            return ("USNG", null);
        }

        // UTM, GARS, and GEOREF are recognized Esri conversion types but are not yet
        // implemented; return a clear 400 rather than a 500.
        if (string.Equals(normalized, "UTM", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "GARS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "GEOREF", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "DD", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "DDM", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "DMS", StringComparison.OrdinalIgnoreCase))
        {
            return (normalized, $"Conversion type '{normalized}' is not supported. Supported types: MGRS, USNG.");
        }

        return (normalized, $"Parameter 'conversionType' value '{normalized}' is not recognized. Supported types: MGRS, USNG.");
    }

    private static (double[][] Coordinates, string? Error) ParseCoordinatePairs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ([], "Parameter 'coordinates' is required.");
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ([], "Parameter 'coordinates' must be a JSON array of [x, y] pairs.");
            }

            var result = new List<double[]>(doc.RootElement.GetArrayLength());
            foreach (var pair in doc.RootElement.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2)
                {
                    return ([], "Each entry in 'coordinates' must be an [x, y] array with at least two numbers.");
                }

                if (!pair[0].TryGetDouble(out var x) || !pair[1].TryGetDouble(out var y))
                {
                    return ([], "Each coordinate in 'coordinates' must contain numeric x and y values.");
                }

                result.Add([x, y]);
            }

            return (result.ToArray(), null);
        }
        catch (JsonException)
        {
            return ([], "Parameter 'coordinates' contains invalid JSON.");
        }
    }

    private static (string[] Strings, string? Error) ParseGeoCoordinateStrings(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ([], "Parameter 'strings' is required.");
        }

        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return ([], "Parameter 'strings' must be a JSON array of grid reference strings.");
                }

                var result = new List<string>(doc.RootElement.GetArrayLength());
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        return ([], "Each entry in 'strings' must be a string.");
                    }

                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(value);
                    }
                }

                return (result.ToArray(), null);
            }
            catch (JsonException)
            {
                return ([], "Parameter 'strings' contains invalid JSON.");
            }
        }

        // Single bare string value (GET convenience form).
        return ([raw], null);
    }

    private static (int NumOfDigits, string? Error) ParseNumOfDigits(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (5, null);
        }

        if (!int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var digits))
        {
            return (5, "Parameter 'numOfDigits' must be an integer between 0 and 5.");
        }

        if (digits is < 0 or > 5)
        {
            return (5, "Parameter 'numOfDigits' must be between 0 and 5.");
        }

        return (digits, null);
    }

    private IResult ExecuteCut(CutParameters parameters, HonuaTelemetryScope scope)
    {
        var cutter = ReadGeometry(parameters.CutterJson);
        var writer = new WKBWriter();
        var resultWkbs = new List<byte[]>();
        var cutIndexes = new List<int>();

        for (var i = 0; i < parameters.TargetJsonStrings.Length; i++)
        {
            var target = ReadGeometry(parameters.TargetJsonStrings[i]);
            foreach (var piece in CutGeometry(target, cutter))
            {
                resultWkbs.Add(writer.Write(piece));
                cutIndexes.Add(i);
            }
        }

        var geometries = new JsonElement[resultWkbs.Count];
        for (var j = 0; j < resultWkbs.Count; j++)
        {
            geometries[j] = _geometryConverter.ConvertWkbToGeoServicesGeometry(resultWkbs[j], parameters.SR);
        }

        var response = new GeometryServiceCutResponse
        {
            Geometries = geometries,
            CutIndexes = cutIndexes.ToArray()
        };
        GeometryServiceLog.BinaryOperationCompleted(_logger, "cut", resultWkbs.Count);
        scope.SetSuccess(resultWkbs.Count);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceCutResponse, contentType: "application/json");
    }

    // Splits a target geometry by a cutter polyline. Polylines are split where the
    // cutter crosses them; polygons are split into the pieces formed by the cutter
    // dividing the area. Returns the original geometry unchanged when no cut occurs.
    private static Geometry[] CutGeometry(Geometry target, Geometry cutter)
    {
        // Esri /cut returns no pieces (and no cutIndexes) when the cutter does not actually
        // cut the target. ST_Crosses mirrors PostGIS ST_Split semantics: a cutter that only
        // touches or misses the target produces no cut (avoids the false-positive cutIndex).
        if (!target.Crosses(cutter))
        {
            return Array.Empty<Geometry>();
        }

        if (target is IPolygonal)
        {
            var boundary = target.Boundary;
            var nodedEdges = boundary.Union(cutter);
            var polygonizer = new NetTopologySuite.Operation.Polygonize.Polygonizer();
            polygonizer.Add(nodedEdges);
            var pieces = polygonizer.GetPolygons()
                .Cast<Geometry>()
                .Where(p => target.Contains(p.InteriorPoint))
                .ToArray();
            return pieces.Length > 1 ? pieces : Array.Empty<Geometry>();
        }

        if (target is ILineal)
        {
            // Node the target line against the cutter (union introduces vertices at every
            // crossing) then line-merge: each maximal run between crossing points becomes a
            // separate piece. This splits the target wherever the cutter crosses it.
            var noded = target.Union(cutter);
            var merger = new NetTopologySuite.Operation.Linemerge.LineMerger();
            merger.Add(noded);
            var merged = merger.GetMergedLineStrings().Cast<Geometry>().ToArray();

            // Keep only pieces that belong to the original target (drop the cutter's own runs).
            var pieces = merged
                .Where(piece => target.Buffer(target.Length * 1e-9 + 1e-12).Contains(piece))
                .ToArray();
            return pieces.Length > 1 ? pieces : Array.Empty<Geometry>();
        }

        return Array.Empty<Geometry>();
    }

    private IResult ExecuteTrimExtend(TrimExtendParameters parameters, HonuaTelemetryScope scope)
    {
        var trimLine = ReadGeometry(parameters.TrimExtendToJson);
        var results = new List<byte[]>(parameters.PolylineJsonStrings.Length);
        var writer = new WKBWriter();

        foreach (var polylineJson in parameters.PolylineJsonStrings)
        {
            var polyline = ReadGeometry(polylineJson);
            var adjusted = TrimOrExtend(polyline, trimLine);
            results.Add(writer.Write(adjusted));
        }

        var response = ConvertToResponse(results, parameters.SR, "esriGeometryPolyline");
        GeometryServiceLog.BinaryOperationCompleted(_logger, "trimExtend", results.Count);
        scope.SetSuccess(results.Count);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceResponse, contentType: "application/json");
    }

    // Trims a polyline at its intersection with the trim line, or extends the polyline's
    // terminal segment to reach the trim line when they do not currently intersect.
    private static Geometry TrimOrExtend(Geometry polyline, Geometry trimLine)
    {
        var factory = polyline.Factory;
        var coords = polyline.Coordinates;
        if (coords.Length < 2)
        {
            return polyline;
        }

        if (polyline.Intersects(trimLine))
        {
            // Trim: keep the portion of the polyline up to its first intersection point.
            var intersection = polyline.Intersection(trimLine);
            var cutPoint = intersection.IsEmpty ? null : intersection.Coordinate;
            if (cutPoint is not null)
            {
                var kept = new List<Coordinate> { coords[0] };
                for (var i = 1; i < coords.Length; i++)
                {
                    var segment = factory.CreateLineString(new[] { coords[i - 1], coords[i] });
                    if (segment.Intersects(trimLine))
                    {
                        kept.Add(cutPoint);
                        break;
                    }

                    kept.Add(coords[i]);
                }

                if (kept.Count >= 2)
                {
                    return factory.CreateLineString(kept.ToArray());
                }
            }

            return polyline;
        }

        // Extend: project the final segment forward until it meets the trim line.
        var last = coords[^1];
        var prev = coords[^2];
        var dx = last.X - prev.X;
        var dy = last.Y - prev.Y;
        var len = Math.Sqrt((dx * dx) + (dy * dy));
        if (len == 0)
        {
            return polyline;
        }

        var envelope = trimLine.EnvelopeInternal;
        var reach = Math.Max(envelope.Width, envelope.Height) + polyline.EnvelopeInternal.MaxExtent + len;
        var farPoint = new Coordinate(last.X + (dx / len * reach), last.Y + (dy / len * reach));
        var ray = factory.CreateLineString(new[] { last, farPoint });
        var hit = ray.Intersection(trimLine);
        if (hit.IsEmpty || hit.Coordinate is null)
        {
            return polyline;
        }

        var extended = new Coordinate[coords.Length + 1];
        Array.Copy(coords, extended, coords.Length);
        extended[^1] = hit.Coordinate;
        return factory.CreateLineString(extended);
    }

    private IResult ExecuteOffset(OffsetParameters parameters, HonuaTelemetryScope scope)
    {
        var distance = parameters.OffsetDistance
            * GeometryServiceRequestParser.GetUnitMultiplier(parameters.OffsetUnit);
        var results = new List<byte[]>(parameters.GeometryJsonStrings.Length);
        var writer = new WKBWriter();
        var joinStyle = ResolveOffsetJoinStyle(parameters.OffsetHow);

        foreach (var geomJson in parameters.GeometryJsonStrings)
        {
            var geometry = ReadGeometry(geomJson);
            Geometry offset;
            if (geometry is IPolygonal)
            {
                // For polygons an offset is the buffer of the area by the (signed) distance.
                offset = geometry.Buffer(distance);
            }
            else
            {
                // OffsetCurve produces a line offset to one side of the input line(s),
                // which is the polyline semantics ArcGIS exposes for offset. NTS (like
                // PostGIS ST_OffsetCurve) treats a positive distance as the LEFT side of the
                // line direction; Esri treats a positive offsetDistance as the RIGHT side, so
                // negate to match the GeoServices contract.
                offset = NetTopologySuite.Operation.Buffer.OffsetCurve.GetCurve(
                    geometry,
                    -distance,
                    NetTopologySuite.Operation.Buffer.BufferParameters.DefaultQuadrantSegments,
                    joinStyle,
                    Math.Max(parameters.BevelRatio, NetTopologySuite.Operation.Buffer.BufferParameters.DefaultMitreLimit));
            }

            results.Add(writer.Write(offset));
        }

        var response = ConvertToResponse(results, parameters.SR, parameters.GeometryType);
        GeometryServiceLog.BinaryOperationCompleted(_logger, "offset", results.Count);
        scope.SetSuccess(results.Count);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceResponse, contentType: "application/json");
    }

    private static NetTopologySuite.Operation.Buffer.JoinStyle ResolveOffsetJoinStyle(string? offsetHow)
        => offsetHow?.ToLowerInvariant() switch
        {
            "esrigeometryoffsetbevelled" => NetTopologySuite.Operation.Buffer.JoinStyle.Bevel,
            "esrigeometryoffsetmitered" => NetTopologySuite.Operation.Buffer.JoinStyle.Mitre,
            _ => NetTopologySuite.Operation.Buffer.JoinStyle.Round
        };

    private IResult ExecuteAutoComplete(AutoCompleteParameters parameters, HonuaTelemetryScope scope)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        var edges = new List<Geometry>();

        foreach (var polygonJson in parameters.PolygonJsonStrings)
        {
            edges.Add(ReadGeometry(polygonJson).Boundary);
        }

        foreach (var polylineJson in parameters.PolylineJsonStrings)
        {
            edges.Add(ReadGeometry(polylineJson));
        }

        // Union all boundary/polyline edges to node them, then polygonize the closed regions.
        Geometry noded = factory.CreateGeometryCollection(edges.ToArray());
        noded = NetTopologySuite.Operation.Union.UnaryUnionOp.Union(noded);

        var polygonizer = new NetTopologySuite.Operation.Polygonize.Polygonizer();
        polygonizer.Add(noded);

        var newPolygons = polygonizer.GetPolygons().Cast<Geometry>().ToArray();
        var writer = new WKBWriter();
        var results = new List<byte[]>(newPolygons.Length);
        foreach (var polygon in newPolygons)
        {
            results.Add(writer.Write(polygon));
        }

        var response = ConvertToResponse(results, parameters.SR, "esriGeometryPolygon");
        GeometryServiceLog.BinaryOperationCompleted(_logger, "autoComplete", results.Count);
        scope.SetSuccess(results.Count);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceResponse, contentType: "application/json");
    }

    private IResult ExecuteReshape(ReshapeParameters parameters, HonuaTelemetryScope scope)
    {
        var target = ReadGeometry(parameters.TargetJson);
        var reshaper = ReadGeometry(parameters.ReshaperJson);
        var reshaped = ReshapeGeometry(target, reshaper);

        var reshapedWkb = new WKBWriter().Write(reshaped);
        var geometryElement = _geometryConverter.ConvertWkbToGeoServicesGeometry(reshapedWkb, parameters.SR);
        var response = new GeometryServiceGeometryResponse { Geometry = geometryElement };
        GeometryServiceLog.BinaryOperationCompleted(_logger, "reshape", 1);
        scope.SetSuccess(1);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceGeometryResponse, contentType: "application/json");
    }

    // Reshapes a polygon or polyline using a reshaper line. The reshaper must cross the
    // target boundary; the portion of the boundary between the two crossing points is
    // replaced by the reshaper line, yielding a modified geometry.
    private static Geometry ReshapeGeometry(Geometry target, Geometry reshaper)
    {
        var factory = target.Factory;

        if (target is IPolygonal)
        {
            var boundary = target.Boundary;
            if (!boundary.Intersects(reshaper))
            {
                return target;
            }

            var nodedEdges = boundary.Union(reshaper);
            var polygonizer = new NetTopologySuite.Operation.Polygonize.Polygonizer();
            polygonizer.Add(nodedEdges);
            var candidates = polygonizer.GetPolygons().Cast<Geometry>().ToArray();
            if (candidates.Length == 0)
            {
                return target;
            }

            // Choose the candidate whose area is closest to (target area +/- the reshaper lobe);
            // in practice the largest candidate that uses the reshaper edge is the reshaped result.
            var reshaped = candidates
                .Where(c => c.Intersects(reshaper))
                .OrderByDescending(c => c.Area)
                .FirstOrDefault() ?? candidates.OrderByDescending(c => c.Area).First();
            return reshaped;
        }

        if (target is ILineal)
        {
            var intersection = target.Intersection(reshaper);
            if (intersection.IsEmpty)
            {
                return target;
            }

            // Merge the reshaper into the line and pick the longest connected result.
            var merger = new NetTopologySuite.Operation.Linemerge.LineMerger();
            merger.Add(target.Difference(reshaper.Buffer(target.Length * 1e-9)));
            merger.Add(reshaper);
            var merged = merger.GetMergedLineStrings().Cast<Geometry>().ToArray();
            if (merged.Length == 0)
            {
                return target;
            }

            return merged.OrderByDescending(m => m.Length).First();
        }

        return target;
    }

    private IResult ExecuteFindTransformations(FindTransformationsParameters parameters, HonuaTelemetryScope scope)
    {
        // Honua performs CRS transformation via the shared projection pipeline (PROJ-backed)
        // and does not expose a discrete Esri datum-transformation catalog. The PROJ pipeline
        // selects and applies the appropriate transformation internally during `project`, so
        // there is no explicit transformation for clients to choose between; an empty list is
        // the spec-correct answer for every inSR/outSR pair. This is documented as a known
        // limitation in docs/gis/geometry-service-matrix.md.
        var transformations = Array.Empty<GeometryServiceTransformation>();

        var response = new GeometryServiceFindTransformationsResponse
        {
            Transformations = transformations
        };
        GeometryServiceLog.BinaryOperationCompleted(_logger, "findTransformations", transformations.Length);
        scope.SetSuccess(transformations.Length);
        return Results.Json(
            response,
            GeometryServiceJsonContext.Default.GeometryServiceFindTransformationsResponse,
            contentType: "application/json");
    }

    private async Task<IResult> ExecuteDistanceAsync(
        DistanceParameters parameters,
        MeasurementSpatialContext spatialContext,
        HonuaTelemetryScope scope,
        CancellationToken ct)
    {
        var geometry1 = ReadGeometry(parameters.Geometry1Json);
        var geometry2 = ReadGeometry(parameters.Geometry2Json);

        double distanceMeters;
        if (parameters.Geodesic)
        {
            // Build a polyline between the closest points of the two geometries and
            // measure it geodesically through the shared measurement pipeline (projects
            // to EPSG:4326 and uses ST_Length on geography). This reuses the same
            // geodesic path that 'lengths' relies on.
            var nearest = NetTopologySuite.Operation.Distance.DistanceOp.NearestPoints(geometry1, geometry2);
            var lineWkb = new WKBWriter().Write(
                geometry1.Factory.CreateLineString([nearest[0], nearest[1]]));
            var measurementGeometry = await ProjectForGeodeticMeasurementAsync(lineWkb, parameters.SR, ct).ConfigureAwait(false);
            distanceMeters = await _operationService.LengthAsync(measurementGeometry, 4326, ct).ConfigureAwait(false);
        }
        else
        {
            distanceMeters = geometry1.Distance(geometry2) * spatialContext.MetersPerNativeUnit;
        }

        var distance = string.IsNullOrWhiteSpace(parameters.DistanceUnit)
            ? (parameters.Geodesic
                ? distanceMeters
                : geometry1.Distance(geometry2))
            : ConvertLengthFromMeters(distanceMeters, parameters.DistanceUnit);

        var response = new GeometryServiceDistanceResponse { Distance = distance };
        GeometryServiceLog.MeasurementOperationCompleted(_logger, "distance", 1);
        scope.SetSuccess(1);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceDistanceResponse, contentType: "application/json");
    }

    private IResult ExecuteRelation(RelationParameters parameters, HonuaTelemetryScope scope)
    {
        var geometries1 = new Geometry[parameters.Geometries1Json.Length];
        for (var i = 0; i < geometries1.Length; i++)
        {
            geometries1[i] = ReadGeometry(parameters.Geometries1Json[i]);
        }

        var geometries2 = new Geometry[parameters.Geometries2Json.Length];
        for (var j = 0; j < geometries2.Length; j++)
        {
            geometries2[j] = ReadGeometry(parameters.Geometries2Json[j]);
        }

        var matches = new List<GeometryServiceRelationPair>();
        for (var i = 0; i < geometries1.Length; i++)
        {
            for (var j = 0; j < geometries2.Length; j++)
            {
                if (EvaluateRelation(parameters.Relation, parameters.RelationParam, geometries1[i], geometries2[j]))
                {
                    matches.Add(new GeometryServiceRelationPair { Geometry1Index = i, Geometry2Index = j });
                }
            }
        }

        var response = new GeometryServiceRelationResponse { Relations = matches.ToArray() };
        GeometryServiceLog.BinaryOperationCompleted(_logger, "relation", matches.Count);
        scope.SetSuccess(matches.Count);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceRelationResponse, contentType: "application/json");
    }

    private IResult ExecuteDensify(DensifyParameters parameters, HonuaTelemetryScope scope)
    {
        var densified = new List<byte[]>(parameters.GeometryJsonStrings.Length);
        var writer = new WKBWriter();
        foreach (var geomJson in parameters.GeometryJsonStrings)
        {
            var geometry = ReadGeometry(geomJson);
            var result = NetTopologySuite.Densify.Densifier.Densify(geometry, parameters.MaxSegmentLength);
            densified.Add(writer.Write(result));
        }

        var response = ConvertToResponse(densified, parameters.SR, parameters.GeometryType);
        GeometryServiceLog.SimplifyOperationCompleted(_logger, parameters.GeometryJsonStrings.Length);
        scope.SetSuccess(densified.Count);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceResponse, contentType: "application/json");
    }

    private IResult ExecuteConvexHull(ConvexHullParameters parameters, HonuaTelemetryScope scope)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        var geometries = new Geometry[parameters.GeometryJsonStrings.Length];
        for (var i = 0; i < geometries.Length; i++)
        {
            geometries[i] = ReadGeometry(parameters.GeometryJsonStrings[i]);
        }

        // convexHull computes a single hull over the union of all input geometries.
        var combined = geometries.Length == 1
            ? geometries[0]
            : factory.CreateGeometryCollection(geometries);
        var hull = combined.ConvexHull();
        var hullWkb = new WKBWriter().Write(hull);
        var hullElement = _geometryConverter.ConvertWkbToGeoServicesGeometry(hullWkb, parameters.SR);

        var response = new GeometryServiceGeometryResponse { Geometry = hullElement };
        GeometryServiceLog.BinaryOperationCompleted(_logger, "convexHull", parameters.GeometryJsonStrings.Length);
        scope.SetSuccess(1);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceGeometryResponse, contentType: "application/json");
    }

    private IResult ExecuteGeneralize(GeneralizeParameters parameters, HonuaTelemetryScope scope)
    {
        var generalized = new List<byte[]>(parameters.GeometryJsonStrings.Length);
        var writer = new WKBWriter();
        foreach (var geomJson in parameters.GeometryJsonStrings)
        {
            var geometry = ReadGeometry(geomJson);
            var result = NetTopologySuite.Simplify.DouglasPeuckerSimplifier.Simplify(geometry, parameters.MaxDeviation);
            generalized.Add(writer.Write(result));
        }

        var response = ConvertToResponse(generalized, parameters.SR, parameters.GeometryType);
        GeometryServiceLog.SimplifyOperationCompleted(_logger, parameters.GeometryJsonStrings.Length);
        scope.SetSuccess(generalized.Count);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceResponse, contentType: "application/json");
    }

    private IResult ExecuteLabelPoints(LabelPointsParameters parameters, HonuaTelemetryScope scope)
    {
        var points = new List<byte[]>(parameters.GeometryJsonStrings.Length);
        var writer = new WKBWriter();
        foreach (var geomJson in parameters.GeometryJsonStrings)
        {
            var geometry = ReadGeometry(geomJson);
            // InteriorPoint is guaranteed to lie inside the polygon, matching the
            // semantics ArcGIS uses for label placement points.
            var labelPoint = geometry.InteriorPoint;
            points.Add(writer.Write(labelPoint));
        }

        // The Esri /labelPoints operation returns the result points under a
        // `labelPoints` key (not the generic `geometries` array). Emitting the
        // array shape made the ArcGIS Maps SDK for JavaScript read labelPoints ->
        // undefined and return [].
        var labelPointElements = new JsonElement[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            labelPointElements[i] = _geometryConverter.ConvertWkbToGeoServicesGeometry(points[i], parameters.SR);
        }

        var response = new GeometryServiceLabelPointsResponse { LabelPoints = labelPointElements };
        GeometryServiceLog.BinaryOperationCompleted(_logger, "labelPoints", points.Count);
        scope.SetSuccess(points.Count);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceLabelPointsResponse, contentType: "application/json");
    }

    private Geometry ReadGeometry(string geometryJson)
    {
        var wkb = _geometryConverter.ConvertGeoServicesJsonToWkb(geometryJson);
        return new WKBReader().Read(wkb);
    }

    private static (double Value, string? Error) ParseRequiredPositiveDouble(string? raw, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (0, $"Parameter '{parameterName}' is required.");
        }

        if (!double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return (0, $"Parameter '{parameterName}' must be a positive number.");
        }

        return (value, null);
    }

    private static bool RelationRequiresRelationParam(string relation)
        => string.Equals(relation, "esriGeometryRelationRelation", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedRelation(string relation)
        => relation.ToLowerInvariant() switch
        {
            "esrigeometryrelationcross" => true,
            "esrigeometryrelationdisjoint" => true,
            "esrigeometryrelationin" => true,
            "esrigeometryrelationinteriorintersection" => true,
            "esrigeometryrelationintersection" => true,
            "esrigeometryrelationlinetouch" => true,
            "esrigeometryrelationoverlap" => true,
            "esrigeometryrelationpointtouch" => true,
            "esrigeometryrelationtouch" => true,
            "esrigeometryrelationwithin" => true,
            "esrigeometryrelationrelation" => true,
            _ => false
        };

    private static bool EvaluateRelation(string relation, string? relationParam, Geometry geometry1, Geometry geometry2)
        => relation.ToLowerInvariant() switch
        {
            "esrigeometryrelationcross" => geometry1.Crosses(geometry2),
            "esrigeometryrelationdisjoint" => geometry1.Disjoint(geometry2),
            "esrigeometryrelationin" => geometry1.Within(geometry2),
            "esrigeometryrelationwithin" => geometry1.Within(geometry2),
            "esrigeometryrelationinteriorintersection" => geometry1.Relate(geometry2, "T********"),
            "esrigeometryrelationintersection" => geometry1.Intersects(geometry2),
            "esrigeometryrelationoverlap" => geometry1.Overlaps(geometry2),
            "esrigeometryrelationtouch" => geometry1.Touches(geometry2),
            "esrigeometryrelationlinetouch" => geometry1.Relate(geometry2, "F***T****"),
            "esrigeometryrelationpointtouch" => geometry1.Relate(geometry2, "FT*******"),
            "esrigeometryrelationrelation" => geometry1.Relate(geometry2, relationParam!),
            _ => false
        };

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

                if (parameters.Geodesic && parameters.InSR != 4326)
                {
                    wkb = await _operationService.ProjectAsync(wkb, parameters.InSR, 4326, ct).ConfigureAwait(false);
                }
                else if (!parameters.Geodesic && bufferSrid != parameters.InSR)
                {
                    wkb = await _operationService.ProjectAsync(wkb, parameters.InSR, bufferSrid, ct).ConfigureAwait(false);
                }

                var result = await _operationService.BufferAsync(
                    wkb,
                    parameters.Geodesic ? 4326 : bufferSrid,
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
        // The Esri /union operation returns a single `geometry`, not a `geometries`
        // array (union produces one merged geometry). The array shape broke the
        // ArcGIS Maps SDK for JavaScript, which reads result.geometry (singular).
        var geometryElement = _geometryConverter.ConvertWkbToGeoServicesGeometry(unionResult, parameters.SR);
        var response = new GeometryServiceGeometryResponse { Geometry = geometryElement };
        GeometryServiceLog.UnionOperationCompleted(_logger, parameters.GeometryJsonStrings.Length);
        scope.SetSuccess(1);
        return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceGeometryResponse, contentType: "application/json");
    }

    private async Task<IResult> HandleBinaryGeometryOperationAsync(
        HttpContext context,
        string operationName,
        Func<byte[], byte[], int, CancellationToken, Task<byte[]>> operation,
        CancellationToken ct,
        string operatorParameterName = "geometry")
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
                GeometryServiceRequestParser.GetValue(values, operatorParameterName),
                parameterName: operatorParameterName,
                maxGeometryJsonLength: requestLimits.MaxGeometryJsonLength);
            if (operatorError is not null || string.IsNullOrWhiteSpace(operatorGeometry))
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, operationName, operatorError ?? $"Missing {operatorParameterName}");
                return CreateError(context, 400, operatorError ?? $"Parameter '{operatorParameterName}' is required.");
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
