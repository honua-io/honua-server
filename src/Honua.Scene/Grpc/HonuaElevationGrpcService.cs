// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Raster;
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
internal sealed partial class HonuaElevationGrpcService : Proto.ElevationService.ElevationServiceBase
{
    private const int Wgs84 = 4326;

    private readonly IElevationService _elevationService;
    private readonly ICrsRegistry _crsRegistry;
    private readonly LimitsOptions _limits;
    private readonly ILogger<HonuaElevationGrpcService> _logger;

    public HonuaElevationGrpcService(
        IElevationService elevationService,
        ICrsRegistry crsRegistry,
        IOptions<LimitsOptions> limits,
        ILogger<HonuaElevationGrpcService> logger)
    {
        ArgumentNullException.ThrowIfNull(elevationService);
        ArgumentNullException.ThrowIfNull(crsRegistry);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(logger);

        _elevationService = elevationService;
        _crsRegistry = crsRegistry;
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
            Log.InvalidArgument(_logger, SceneGrpcTelemetry.GetElevationOperation, request.LayerId, "point is required.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "point is required."));
        }

        var x = request.Point.X;
        var y = request.Point.Y;
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            Log.InvalidArgument(_logger, SceneGrpcTelemetry.GetElevationOperation, request.LayerId, "point x and y must be finite values.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "point x and y must be finite values."));
        }

        var srid = await ValidateSridAsync(
            request.SpatialReference,
            SceneGrpcTelemetry.GetElevationOperation,
            request.LayerId,
            context.CancellationToken).ConfigureAwait(false);
        // Enforce per-layer read RBAC BEFORE querying, mirroring the HTTP
        // elevation surface (ElevationEndpoints.ValidateDatasetAsync). The shared
        // guard maps a protected layer to Unauthenticated/PermissionDenied and an
        // unknown layer to NotFound, and returns the authorized resource.
        var resource = await ElevationAccessGuard
            .EnforceReadAccessAsync(context, request.LayerId, context.CancellationToken)
            .ConfigureAwait(false);

        // Resolve the merge strategy through the shared RasterMosaicUtilities used
        // by the HTTP elevation path (ElevationEndpoints), so the request rule and
        // the dataset's configured default mergeStrategy resolve identically across
        // protocols (the prior protocol-local ParseMergeStrategy ignored both the
        // resource default and the canonical token aliases).
        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(resource, request.MosaicRule);

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
            Log.ElevationQueryFailed(_logger, SceneGrpcTelemetry.GetElevationOperation, request.LayerId, ex.Message);
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

        var lineString = BuildLineString(request.Line, request.LayerId);
        var srid = await ValidateSridAsync(
            request.SpatialReference,
            SceneGrpcTelemetry.GetElevationProfileOperation,
            request.LayerId,
            context.CancellationToken).ConfigureAwait(false) ?? Wgs84;

        var requestedSampleCount = request.SampleCount;

        // sample_count is "at least 2; zero uses the server default" per the
        // proto. Reject any non-zero value below 2 (including negatives) so a
        // malformed -1 is not silently coerced to the default — mirroring the
        // HTTP elevation endpoint, which rejects any supplied value < 2.
        if (requestedSampleCount != 0 && requestedSampleCount < 2)
        {
            Log.InvalidArgument(_logger, SceneGrpcTelemetry.GetElevationProfileOperation, request.LayerId, "sample_count must be at least 2.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "sample_count must be at least 2."));
        }

        if (requestedSampleCount > _limits.Elevation.MaxSampleCount)
        {
            Log.InvalidArgument(_logger, SceneGrpcTelemetry.GetElevationProfileOperation, request.LayerId, "sample_count exceeds the configured maximum.");
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

        // Enforce per-layer read RBAC BEFORE querying, mirroring the HTTP
        // elevation surface (ElevationEndpoints.ValidateDatasetAsync).
        var resource = await ElevationAccessGuard
            .EnforceReadAccessAsync(context, request.LayerId, context.CancellationToken)
            .ConfigureAwait(false);

        // Shared mosaic-rule resolution (see GetElevation for rationale).
        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(resource, request.MosaicRule);

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
            Log.ElevationQueryFailed(_logger, SceneGrpcTelemetry.GetElevationProfileOperation, request.LayerId, ex.Message);
            throw ToRpcException(ex);
        }
    }

    /// <summary>
    /// Resolves the requested input SRID from the proto spatial reference and
    /// validates it against the shared <see cref="ICrsRegistry"/>, mirroring the
    /// HTTP elevation surface (<c>ElevationEndpoints.ResolveSridAsync</c>) so an
    /// unsupported/garbage WKID is rejected with <see cref="StatusCode.InvalidArgument"/>
    /// before reaching provider code rather than diverging from the shared
    /// validation pipeline. A null/unset reference (Wkid &lt;= 0) defers to the
    /// canonical service default (WGS-84).
    /// </summary>
    private async Task<int?> ValidateSridAsync(
        Proto.SpatialReference? spatialReference,
        string operation,
        int layerId,
        CancellationToken cancellationToken)
    {
        if (spatialReference is not { Wkid: > 0 } reference)
        {
            return null;
        }

        var supported = await _crsRegistry
            .IsSridSupportedAsync(reference.Wkid, cancellationToken)
            .ConfigureAwait(false);
        if (!supported)
        {
            Log.UnsupportedCrs(_logger, operation, layerId, reference.Wkid);
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"spatial reference WKID {reference.Wkid} is not supported."));
        }

