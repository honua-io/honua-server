// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Honua.Core.Features.Raster.CogParser;
using Honua.Infrastructure.Rendering;
using SkiaSharp;
using OldRendering = Baseline.Honua.Infrastructure.Rendering;
using OldRaster = Baseline.Honua.Core.Features.Raster.CogParser;

BenchmarkSwitcher.FromAssembly(typeof(PointDecoding).Assembly).Run(args);

[MemoryDiagnoser]
[ShortRunJob]
public class PointDecoding
{
    private readonly byte[] _point = new byte[21];
    private static readonly Func<double, double, SKPoint> _transform = static (x, y) => new((float)x, (float)y);

    [Params(false, true)]
    public bool LittleEndian { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _point[0] = LittleEndian ? (byte)1 : (byte)0;
        if (LittleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_point.AsSpan(1), 1);
            BinaryPrimitives.WriteDoubleLittleEndian(_point.AsSpan(5), 12.25);
            BinaryPrimitives.WriteDoubleLittleEndian(_point.AsSpan(13), -45.5);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(_point.AsSpan(1), 1);
            BinaryPrimitives.WriteDoubleBigEndian(_point.AsSpan(5), 12.25);
            BinaryPrimitives.WriteDoubleBigEndian(_point.AsSpan(13), -45.5);
        }

        if (Before() != After())
        {
            throw new InvalidOperationException("Point output differs from the baseline.");
        }
    }

    [Benchmark(Baseline = true)]
    public SKPoint Before()
    {
        OldRendering.WkbToSkiaConverter.TryConvertPoint(_point, _transform, out var point);
        return point;
    }

    [Benchmark]
    public SKPoint After()
    {
        WkbToSkiaConverter.TryConvertPoint(_point, _transform, out var point);
        return point;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class RasterDecoding
{
    private byte[] _before = [];
    private byte[] _after = [];
    private TilePixelLayout _layout;
    private OldRaster.TilePixelLayout _oldLayout;

    [Params(1, 3, 4)]
    public int Channels { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _before = new byte[256 * 256 * Channels];
        new Random(47).NextBytes(_before);
        _after = (byte[])_before.Clone();
        _layout = new(256, Channels, 8, TilePixelLayout.PredictorHorizontalDifferencing, true);
        _oldLayout = new(256, Channels, 8, OldRaster.TilePixelLayout.PredictorHorizontalDifferencing, true);
        Before();
        After();
        if (!_before.AsSpan().SequenceEqual(_after))
        {
            throw new InvalidOperationException("Raster output differs from the baseline.");
        }
    }

    [Benchmark(Baseline = true)]
    public void Before() => OldRaster.TilePredictor.Undo(_before, _oldLayout);

    [Benchmark]
    public void After() => TilePredictor.Undo(_after, _layout);
}
