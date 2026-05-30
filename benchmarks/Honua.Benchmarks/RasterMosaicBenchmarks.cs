// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Attributes;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Raster;

namespace Honua.Benchmarks;

/// <summary>
/// Raster mosaic + tiling fast-path benchmarks. Both <see cref="RasterMosaicUtilities.ResolveMergeStrategy"/>
/// and <see cref="TileMath"/> are pure, allocation-light helpers that run on
/// every ImageServer export / identify / tile / statistics request and on
/// every map-tile materialisation respectively. We benchmark the three merge
/// strategy resolution shapes (token, JSON document, fallback) and the two
/// tile-bounds projections (Web Mercator and WorldCRS84Quad) so regressions
/// in argument parsing or coordinate math surface in CI.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.Raster, Categories.Tile)]
public class RasterMosaicBenchmarks
{
    // A small spread of zoom levels keeps the input deterministic while
    // exercising the high-zoom branch of the power-of-two math, which is
    // where Math.Pow domination shows up on the profile.
    private static readonly (int X, int Y, int Z)[] _tiles =
    {
        (0, 0, 0),
        (1, 1, 1),
        (10, 12, 5),
        (1023, 511, 10),
        (524287, 524287, 19),
    };

    private MetadataV2Resource _resourceMergeNewest = null!;
    private MetadataV2Resource _resourceMergeMax = null!;
    private string _jsonMergeRule = null!;
    private string _jsonOperationRule = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Layer metadata with a configured token strategy — the common
        // ImageServer hot path when no per-request mosaicRule is supplied.
        _resourceMergeNewest = CreateRasterResource("raster-newest", "newest");

        // Layer metadata that falls through to JSON-shaped overrides; covers
        // the heavier System.Text.Json branch that Esri-style clients hit.
        _resourceMergeMax = CreateRasterResource("raster-max", "max");

        // Per-request mosaicRule shapes used by ArcGIS clients:
        // either a `mergeStrategy` field or an `operation` field on a JSON
        // document. Both are unwrapped before the token switch.
        _jsonMergeRule = "{\"mergeStrategy\":\"mean\"}";
        _jsonOperationRule = "{\"operation\":\"mt_first\",\"ascending\":true}";
    }

    [Benchmark(Baseline = true, Description = "ResolveMergeStrategy token (metadata-only)")]
    public RasterMergeStrategy ResolveFromMetadataToken()
        => RasterMosaicUtilities.ResolveMergeStrategy(_resourceMergeNewest, mosaicRule: null);

    [Benchmark(Description = "ResolveMergeStrategy token override (request wins)")]
    public RasterMergeStrategy ResolveFromRequestToken()
        => RasterMosaicUtilities.ResolveMergeStrategy(_resourceMergeNewest, mosaicRule: "average");

    [Benchmark(Description = "ResolveMergeStrategy JSON mergeStrategy field")]
    public RasterMergeStrategy ResolveFromJsonMergeStrategy()
        => RasterMosaicUtilities.ResolveMergeStrategy(_resourceMergeMax, mosaicRule: _jsonMergeRule);

    [Benchmark(Description = "ResolveMergeStrategy JSON operation field")]
    public RasterMergeStrategy ResolveFromJsonOperation()
        => RasterMosaicUtilities.ResolveMergeStrategy(_resourceMergeMax, mosaicRule: _jsonOperationRule);

    [Benchmark(Description = "TileMath.GetTileBounds (WebMercator) x5 zooms")]
    public double TileBoundsWebMercator()
    {
        // Sum into a sink the JIT cannot elide so the bounds math actually
        // executes for each tile; mirrors the per-frame work in raster tile
        // dispatch where bounds are recomputed per request.
        double sink = 0;
        foreach (var (x, y, z) in _tiles)
        {
            var bounds = TileMath.GetTileBounds(x, y, z);
            sink += bounds.XMin + bounds.YMin + bounds.XMax + bounds.YMax;
        }

        return sink;
    }

    [Benchmark(Description = "TileMath.GetTileBoundsGeographic (CRS84Quad) x5 zooms")]
    public double TileBoundsGeographic()
    {
        double sink = 0;
        foreach (var (x, y, z) in _tiles)
        {
            var bounds = TileMath.GetTileBoundsGeographic(x, y, z);
            sink += bounds.XMin + bounds.YMin + bounds.XMax + bounds.YMax;
        }

        return sink;
    }

    private static MetadataV2Resource CreateRasterResource(string id, string mergeStrategy)
    {
        var rasterMosaic = JsonSerializer.Deserialize<JsonElement>($$"""{"mergeStrategy":"{{mergeStrategy}}"}""");

        return new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = id,
                Name = id
            },
            Type = MetadataV2ResourceType.RasterDataset,
            Extensions = new Dictionary<string, JsonElement>
            {
                ["rasterMosaic"] = rasterMosaic
            }
        };
    }
}