        return reference.Wkid;
    }

    /// <summary>
    /// Builds the geodesic-profile line from the proto polyline.
    /// </summary>
    /// <remarks>
    /// Single-path contract: a profile is sampled along ONE continuous polyline.
    /// The proto <c>PolylineGeometry</c> can carry multiple paths (a multi-part
    /// line), but a multi-part profile has no well-defined cumulative distance,
    /// so a request with more than one path is rejected with
    /// <see cref="StatusCode.InvalidArgument"/> rather than silently sampling
    /// only the first part. Callers that need several profiles must issue one
    /// request per path.
    /// </remarks>
    private LineString BuildLineString(Proto.PolylineGeometry? line, int layerId)
    {
        if (line is null || line.Paths.Count == 0)
        {
            Log.InvalidArgument(_logger, SceneGrpcTelemetry.GetElevationProfileOperation, layerId, "line with at least one path is required.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "line with at least one path is required."));
        }

        if (line.Paths.Count > 1)
        {
            Log.InvalidArgument(_logger, SceneGrpcTelemetry.GetElevationProfileOperation, layerId, "line must contain exactly one path; multi-part profiles are not supported.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "line must contain exactly one path; submit one request per path for a multi-part line."));
        }

        var path = line.Paths[0];
        if (path.Coords.Count < 2)
        {
            Log.InvalidArgument(_logger, SceneGrpcTelemetry.GetElevationProfileOperation, layerId, "line must contain at least 2 coordinates.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "line must contain at least 2 coordinates."));
        }

        var coordinates = new Coordinate[path.Coords.Count];
        for (var i = 0; i < path.Coords.Count; i++)
        {
            var coord = path.Coords[i];
            if (!double.IsFinite(coord.X) || !double.IsFinite(coord.Y))
            {
                Log.InvalidArgument(_logger, SceneGrpcTelemetry.GetElevationProfileOperation, layerId, "line coordinates must be finite values.");
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
        => new(new Status(MapFailureKind(exception.FailureKind), exception.Message));

    /// <summary>
    /// Maps an <see cref="ElevationFailureKind"/> to the gRPC status that mirrors
    /// the canonical HTTP mapping (ElevationEndpoints.MapElevationException), so a
    /// failure surfaces with consistent semantics across protocols. gRPC has no
    /// 422 equivalent, so the HTTP UnprocessableEntity cases (UnsupportedCrs /
    /// InvalidGeometry) collapse to <see cref="StatusCode.InvalidArgument"/>, which
    /// carries the same "client supplied an unacceptable request" meaning. A
    /// missing source maps to <see cref="StatusCode.NotFound"/> (HTTP 404) and any
    /// future/unknown kind maps to the safe default
    /// <see cref="StatusCode.InvalidArgument"/> (HTTP 400 BadRequest).
    /// </summary>
    private static StatusCode MapFailureKind(ElevationFailureKind failureKind)
        => failureKind switch
        {
            ElevationFailureKind.SourceUnavailable => StatusCode.NotFound,
            ElevationFailureKind.UnsupportedCrs => StatusCode.InvalidArgument,
            ElevationFailureKind.InvalidGeometry => StatusCode.InvalidArgument,
            _ => StatusCode.InvalidArgument
        };

    private static partial class Log
    {
        [LoggerMessage(EventId = 8440, Level = LogLevel.Debug,
            Message = "gRPC elevation {Operation} rejected layer {LayerId}: {Reason}")]
        public static partial void InvalidArgument(ILogger logger, string operation, int layerId, string reason);

        [LoggerMessage(EventId = 8441, Level = LogLevel.Debug,
            Message = "gRPC elevation {Operation} rejected layer {LayerId}: unsupported CRS WKID {Wkid}")]
        public static partial void UnsupportedCrs(ILogger logger, string operation, int layerId, int wkid);

        [LoggerMessage(EventId = 8442, Level = LogLevel.Debug,
            Message = "gRPC elevation {Operation} query failed for layer {LayerId}: {Reason}")]
        public static partial void ElevationQueryFailed(ILogger logger, string operation, int layerId, string reason);
    }
}
