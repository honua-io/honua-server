// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Proto = Geospatial.V1;

namespace Honua.Scene.Grpc;

/// <summary>
/// gRPC <c>ElevationService</c> implementation (honua-server#1194 / #1195).
/// Mirrors the HTTP elevation API: point sampling and geodesic profile
/// sampling against a registered raster layer, honoring a mosaic rule and
/// reporting no-data / out-of-bounds conditions explicitly.
/// </summary>
/// <remarks>
/// Intentional divergence: the canonical elevation service
/// (<see cref="IElevationService"/>) is keyed exclusively by the integer
/// publication layer index, so <c>layer_id</c> is the authoritative resolution
/// key for this adapter. The proto <c>dataset_id</c> field is advisory/echo-only
/// here — it carries the caller's human-readable dataset reference but does not
/// participate in resolution and is not used to scope the query. Callers that
/// only know a dataset string must resolve it to a layer index out-of-band
/// (the HTTP surface performs that name-to-index resolution itself). This
/// matches CLAUDE.md's requirement to document intentional protocol divergence
/// in code; the divergence is covered by the elevation gRPC tests.
/// </remarks>
internal sealed class HonuaElevationGrpcService : Proto.ElevationService.ElevationServiceBase
{
    private const int Wgs84 = 4326;

    private readonly IElevationService _elevationService;
    private readonly LimitsOptions _limits;
    private readonly ILogger<HonuaElevationGrpcService> _logger;

    public HonuaElevationGrpcService(
        IElevationService elevationService,
        IOptions<LimitsOptions> limits,
        ILogger<HonuaElevationGrpcService> logger)
    {
        ArgumentNullException.ThrowIfNull(elevationService);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(logger);

        _elevationService = elevationService;
        _limits = limits.Value;
        _logger = logger;
    }

    public override async Task<Proto.GetElevationResponse> GetElevation(
        Proto.GetElevationRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        using var activity = HonuaTelemetry.StartActivity(SceneGrpcTelemetry.GetElevationActivity);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Grpc);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, SceneGrpcTelemetry.GetElevationOperation);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, request.LayerId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (request.Point is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "point is required."));
        }

        var x = request.Point.X;
        var y = request.Point.Y;
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "point x and y must be finite values."));
        }

        var srid = ResolveSrid(request.SpatialReference);
        var mergeStrategy = SceneGrpcMapping.ParseMergeStrategy(request.MosaicRule);

        try
        {
            // layer_id is authoritative; request.DatasetId is advisory/echo-only
            // (see the class remarks) and is deliberately not used to resolve.
            var result = await _elevationService
                .QueryPointAsync(request.LayerId, x, y, srid, mergeStrategy, context.CancellationToken)
                .ConfigureAwait(false);
            return SceneGrpcMapping.ToElevationResponse(result, mergeStrategy);
        }
        catch (ElevationQueryException ex)
        {
            throw ToRpcException(ex);
        }
    }

    public override async Task<Proto.GetElevationProfileResponse> GetElevationProfile(
        Proto.GetElevationProfileRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        using var activity = HonuaTelemetry.StartActivity(SceneGrpcTelemetry.GetElevationProfileActivity);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Grpc);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, SceneGrpcTelemetry.GetElevationProfileOperation);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, request.LayerId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var lineString = BuildLineString(request.Line);
        var srid = ResolveSrid(request.SpatialReference) ?? Wgs84;
        var mergeStrategy = SceneGrpcMapping.ParseMergeStrategy(request.MosaicRule);

        var requestedSampleCount = request.SampleCount;

        // sample_count is "at least 2; zero uses the server default" per the
        // proto. Reject any non-zero value below 2 (including negatives) so a
        // malformed -1 is not silently coerced to the default — mirroring the
        // HTTP elevation endpoint, which rejects any supplied value < 2.
        if (requestedSampleCount != 0 && requestedSampleCount < 2)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "sample_count must be at least 2."));
        }

        if (requestedSampleCount > _limits.Elevation.MaxSampleCount)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"sample_count ({requestedSampleCount}) exceeds the configured maximum of {_limits.Elevation.MaxSampleCount}."));
        }

        var samplingOptions = new ProfileSamplingOptions
        {
            SampleCount = requestedSampleCount > 0 ? requestedSampleCount : null,
            IntervalMeters = null,
            DefaultSampleCount = _limits.Elevation.DefaultSampleCount,
            MaxSampleCount = _limits.Elevation.MaxSampleCount,
        };

        try
        {
            // layer_id is authoritative; request.DatasetId is advisory/echo-only
            // (see the class remarks) and is deliberately not used to resolve.
            var result = await _elevationService
                .QueryProfileAsync(request.LayerId, WriteLineWkb(lineString), srid, samplingOptions, mergeStrategy, context.CancellationToken)
                .ConfigureAwait(false);
            activity?.SetTag(SceneGrpcTelemetry.SampleCountTag, result.SampleCount);
            return SceneGrpcMapping.ToElevationProfileResponse(result, mergeStrategy);
        }
        catch (ElevationQueryException ex)
        {
            throw ToRpcException(ex);
        }
    }

    private static int? ResolveSrid(Proto.SpatialReference? spatialReference)
        => spatialReference is { Wkid: > 0 } reference ? reference.Wkid : null;

    private static LineString BuildLineString(Proto.PolylineGeometry? line)
    {
        if (line is null || line.Paths.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "line with at least one path is required."));
        }

        var path = line.Paths[0];
        if (path.Coords.Count < 2)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "line must contain at least 2 coordinates."));
        }

        var coordinates = new Coordinate[path.Coords.Count];
        for (var i = 0; i < path.Coords.Count; i++)
        {
            var coord = path.Coords[i];
            if (!double.IsFinite(coord.X) || !double.IsFinite(coord.Y))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "line coordinates must be finite values."));
            }

            coordinates[i] = new Coordinate(coord.X, coord.Y);
        }

        return new LineString(coordinates);
    }

    private static byte[] WriteLineWkb(LineString lineString)
    {
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: false, emitM: false);
        return writer.Write(lineString);
    }

    private static RpcException ToRpcException(ElevationQueryException exception)
        => new(new Status(StatusCode.FailedPrecondition, exception.Message));
}
