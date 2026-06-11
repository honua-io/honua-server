// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Tests.Features.Infrastructure.Services;

public sealed class SpatialReferenceResolverTests
{
    private readonly FakeCrsDetectionService _crsDetectionService = new();
    private readonly FakeCrsRegistry _crsRegistry = new();
    private readonly SpatialReferenceResolver _resolver;

    public SpatialReferenceResolverTests()
    {
        _resolver = new SpatialReferenceResolver(_crsDetectionService, _crsRegistry);
    }

    [Fact]
    public async Task ResolveSridAsync_JsonLatestWkid_ReturnsLatestWkid()
    {
        var srid = await _resolver.ResolveSridAsync("""{"latestWkid":3857}""", null);

        srid.Should().Be(3857);
    }

    [Fact]
    public async Task ResolveSridAsync_JsonName_ReturnsEpsgCode()
    {
        _crsDetectionService.EpsgLookup["EPSG:4326"] = 4326;

        var srid = await _resolver.ResolveSridAsync("""{"name":"EPSG:4326"}""", null);

        srid.Should().Be(4326);
    }

    [Fact]
    public async Task ResolveSridAsync_JsonWkt_ReturnsDetectedSrid()
    {
        _crsDetectionService.WktResult = 3857;

        var srid = await _resolver.ResolveSridAsync("""{"wkt":"GEOGCRS[\"WGS 84\"]"}""", null);

        srid.Should().Be(3857);
    }

    [Fact]
    public async Task ParseSridAsync_WktString_ForwardsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        _crsDetectionService.WktResult = 4326;

        var srid = await _resolver.ParseSridAsync("GEOGCS[\"WGS 84\"]", cts.Token);

        srid.Should().Be(4326);
        _crsDetectionService.LastCancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task ResolveSridAsync_WktInGeometrySpatialReference_ForwardsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        _crsDetectionService.WktResult = 3857;
        var spatialRef = new GeoServicesSpatialReference { Wkt = "PROJCRS[\"WGS 84 / Pseudo-Mercator\"]" };

        var srid = await _resolver.ResolveSridAsync(null, spatialRef, cts.Token);

        srid.Should().Be(3857);
        _crsDetectionService.LastCancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task ParseSridAsync_WktString_RespectsAlreadyCancelledToken()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await _resolver.ParseSridAsync("GEOGCS[\"WGS 84\"]", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FakeCrsRegistry : ICrsRegistry
    {
        public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CrsDefinition?>(null);

        public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CrsDefinition?>(null);

        public ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }

    private sealed class FakeCrsDetectionService : ICrsDetectionService
    {
        public Dictionary<string, int?> EpsgLookup { get; } = new(StringComparer.Ordinal);

        public int? WktResult { get; set; }

        /// <summary>The last <see cref="CancellationToken"/> passed to any async method.</summary>
        public CancellationToken LastCancellationToken { get; private set; }

        public Task<int?> DetectFromPrjAsync(string prjContent, CancellationToken cancellationToken = default)
        {
            LastCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<int?>(null);
        }

        public Task<int?> DetectFromWktAsync(string wktContent, CancellationToken cancellationToken = default)
        {
            LastCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(WktResult);
        }

        public int? DetectFromEpsgCode(string epsgCode)
            => EpsgLookup.TryGetValue(epsgCode, out var srid) ? srid : null;

        public Task<int?> DetectFromGeoJsonCrsAsync(string crsObject, CancellationToken cancellationToken = default)
        {
            LastCancellationToken = cancellationToken;
            return Task.FromResult<int?>(null);
        }

        public Task<int?> DetectFromShapefilePrjAsync(string shapefilePath, CancellationToken cancellationToken = default)
        {
            LastCancellationToken = cancellationToken;
            return Task.FromResult<int?>(null);
        }

        public Task<bool> ValidateSridAsync(int srid, CancellationToken cancellationToken = default)
        {
            LastCancellationToken = cancellationToken;
            return Task.FromResult(true);
        }
    }
}
