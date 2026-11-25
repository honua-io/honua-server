// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Geometry.Domain;
using Honua.Core.Features.Infrastructure.IO;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Core.Features.Tiles.PMTiles;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using SkiaSharp;

namespace Honua.Core.Tests.Fuzzing;

public sealed partial class BinaryFormatFuzzTests
{
    private const int ZarrFuzzIterations = 500;
    private const int PMTilesFuzzIterations = 500;
    private const int CogFuzzIterations = 300;
    private const int WkbFuzzIterations = 500;

    [UnitTest]
    public void ZarrChunkDecoder_DecompressionBombsAndUnsupportedCodecs_RejectWithinBounds()
    {
        var bombSource = new byte[2 * 1024 * 1024];
        var gzipBomb = CompressGzip(bombSource);
        var zlibBomb = CompressZlib(bombSource);
        var zstdBomb = ZstdCodec.Compress(bombSource, CompressionLevel.Optimal);
        var destination = new byte[64];

        Action decodeGzip = () => DecodeZarr(gzipBomb, "gzip", destination);
        Action decodeZlib = () => DecodeZarr(zlibBomb, "zlib", destination);
        Action decodeZstd = () => ZstdCodec.Decompress(zstdBomb, maxDecompressedBytes: destination.Length);
        Action decodeBlosc = () => DecodeZarr([2, 1, 1, 4, 0, 0, 0, 0], "blosc", destination);

        decodeGzip.Should().Throw<InvalidDataException>();
        decodeZlib.Should().Throw<InvalidDataException>();
        decodeZstd.Should().Throw<InvalidDataException>();
        decodeBlosc.Should().Throw<ZarrSubsetValidationException>();
    }

    [UnitTest]
    public void ZarrChunkDecoder_SeededRandomAndTruncatedMutations_NeverThrowsUnexpectedExceptions()
    {
        var rng = new Random(20260301);
        var sample = new byte[64];
        rng.NextBytes(sample);
        var validGzip = CompressGzip(sample);
        var validZlib = CompressZlib(sample);
        var destination = new byte[64];

        for (var i = 0; i < ZarrFuzzIterations; i++)
        {
            var codec = (i % 3) switch
            {
                0 => "gzip",
                1 => "zlib",
                _ => null
            };
            var baseline = codec switch
            {
                "gzip" => validGzip,
                "zlib" => validZlib,
                _ => sample
            };
            var mutated = MutatePayload(rng, baseline, maxRandomLength: 192);
            AssertSafeZarrDecode(mutated, codec, destination);
        }
    }

    [UnitTest]
    public void PMTilesDirectory_DecompressionBombAndOversizedVarints_RejectWithoutUnboundedAllocation()
    {
        var gzipBomb = CompressGzip(new byte[2 * 1024 * 1024]);
        Action decompressBomb = () => PMTilesDirectory.Decompress(gzipBomb, PMTilesCompression.Gzip, maxDecompressedBytes: 1024);
        decompressBomb.Should().Throw<InvalidDataException>();

        byte[][] hostileDirectories =
        [
            [],
            [0x80],
            [0xFF, 0xFF, 0xFF, 0x7F],
            [0x80, 0x80, 0x80, 0x80, 0x08],
            [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02],
            [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01],
            [0x01, 0x00, 0x01, 0x10, 0x00],
        ];

        foreach (var payload in hostileDirectories)
        {
            Action deserialize = () => PMTilesDirectory.DeserializeEntries(payload);
            deserialize.Should().Throw<InvalidDataException>();
        }
    }

    [UnitTest]
    public void PMTilesDirectory_SeededMutations_NeverAllocatesUnboundedOrThrowsUnexpected()
    {
        var rng = new Random(20260302);
        var validDir = PMTilesDirectory.SerializeEntries(
        [
            new PMTilesEntry(0, 0, 128, 1),
            new PMTilesEntry(1, 128, 256, 1),
            new PMTilesEntry(5, 1024, 64, 0),
        ]);

        for (var i = 0; i < PMTilesFuzzIterations; i++)
        {
            var mutated = MutatePayload(rng, validDir, maxRandomLength: 160);
            AssertSafePMTilesDeserialize(mutated);
            AssertSafePMTilesHeaderRead(mutated);
        }
    }

    [UnitTest]
    public async Task CogTiffParser_CyclicIfdAndMalformedHeaders_RejectWithoutInfiniteLoop()
    {
        var cyclicTiff = BuildClassicTiff(nextIfdOffset: 8);
        var readCyclic = () => new CogMetadataExtractor().ReadMetadataAsync(
            CreateRangeReader(cyclicTiff), "fuzz", "cyclic.tif", CancellationToken.None);
        await readCyclic.Should().ThrowAsync<InvalidDataException>().WithMessage("*Cyclic*");

        byte[] negativeBigTiffHeader =
        [
            0x49, 0x49, 0x2B, 0x00, 0x08, 0x00, 0x00, 0x00,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        ];
        Action parseNegativeHeader = () => TiffIfdParser.ParseHeader(negativeBigTiffHeader);
        parseNegativeHeader.Should().Throw<InvalidDataException>();

        var (classicParser, _) = TiffIfdParser.ParseHeader([0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00]);
        Action parseTruncatedIfd = () => classicParser.ParseIfd([0x50, 0xC3, 0x00, 0x01]);
        parseTruncatedIfd.Should().Throw<InvalidDataException>();
    }

