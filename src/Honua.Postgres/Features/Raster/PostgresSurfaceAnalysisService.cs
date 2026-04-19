// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// PostGIS-backed implementation of canonical surface-analysis operations.
/// </summary>
internal sealed class PostgresSurfaceAnalysisService : ISurfaceAnalysisService
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresSurfaceAnalysisService> _logger;
    private readonly string _rasterDataTable;

    public PostgresSurfaceAnalysisService(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresSurfaceAnalysisService> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rasterDataTable = SchemaSearchPath.QualifyTable("raster_data", schemaName);
    }

    public Task<SurfaceAnalysisResult> ComputeSlopeAsync(
        SurfaceAnalysisRequest request,
        SlopeUnits units,
        double zFactor,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ValidatePositiveFinite(zFactor, nameof(zFactor));

        var unitLabel = units switch
        {
            SlopeUnits.Degrees => "DEGREES",
            SlopeUnits.Percent => "PERCENT",
            SlopeUnits.Radians => "RADIANS",
            _ => throw new ArgumentOutOfRangeException(nameof(units), units, "Unsupported slope units.")
        };

        return ExecuteDerivedRasterAsync(
            request,
            operation: "slope",
            rasterExpression: "ST_Slope(raster, 1, '32BF', @units, @zFactor, FALSE)",
            parameters:
            [
                ("@units", unitLabel),
                ("@zFactor", zFactor),
            ],
            cancellationToken);
    }

    public Task<SurfaceAnalysisResult> ComputeAspectAsync(
        SurfaceAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        return ExecuteDerivedRasterAsync(
            request,
            operation: "aspect",
            rasterExpression: "ST_Aspect(raster, 1, '32BF', 'DEGREES', FALSE)",
            parameters: [],
            cancellationToken);
    }

    public Task<SurfaceAnalysisResult> ComputeHillshadeAsync(
        SurfaceAnalysisRequest request,
        double azimuthDegrees,
        double altitudeDegrees,
        double zFactor,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ValidateClosedRange(azimuthDegrees, 0d, 360d, nameof(azimuthDegrees));
        ValidateClosedRange(altitudeDegrees, 0d, 90d, nameof(altitudeDegrees));
        ValidatePositiveFinite(zFactor, nameof(zFactor));

        return ExecuteDerivedRasterAsync(
            request,
            operation: "hillshade",
            rasterExpression: "ST_HillShade(raster, 1, '32BF', @azimuth, @altitude, 255, @zFactor, FALSE)",
            parameters:
            [
                ("@azimuth", azimuthDegrees),
                ("@altitude", altitudeDegrees),
                ("@zFactor", zFactor),
            ],
            cancellationToken);
    }

    public Task<SurfaceAnalysisResult> ComputeRugosityAsync(
        SurfaceAnalysisRequest request,
        RugosityMethod method,
        int windowRadius,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        if (windowRadius != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowRadius),
                windowRadius,
                "The canonical PostGIS-backed surface runtime currently supports only a window radius of 1.");
        }

        var (operation, expression) = method switch
        {
            RugosityMethod.TerrainRuggednessIndex => ("rugosity-tri", "ST_TRI(raster, 1, NULL::raster, '32BF', FALSE)"),
            RugosityMethod.TopographicPositionIndex => ("rugosity-tpi", "ST_TPI(raster, 1, NULL::raster, '32BF', FALSE)"),
            RugosityMethod.Roughness => ("roughness", "ST_Roughness(raster, 1, NULL::raster, '32BF', FALSE)"),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported rugosity method.")
        };

        return ExecuteDerivedRasterAsync(
            request,
            operation,
            expression,
            parameters: [],
            cancellationToken);
    }

    private async Task<SurfaceAnalysisResult> ExecuteDerivedRasterAsync(
        SurfaceAnalysisRequest request,
        string operation,
        string rasterExpression,
        IReadOnlyList<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH src AS (
                SELECT raster
                FROM {_rasterDataTable}
                WHERE layer_id = @sourceLayerId AND id = @sourceRasterId
            )
            INSERT INTO {_rasterDataTable} (layer_id, name, description, raster)
            SELECT @outputLayerId,
                   @outputName,
                   @description,
                   {rasterExpression}
            FROM src
            RETURNING id, layer_id, width, height, srid
            """;
        AddParameter(command, "@sourceLayerId", request.SourceLayerId);
        AddParameter(command, "@sourceRasterId", request.SourceRasterId);
        AddParameter(command, "@outputLayerId", request.OutputLayerId);
        AddParameter(command, "@outputName", request.OutputName.Trim());
        AddParameter(command, "@description", $"Derived {operation} raster generated by the canonical surface runtime.");
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Raster {request.SourceRasterId} was not found in layer {request.SourceLayerId}.");
        }

        var result = new SurfaceAnalysisResult
        {
            RasterId = reader.GetInt64(0),
            LayerId = reader.GetInt32(1),
            Width = reader.GetInt32(2),
            Height = reader.GetInt32(3),
            Srid = reader.IsDBNull(4) ? null : reader.GetInt32(4),
        };

        PostgresRasterLog.SurfaceRasterComputed(
            _logger,
            operation,
            request.SourceLayerId,
            request.SourceRasterId,
            result.LayerId,
            result.RasterId);

        return result;
    }

    private static void ValidateRequest(SurfaceAnalysisRequest request)
    {
        if (request.SourceLayerId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.SourceLayerId, "Source layer id must be non-negative.");
        }

        if (request.SourceRasterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.SourceRasterId, "Source raster id must be positive.");
        }

        if (request.OutputLayerId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.OutputLayerId, "Output layer id must be non-negative.");
        }

        if (string.IsNullOrWhiteSpace(request.OutputName))
        {
            throw new ArgumentException("Output raster name is required.", nameof(request));
        }
    }

    private static void ValidatePositiveFinite(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be a finite number greater than zero.");
        }
    }

    private static void ValidateClosedRange(double value, double minimum, double maximum, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be between {minimum} and {maximum}.");
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
        => command.AddParameter(name, value);
}
