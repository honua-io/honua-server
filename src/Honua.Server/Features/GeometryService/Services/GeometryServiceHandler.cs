// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Server.Features.GeometryService.Models;
using Honua.Server.Features.Infrastructure.Services;

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

    /// <summary>
    /// Unit conversion factors to meters.
    /// </summary>
    private static readonly Dictionary<string, double> UnitConversions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esriMeters"] = 1.0,
        ["esriFeet"] = 0.3048,
        ["esriKilometers"] = 1000.0,
        ["esriMiles"] = 1609.344,
        ["esriNauticalMiles"] = 1852.0,
        ["esriYards"] = 0.9144
    };

    /// <summary>
    /// Handles a buffer request.
    /// </summary>
    public async Task<IResult> HandleBufferAsync(BufferRequest request, CancellationToken ct)
    {
        if (request.Geometries == null || request.Geometries.Length == 0)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", "No geometries provided");
            return CreateError(400, "Parameter 'geometries' is required and must contain at least one geometry.");
        }

        if (request.Distances == null || request.Distances.Length == 0)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", "No distances provided");
            return CreateError(400, "Parameter 'distances' is required and must contain at least one value.");
        }

        if (request.InSR <= 0)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", "Invalid inSR");
            return CreateError(400, "Parameter 'inSR' must be a valid spatial reference WKID.");
        }

        try
        {
            var unitMultiplier = GetUnitMultiplier(request.Unit);
            var outSrid = request.OutSR ?? request.InSR;
            var bufferedGeometries = new List<byte[]>();

            for (var i = 0; i < request.Geometries.Length; i++)
            {
                var distanceIndex = i < request.Distances.Length ? i : request.Distances.Length - 1;
                var distance = request.Distances[distanceIndex] * unitMultiplier;

                var wkb = _geometryConverter.ConvertGeoServicesJsonToWkb(request.Geometries[i].GetRawText());
                var result = await _operationService.BufferAsync(wkb, request.InSR, distance, request.Geodesic, ct).ConfigureAwait(false);

                // Project to output SR if different
                if (outSrid != request.InSR)
                {
                    // Buffer output is in 4326 when geodesic, otherwise in inSR
                    var bufferSrid = request.Geodesic ? 4326 : request.InSR;
                    result = await _operationService.ProjectAsync(result, bufferSrid, outSrid, ct).ConfigureAwait(false);
                }

                bufferedGeometries.Add(result);
            }

            if (request.UnionResults && bufferedGeometries.Count > 1)
            {
                var unionResult = await _operationService.UnionAsync(
                    bufferedGeometries.ToArray(), outSrid, ct).ConfigureAwait(false);
                bufferedGeometries = [unionResult];
            }

            var response = ConvertToResponse(bufferedGeometries, outSrid);
            GeometryServiceLog.BufferOperationCompleted(
                _logger, request.Geometries.Length, request.Distances[0], request.Geodesic);
            return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceResponse, contentType: "application/json");
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "buffer", ex.Message);
            return CreateError(400, ex.Message);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "buffer", ex.Message, ex);
            return CreateError(500, "An internal error occurred during the buffer operation.");
        }
    }

    /// <summary>
    /// Handles a simplify request.
    /// </summary>
    public async Task<IResult> HandleSimplifyAsync(SimplifyRequest request, CancellationToken ct)
    {
        if (request.Geometries == null || request.Geometries.Length == 0)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", "No geometries provided");
            return CreateError(400, "Parameter 'geometries' is required and must contain at least one geometry.");
        }

        if (request.MaxDeviation <= 0)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", "Invalid maxDeviation");
            return CreateError(400, "Parameter 'maxDeviation' must be a positive number.");
        }

        if (request.InSR <= 0)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", "Invalid inSR");
            return CreateError(400, "Parameter 'inSR' must be a valid spatial reference WKID.");
        }

        try
        {
            var unitMultiplier = GetUnitMultiplier(request.DeviationUnit);
            var tolerance = request.MaxDeviation * unitMultiplier;
            var outSrid = request.OutSR ?? request.InSR;
            var simplifiedGeometries = new List<byte[]>();

            foreach (var geomElement in request.Geometries)
            {
                var wkb = _geometryConverter.ConvertGeoServicesJsonToWkb(geomElement.GetRawText());
                var result = await _operationService.SimplifyAsync(wkb, tolerance, preserveTopology: true, ct).ConfigureAwait(false);

                if (outSrid != request.InSR)
                {
                    result = await _operationService.ProjectAsync(result, request.InSR, outSrid, ct).ConfigureAwait(false);
                }

                simplifiedGeometries.Add(result);
            }

            var response = ConvertToResponse(simplifiedGeometries, outSrid);
            GeometryServiceLog.SimplifyOperationCompleted(_logger, request.Geometries.Length, request.MaxDeviation);
            return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceResponse, contentType: "application/json");
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "simplify", ex.Message);
            return CreateError(400, ex.Message);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "simplify", ex.Message, ex);
            return CreateError(500, "An internal error occurred during the simplify operation.");
        }
    }

    /// <summary>
    /// Handles a project request.
    /// </summary>
    public async Task<IResult> HandleProjectAsync(ProjectRequest request, CancellationToken ct)
    {
        if (request.Geometries == null || request.Geometries.Length == 0)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "project", "No geometries provided");
            return CreateError(400, "Parameter 'geometries' is required and must contain at least one geometry.");
        }

        if (request.InSR <= 0)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "project", "Invalid inSR");
            return CreateError(400, "Parameter 'inSR' must be a valid spatial reference WKID.");
        }

        if (request.OutSR <= 0)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "project", "Invalid outSR");
            return CreateError(400, "Parameter 'outSR' must be a valid spatial reference WKID.");
        }

        try
        {
            var projectedGeometries = new List<byte[]>();

            foreach (var geomElement in request.Geometries)
            {
                var wkb = _geometryConverter.ConvertGeoServicesJsonToWkb(geomElement.GetRawText());
                var result = await _operationService.ProjectAsync(wkb, request.InSR, request.OutSR, ct).ConfigureAwait(false);
                projectedGeometries.Add(result);
            }

            var response = ConvertToResponse(projectedGeometries, request.OutSR);
            GeometryServiceLog.ProjectOperationCompleted(_logger, request.Geometries.Length, request.InSR, request.OutSR);
            return Results.Json(response, GeometryServiceJsonContext.Default.GeometryServiceResponse, contentType: "application/json");
        }
        catch (ArgumentException ex)
        {
            GeometryServiceLog.InvalidGeometryInput(_logger, "project", ex.Message);
            return CreateError(400, ex.Message);
        }
        catch (Exception ex)
        {
            GeometryServiceLog.GeometryOperationFailed(_logger, "project", ex.Message, ex);
            return CreateError(500, "An internal error occurred during the project operation.");
        }
    }

    private GeometryServiceResponse ConvertToResponse(List<byte[]> wkbs, int srid)
    {
        var geometryElements = new JsonElement[wkbs.Count];

        for (var i = 0; i < wkbs.Count; i++)
        {
            var geoServicesGeometry = _geometryConverter.ConvertWkbToGeoServicesGeometry(wkbs[i], srid);
            var json = JsonSerializer.Serialize(geoServicesGeometry, GeometryServiceJsonContext.Default.JsonElement);
            geometryElements[i] = JsonSerializer.Deserialize<JsonElement>(json);
        }

        return new GeometryServiceResponse { Geometries = geometryElements };
    }

    private static double GetUnitMultiplier(string? unit)
    {
        if (string.IsNullOrEmpty(unit))
        {
            return 1.0;
        }

        return UnitConversions.TryGetValue(unit, out var multiplier) ? multiplier : 1.0;
    }

    private static IResult CreateError(int code, string message)
    {
        var errorResponse = new GeometryServiceErrorResponse
        {
            Error = new GeometryServiceError
            {
                Code = code,
                Message = message
            }
        };

        return Results.Json(
            errorResponse,
            GeometryServiceJsonContext.Default.GeometryServiceErrorResponse,
            contentType: "application/json",
            statusCode: code);
    }
}