    [UnitTest]
    public async Task CogTiffParser_SeededMutations_NeverHangsOrThrowsUnexpected()
    {
        var rng = new Random(20260303);
        var validClassic = BuildClassicTiff(nextIfdOffset: 0);
        var validBigTiff = BuildBigTiffHeaderAndIfd();
        var extractor = new CogMetadataExtractor();

        for (var i = 0; i < CogFuzzIterations; i++)
        {
            var baseline = (i & 1) == 0 ? validClassic : validBigTiff;
            var mutated = MutatePayload(rng, baseline, maxRandomLength: 256);
            await AssertSafeCogParseAsync(extractor, mutated);
        }
    }

    [UnitTest]
    public void WkbParsers_AdversarialCountsNaNAndDeepNesting_RejectWithoutCrash()
    {
        var validator = CreateWkbValidator();
        byte[] multiPointBomb = [1, 4, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F];
        byte[] lineStringBomb = [1, 2, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F];
        byte[] polygonBomb = [1, 3, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F];

        var skiaResult = WkbToSkiaConverter.Convert(multiPointBomb, static (x, y) => new SKPoint((float)x, (float)y));
        skiaResult.Points.Should().BeNull();
        skiaResult.Path.Should().BeNull();

        validator.ValidateWkb(multiPointBomb).Errors.Should().ContainSingle(e => e.Code == ValidationErrorCode.InvalidWkbStructure);
        validator.ValidateWkb(lineStringBomb).Errors.Should().ContainSingle(e => e.Code == ValidationErrorCode.InvalidWkbStructure);
        validator.ValidateWkb(polygonBomb).Errors.Should().ContainSingle(e => e.Code == ValidationErrorCode.InvalidWkbStructure);

        validator.ValidateWkb(BuildWkbPoint(double.NaN, 1.0)).IsValid.Should().BeFalse();
        validator.ValidateWkb(BuildWkbPoint(double.PositiveInfinity, 1.0)).Errors.Should().ContainSingle(e => e.Code == ValidationErrorCode.InvalidCoordinates);
        validator.ValidateWkb(BuildWkbPoint(1.0, double.NegativeInfinity)).Errors.Should().ContainSingle(e => e.Code == ValidationErrorCode.InvalidCoordinates);

        var deeplyNested = BuildNestedGeometryCollection(depth: 200);
        WkbSridNormalizer.TryGetDeclaredOrdinates(deeplyNested, out _, out _).Should().BeFalse();
        WkbSridNormalizer.RemoveEmbeddedSrid(deeplyNested).Should().Equal(deeplyNested);
        validator.ValidateWkb(deeplyNested).IsValid.Should().BeFalse();
    }

    [UnitTest]
    public void WkbParsers_SeededMutations_NeverThrowsUnhandledOrOverflowsStack()
    {
        var rng = new Random(20260304);
        var validator = CreateWkbValidator();
        byte[][] baselines =
        [
            BuildWkbPoint(12.5, -45.25),
            BuildNestedGeometryCollection(depth: 4),
            [1, 2, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 240, 63, 0, 0, 0, 0, 0, 0, 240, 63],
        ];

        for (var i = 0; i < WkbFuzzIterations; i++)
        {
            var mutated = MutatePayload(rng, baselines[i % baselines.Length], maxRandomLength: 192);
            AssertSafeWkbParse(validator, mutated);
        }
    }

    private static void DecodeZarr(byte[] raw, string? compressor, byte[] destination)
        => ZarrChunkDecoder.Decode(
            new ZarrChunkDecodeRequest
            {
                Raw = raw,
                Compressor = compressor,
                Destination = destination,
                DestinationLength = destination.Length,
            },
            CancellationToken.None);

    private static void AssertSafeZarrDecode(byte[] payload, string? codec, byte[] destination)
    {
        try
        {
            DecodeZarr(payload, codec, destination);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ZarrSubsetValidationException)
        {
            ex.Message.Should().NotBeNullOrWhiteSpace();
        }
    }

    private static void AssertSafePMTilesDeserialize(byte[] payload)
    {
        try
        {
            var entries = PMTilesDirectory.DeserializeEntries(payload);
            entries.Length.Should().BeLessThanOrEqualTo(payload.Length / 4);
        }
        catch (InvalidDataException ex)
        {
            ex.Message.Should().NotBeNullOrWhiteSpace();
        }
    }

    private static void AssertSafePMTilesHeaderRead(byte[] payload)
    {
        try
        {
            _ = PMTilesHeader.ReadFrom(payload);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            ex.Message.Should().NotBeNullOrWhiteSpace();
        }
    }

    private static async Task AssertSafeCogParseAsync(CogMetadataExtractor extractor, byte[] payload)
    {
        try
        {
            var (parser, firstOffset) = TiffIfdParser.ParseHeader(payload);
            if (firstOffset >= 0 && firstOffset < payload.Length)
            {
                _ = parser.ParseIfd(payload.AsSpan((int)firstOffset));
            }
        }
        catch (InvalidDataException ex)
        {
            ex.Message.Should().NotBeNullOrWhiteSpace();
        }

        try
        {
            _ = await extractor.ReadMetadataAsync(CreateRangeReader(payload), "fuzz", "mutated.tif", CancellationToken.None);
        }
        catch (InvalidDataException ex)
        {
            ex.Message.Should().NotBeNullOrWhiteSpace();
        }
    }

    private static void AssertSafeWkbParse(WkbGeometryValidator validator, byte[] payload)
    {
        var result = validator.ValidateWkb(payload);
        result.Should().NotBeNull();
        _ = WkbSridNormalizer.TryGetDeclaredOrdinates(payload, out _, out _);
        _ = WkbSridNormalizer.RemoveEmbeddedSrid(payload);
        _ = WkbToSkiaConverter.TryConvertPoint(payload, static (x, y) => new SKPoint((float)x, (float)y), out _);
    }
}
