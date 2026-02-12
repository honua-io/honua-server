// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Server.Features.GeometryService.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.GeometryService.Services;

/// <summary>
/// Orchestrates geometry service operations: parse input, invoke PostGIS, format output.
/// </summary>
internal sealed class GeometryServiceHandler(
    IGeometryOperationService operationService,
    IGeometryConverter geometryConverter,
    ILogger<GeometryServiceHandler> logger)
{
    private readonly IGeometryOperationService _operationService = operationService
        ?? throw new ArgumentNullException(nameof(operationService));
    private readonly IGeometryConverter _geometryConverter = geometryConverter
        ?? throw new ArgumentNullException(nameof(geometryConverter));
    private readonly ILogger<GeometryServiceHandler> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task<IResult> HandleBufferAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "buffer", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", parseError ?? "No parameters");
                return CreateError(400, parseError ?? "Request parameters are required.");
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(400, formatError);
            }

            // Parse geometries
            var (geomStrings, geomType, geomError) = GeometryServiceRequestParser.ParseGeometries(
                GeometryServiceRequestParser.GetValue(values, "geometries"));
            if (geomError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", geomError);
                return CreateError(400, geomError);
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", "No geometries provided");
                return CreateError(400, "Parameter 'geometries' must contain at least one geometry.");
            }

            // Parse spatial references
            var (inSr, inSrError) = GeometryServiceRequestParser.ParseSpatialReference(
                GeometryServiceRequestParser.GetValue(values, "inSR"));
            if (inSrError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", inSrError);
                return CreateError(400, inSrError);
            }

            if (inSr <= 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", "Invalid inSR");
                return CreateError(400, "Parameter 'inSR' must be a valid spatial reference WKID.");
            }

            var (outSr, outSrError) = GeometryServiceRequestParser.ParseSpatialReference(
                GeometryServiceRequestParser.GetValue(values, "outSR"));
            if (outSrError is not null)
            {
                return CreateError(400, outSrError);
            }

            var (bufferSr, bufferSrError) = GeometryServiceRequestParser.ParseSpatialReference(
                GeometryServiceRequestParser.GetValue(values, "bufferSR"));
            if (bufferSrError is not null)
            {
                return CreateError(400, bufferSrError);
            }

            // Parse distances
            var (distances, distError) = GeometryServiceRequestParser.ParseDoubleArray(
                GeometryServiceRequestParser.GetValue(values, "distances"));
            if (distError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", distError);
                return CreateError(400, distError);
            }

            if (distances.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", "No distances provided");
                return CreateError(400, "Parameter 'distances' is required and must contain at least one value.");
            }

            var unit = GeometryServiceRequestParser.GetValue(values, "unit");
            var unionResults = GeometryServiceRequestParser.ParseBool(
                GeometryServiceRequestParser.GetValue(values, "unionResults"));
            var geodesic = GeometryServiceRequestParser.ParseBool(
                GeometryServiceRequestParser.GetValue(values, "geodesic"));

            var parameters = new BufferParameters
            {
                GeometryJsonStrings = geomStrings,
                GeometryType = geomType,
                InSR = inSr,
                OutSR = outSr > 0 ? outSr : null,
                BufferSR = bufferSr > 0 ? bufferSr : null,
                Distances = distances,
                Unit = unit,
                UnionResults = unionResults,
                Geodesic = geodesic
            };

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
            return CreateError(400, ex.Message);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "buffer", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(500, "An internal error occurred during the buffer operation.");
        }
    }

    public async Task<IResult> HandleSimplifyAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "simplify", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", parseError ?? "No parameters");
                return CreateError(400, parseError ?? "Request parameters are required.");
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(400, formatError);
            }

            // Parse geometries
            var (geomStrings, geomType, geomError) = GeometryServiceRequestParser.ParseGeometries(
                GeometryServiceRequestParser.GetValue(values, "geometries"));
            if (geomError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", geomError);
                return CreateError(400, geomError);
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", "No geometries provided");
                return CreateError(400, "Parameter 'geometries' must contain at least one geometry.");
            }

            // ArcGIS simplify uses "sr" not "inSR"
            var (sr, srError) = GeometryServiceRequestParser.ParseSpatialReference(
                GeometryServiceRequestParser.GetValue(values, "sr"));
            if (srError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", srError);
                return CreateError(400, srError);
            }

            if (sr <= 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", "Invalid sr");
                return CreateError(400, "Parameter 'sr' must be a valid spatial reference WKID.");
            }

            var parameters = new SimplifyParameters
            {
                GeometryJsonStrings = geomStrings,
                GeometryType = geomType,
                SR = sr
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
            return CreateError(400, ex.Message);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "simplify", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(500, "An internal error occurred during the simplify operation.");
        }
    }

    public async Task<IResult> HandleProjectAsync(HttpContext context, CancellationToken ct)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "project", HonuaTelemetry.Protocols.GeometryService, "geometry");

        try
        {
            var (values, parseError) = await GeometryServiceRequestParser.TryReadRequestValuesAsync(context.Request, ct);
            if (values is null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "project", parseError ?? "No parameters");
                return CreateError(400, parseError ?? "Request parameters are required.");
            }

            var formatError = GeometryServiceRequestParser.ValidateFormat(
                GeometryServiceRequestParser.GetValue(values, "f"));
            if (formatError is not null)
            {
                return CreateError(400, formatError);
            }

            // Parse geometries
            var (geomStrings, geomType, geomError) = GeometryServiceRequestParser.ParseGeometries(
                GeometryServiceRequestParser.GetValue(values, "geometries"));
            if (geomError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "project", geomError);
                return CreateError(400, geomError);
            }

            if (geomStrings.Length == 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "project", "No geometries provided");
                return CreateError(400, "Parameter 'geometries' must contain at least one geometry.");
            }

            // Parse spatial references
            var (inSr, inSrError) = GeometryServiceRequestParser.ParseSpatialReference(
                GeometryServiceRequestParser.GetValue(values, "inSR"));
            if (inSrError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "project", inSrError);
                return CreateError(400, inSrError);
            }

            if (inSr <= 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "project", "Invalid inSR");
                return CreateError(400, "Parameter 'inSR' must be a valid spatial reference WKID.");
            }

            var (outSr, outSrError) = GeometryServiceRequestParser.ParseSpatialReference(
                GeometryServiceRequestParser.GetValue(values, "outSR"));
            if (outSrError is not null)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "project", outSrError);
                return CreateError(400, outSrError);
            }

            if (outSr <= 0)
            {
                GeometryServiceLog.InvalidGeometryInput(_logger, "project", "Invalid outSR");
                return CreateError(400, "Parameter 'outSR' must be a valid spatial reference WKID.");
            }

            var parameters = new ProjectParameters
            {
                GeometryJsonStrings = geomStrings,
                GeometryType = geomType,
                InSR = inSr,
                OutSR = outSr
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
            return CreateError(400, ex.Message);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "project", ex.Message, ex);
            scope.RecordException(ex);
            return CreateError(500, "An internal error occurred during the project operation.");
        }
    }

    private async Task<IResult> ExecuteBufferAsync(BufferParameters parameters, HonuaTelemetryScope scope, CancellationToken ct)
    {
        var unitMultiplier = GeometryServiceRequestParser.GetUnitMultiplier(parameters.Unit);

        // bufferSR cascade: buffer in bufferSR ?? outSR ?? inSR, then project to outSR ?? inSR
        var bufferSrid = parameters.BufferSR ?? parameters.OutSR ?? parameters.InSR;
        var outputSrid = parameters.OutSR ?? parameters.InSR;
        var bufferedGeometries = new List<byte[]>();

        for (var i = 0; i < parameters.GeometryJsonStrings.Length; i++)
        {
            var distanceIndex = i < parameters.Distances.Length ? i : parameters.Distances.Length - 1;
            var distance = parameters.Distances[distanceIndex] * unitMultiplier;

            var wkb = _geometryConverter.ConvertGeoServicesJsonToWkb(parameters.GeometryJsonStrings[i]);

            // Project to bufferSR if needed before buffering (non-geodesic only)
            if (!parameters.Geodesic && bufferSrid != parameters.InSR)
            {
                wkb = await _operationService.ProjectAsync(wkb, parameters.InSR, bufferSrid, ct).ConfigureAwait(false);
            }

            var result = await _operationService.BufferAsync(
                wkb, parameters.Geodesic ? parameters.InSR : bufferSrid, distance, parameters.Geodesic, ct).ConfigureAwait(false);

            // Project to output SR. For geodesic, the result is in SRID 4326.
            var resultSrid = parameters.Geodesic ? 4326 : bufferSrid;
            if (resultSrid != outputSrid)
            {
                result = await _operationService.ProjectAsync(result, resultSrid, outputSrid, ct).ConfigureAwait(false);
            }

            bufferedGeometries.Add(result);
        }

        if (parameters.UnionResults && bufferedGeometries.Count > 1)
        {
            var unionResult = await _operationService.UnionAsync(
                bufferedGeometries.ToArray(), outputSrid, ct).ConfigureAwait(false);
            bufferedGeometries = [unionResult];
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
            Geometries = geometryElements
        };
    }

    private static IResult CreateError(int code, string message)
    {
        var errorResponse = new GeometryServiceErrorResponse
        {
            Error = new GeometryServiceError { Code = code, Message = message }
        };
        return Results.Json(
            errorResponse,
            GeometryServiceJsonContext.Default.GeometryServiceErrorResponse,
            contentType: "application/json",
            statusCode: code);
    }
}
