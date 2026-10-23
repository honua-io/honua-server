// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Authentication;
using Honua.Scene.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// gRPC elevation resolves a spatial reference in feature-query order and rejects one it cannot.
/// </summary>
public sealed class ElevationSpatialReferenceTests
{
    [UnitTest]
    public async Task GetElevation_LatestWkidWithoutWkid_SamplesThatSrid()
    {
        var elevation = new RecordingElevationService();
        var service = CreateService(elevation);

        var response = await service.GetElevation(PointRequest(new Proto.SpatialReference { LatestWkid = 3857 }), CreateCallContext());

        response.NoData.Should().BeTrue();
        elevation.PointSrid.Should().Be(3857);
    }

    [UnitTest]
    public async Task GetElevation_ExplicitWkidWithoutLatestWkid_SamplesThatSrid()
    {
        var elevation = new RecordingElevationService();
        var service = CreateService(elevation);

        await service.GetElevation(PointRequest(new Proto.SpatialReference { Wkid = 3857 }), CreateCallContext());

        elevation.PointSrid.Should().Be(3857);
    }

    [UnitTest]
    public async Task GetElevation_LatestWkidAndWkid_PrefersLatestWkid()
    {
        var elevation = new RecordingElevationService();
        var service = CreateService(elevation);

        await service.GetElevation(
            PointRequest(new Proto.SpatialReference { Wkid = 3857, LatestWkid = 4326 }),
            CreateCallContext());

        elevation.PointSrid.Should().Be(4326);
    }

    [UnitTest]
    public async Task GetElevation_KnownWkt_SamplesDetectedSrid()
    {
        var elevation = new RecordingElevationService();
        var service = CreateService(elevation);

        await service.GetElevation(
            PointRequest(new Proto.SpatialReference { Wkt = "PROJCS[\"Web Mercator\", AUTHORITY[\"EPSG\",\"3857\"]]" }),
            CreateCallContext());

        elevation.PointSrid.Should().Be(3857);
    }

    [UnitTest]
    public async Task GetElevation_UnresolvedWkt_RejectsInsteadOfSamplingWgs84()
    {
        var elevation = new RecordingElevationService();
        var service = CreateService(elevation);

        var act = () => service.GetElevation(
            PointRequest(new Proto.SpatialReference { Wkt = "LOCAL_CS[\"unknown\"]" }),
            CreateCallContext());

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        elevation.PointCalls.Should().Be(0);
    }

    [UnitTest]
    public async Task GetElevation_UnsupportedLatestWkid_DoesNotFallThroughToWkid()
    {
        var elevation = new RecordingElevationService();
        var service = CreateService(elevation);

        var act = () => service.GetElevation(
            PointRequest(new Proto.SpatialReference { Wkid = 4326, LatestWkid = 9999 }),
            CreateCallContext());

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        elevation.PointCalls.Should().Be(0);
    }

    [UnitTest]
    public async Task GetElevationProfile_LatestWkid_SamplesThatSrid()
    {
        var elevation = new RecordingElevationService();
        var service = CreateService(elevation);
        var request = new Proto.GetElevationProfileRequest
        {
            LayerId = 1,
            SpatialReference = new Proto.SpatialReference { LatestWkid = 3857 },
            SampleCount = 2,
        };
        var path = new Proto.CoordinateSequence();
        path.Coords.Add(new Proto.Coordinate { X = 1, Y = 2 });
        path.Coords.Add(new Proto.Coordinate { X = 3, Y = 4 });
        request.Line = new Proto.PolylineGeometry();
        request.Line.Paths.Add(path);

        await service.GetElevationProfile(request, CreateCallContext());

        elevation.ProfileSrid.Should().Be(3857);
    }

    private static HonuaElevationGrpcService CreateService(RecordingElevationService elevation)
        => new(
            elevation,
            new SupportedCrsRegistry(),
            new WktCrsDetection(),
            Options.Create(new LimitsOptions()),
            NullLogger<HonuaElevationGrpcService>.Instance);

    private static Proto.GetElevationRequest PointRequest(Proto.SpatialReference spatialReference)
        => new()
        {
            LayerId = 1,
            Point = new Proto.PointGeometry { X = 500_000, Y = 0 },
            SpatialReference = spatialReference,
        };

    private static TestServerCallContext CreateCallContext()
    {
        var provider = new TestMetadataV2GraphBuilder()
            .AddResource(
                "dem",
                "dem",
                MetadataV2ResourceType.RasterDataset,
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding("dem-binding", "dem", "dem", storageLayerId: 1)
            .AddService("imagery", "imagery", protocols: [ServiceProtocols.Elevation])
            .AddPublication("dem-publication", "imagery", "dem", layerIndex: 1, isPrimary: true)
            .BuildProvider();
        var services = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(provider)
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();
        var context = new TestServerCallContext();
        context.UserState["__HttpContext"] = new DefaultHttpContext
        {
            RequestServices = services,
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()),
        };
        return context;
    }

    private sealed class RecordingElevationService : IElevationService
    {
        public int? PointSrid { get; private set; }

        public int PointCalls { get; private set; }

        public int ProfileSrid { get; private set; }

        public Task<ElevationPointResult> QueryPointAsync(
            int layerId,
            double x,
            double y,
            int? srid,
            RasterMergeStrategy mergeStrategy,
            CancellationToken cancellationToken = default)
        {
            PointCalls++;
            PointSrid = srid;
            return Task.FromResult(new ElevationPointResult
            {
                NoData = true,
                OutOfBounds = true,
                LayerId = layerId,
                RasterIds = [],
                X = x,
                Y = y,
                QuerySrid = srid,
            });
        }

        public Task<ElevationProfileResult> QueryProfileAsync(
            int layerId,
            byte[] lineWkb,
            int lineSrid,
            ProfileSamplingOptions options,
            RasterMergeStrategy mergeStrategy,
            CancellationToken cancellationToken = default)
        {
            ProfileSrid = lineSrid;
            return Task.FromResult(new ElevationProfileResult
            {
                Samples = [],
                LineLengthMeters = 0,
                SampleCount = 0,
                LayerId = layerId,
                RasterIds = [],
                IsAllNoData = true,
            });
        }
    }

    private sealed class SupportedCrsRegistry : ICrsRegistry
    {
        public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CrsDefinition?>(null);

        public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CrsDefinition?>(null);

        public ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(srid is 4326 or 3857);
    }

    private sealed class WktCrsDetection : ICrsDetectionService
    {
        public Task<int?> DetectFromPrjAsync(string prjContent, CancellationToken cancellationToken = default)
            => Task.FromResult<int?>(null);

        public Task<int?> DetectFromWktAsync(string wktContent, CancellationToken cancellationToken = default)
            => Task.FromResult<int?>(wktContent.Contains("3857", StringComparison.Ordinal) ? 3857 : null);

        public int? DetectFromEpsgCode(string epsgCode) => null;

        public Task<int?> DetectFromGeoJsonCrsAsync(string crsObject, CancellationToken cancellationToken = default)
            => Task.FromResult<int?>(null);

        public Task<int?> DetectFromShapefilePrjAsync(string shapefilePath, CancellationToken cancellationToken = default)
            => Task.FromResult<int?>(null);

        public Task<bool> ValidateSridAsync(int srid, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly Metadata _responseTrailers = new();

        protected override string MethodCore => "/geospatial.v1.ElevationService/GetElevation";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => _responseTrailers;
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, []);

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }
}
