// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Honua.Geoprocessing.Inference;
using Honua.TestKit.Attributes;
using Xunit;
using Arb = FsCheck.Fluent.Arb;
using Gen = FsCheck.Fluent.Gen;

namespace Honua.Server.Tests.Features.Geoprocessing.Inference;

/// <summary>
/// Bounded properties and deterministic regression seeds for the small GeoTIFF
/// metadata reader. The fast tier never decodes pixels and caps generated input
/// at 4 KiB; the opt-in soak repeats the same structured mutation grammar.
/// </summary>
public sealed class GeoTiffGeoreferencingPropertyTests
{
    private const int FastRandomCases = 256;
    private const int FastStructuredCases = 256;
    private const int MaximumGeneratedBytes = 4096;

    [Property(
        MaxTest = FastRandomCases,
        EndSize = 1024,
        Arbitrary = new[] { typeof(GeoTiffArbitraries) })]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public bool TryRead_ArbitraryBoundedBytes_IsTotal(BoundedTiffBytes input)
    {
        input.Bytes.Length.Should().BeLessThanOrEqualTo(MaximumGeneratedBytes);

        var parsed = GeoTiffGeoreferencing.TryRead(input.Bytes, out var georeferencing);

        AssertParserInvariants(parsed, georeferencing, $"random payload ({input.Bytes.Length} bytes)");
        return true;
    }

    [Property(
        MaxTest = FastStructuredCases,
        EndSize = 128,
        Arbitrary = new[] { typeof(GeoTiffArbitraries) })]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public bool TryRead_StructuredMetadataMutations_AreTotalAndBounded(StructuredTiffInput input)
    {
        var bytes = GeoTiffFuzzCorpus.CreateMutated(input);
        bytes.Length.Should().BeLessThanOrEqualTo(MaximumGeneratedBytes);

        var parsed = GeoTiffGeoreferencing.TryRead(bytes, out var georeferencing);

        AssertParserInvariants(parsed, georeferencing, input.ToString());
        return true;
    }

    public static IEnumerable<object[]> EveryStructuredMutationCombination()
        => from container in Enum.GetValues<TiffContainer>()
           from byteOrder in Enum.GetValues<TiffByteOrder>()
           from mutation in Enum.GetValues<StructuredTiffMutation>()
           select new object[] { container, byteOrder, mutation };

    [Theory]
    [MemberData(nameof(EveryStructuredMutationCombination))]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void TryRead_EveryStructuredMutationCombination_IsTotal(
        TiffContainer container,
        TiffByteOrder byteOrder,
        StructuredTiffMutation mutation)
    {
        var input = new StructuredTiffInput(container, byteOrder, mutation, 3049);
        var bytes = GeoTiffFuzzCorpus.CreateMutated(input);

        var parsed = GeoTiffGeoreferencing.TryRead(bytes, out var georeferencing);

        AssertParserInvariants(parsed, georeferencing, input.ToString());
    }

    [Property(
        MaxTest = FastStructuredCases,
        EndSize = 128,
        Arbitrary = new[] { typeof(GeoTiffArbitraries) })]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public bool TryRead_ValidClassicAndBigTiffMetadata_RoundTrips(ValidGeoTiffInput input)
    {
        var fixture = GeoTiffFuzzCorpus.CreateValid(input);

        var parsed = GeoTiffGeoreferencing.TryRead(fixture.Bytes, out var actual);

        parsed.Should().BeTrue(input.ToString());
        actual.IsGeoreferenced.Should().BeTrue(input.ToString());
        actual.Width.Should().Be(fixture.Expected.Width);
        actual.Height.Should().Be(fixture.Expected.Height);
        actual.OriginX.Should().Be(fixture.Expected.OriginX);
        actual.OriginY.Should().Be(fixture.Expected.OriginY);
        actual.PixelSizeX.Should().Be(fixture.Expected.PixelSizeX);
        actual.PixelSizeY.Should().Be(fixture.Expected.PixelSizeY);
        actual.CrsCode.Should().Be(fixture.Expected.CrsCode);
        actual.HasRasterData.Should().BeTrue();
        actual.UnsupportedTransformReason.Should().BeNull();
        return true;
    }

    [Property(
        MaxTest = FastStructuredCases,
        EndSize = 128,
        Arbitrary = new[] { typeof(GeoTiffArbitraries) })]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public bool DescribeMismatchAgainst_MateriallyDifferentGround_NeverMatches(MaterialMismatchInput input)
    {
        var (output, source) = CreateMaterialMismatch(input);

        output.DescribeMismatchAgainst(source).Should().NotBeNull(input.Kind.ToString());
        return true;
    }

    public static IEnumerable<object[]> EveryMaterialDifference()
        => Enum.GetValues<MaterialDifference>().Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(EveryMaterialDifference))]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void DescribeMismatchAgainst_EveryMaterialDifference_NeverMatches(MaterialDifference difference)
    {
        var (output, source) = CreateMaterialMismatch(new MaterialMismatchInput(difference, 3049));

        output.DescribeMismatchAgainst(source).Should().NotBeNull(difference.ToString());
    }

    [UnitTest]
    public void IsGeoreferenced_ExtentUnderflowingToZero_IsRejected()
    {
        var georeferencing = new GeoTiffGeoreferencing
        {
            Width = double.Epsilon,
            Height = 1,
            OriginX = 0,
            OriginY = 0,
            PixelSizeX = double.Epsilon,
            PixelSizeY = 1,
            CrsCode = 4326,
            HasRasterData = true
        };

        georeferencing.ExtentWidth.Should().Be(0);
        georeferencing.IsGeoreferenced.Should().BeFalse();
    }

    public static IEnumerable<object[]> ParserRegressionCases()
        => GeoTiffFuzzCorpus.RegressionCases()
            .Select(regression => new object[] { regression.Name });

    [Theory]
    [MemberData(nameof(ParserRegressionCases))]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void TryRead_KnownParserRegressionFixture_HasExpectedOutcome(string regressionName)
    {
        var regression = GeoTiffFuzzCorpus.RegressionCases().Single(item => item.Name == regressionName);
        var parsed = GeoTiffGeoreferencing.TryRead(regression.Bytes, out var georeferencing);

        parsed.Should().Be(regression.ExpectedToParse, regression.Name);
        georeferencing.IsGeoreferenced.Should().Be(regression.ExpectedToBeGeoreferenced, regression.Name);
        AssertParserInvariants(parsed, georeferencing, regression.Name);
    }

    [UnitTest]
    public void TryRead_PixelIsPointAndPixelIsArea_NormalizeToDifferentCorners()
    {
        var cases = GeoTiffFuzzCorpus.RegressionCases().ToDictionary(item => item.Name);

        GeoTiffGeoreferencing.TryRead(cases["pixel-is-area"].Bytes, out var area).Should().BeTrue();
        GeoTiffGeoreferencing.TryRead(cases["pixel-is-point"].Bytes, out var point).Should().BeTrue();

        point.OriginX.Should().Be(area.OriginX - (area.PixelSizeX / 2d));
        point.OriginY.Should().Be(area.OriginY + (area.PixelSizeY / 2d));
        point.DescribeMismatchAgainst(area).Should().NotBeNull(
            "PixelIsPoint and PixelIsArea tiepoints describe corners half a cell apart");
    }

    [UnitTest]
    public void TryRead_BoundedRegressionCorpus_DoesNotAllocatePerParse()
    {
        var corpus = GeoTiffFuzzCorpus.RegressionCases().Select(item => item.Bytes).ToArray();

        // Warm every parser branch before measuring so tier-zero JIT work is not
        // attributed to a metadata read.
        for (var warmup = 0; warmup < 8; warmup++)
        {
            foreach (var bytes in corpus)
            {
                _ = GeoTiffGeoreferencing.TryRead(bytes, out _);
            }
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var repetition = 0; repetition < 64; repetition++)
        {
            foreach (var bytes in corpus)
            {
                _ = GeoTiffGeoreferencing.TryRead(bytes, out _);
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().BeLessThanOrEqualTo(
            256,
            "the parser is a span-only metadata reader and input size must not drive managed allocation");
    }

    [UnitTest]
    public void TryRead_LocalStructuredMutationSoak_WhenEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("HONUA_GEOTIFF_FUZZ_SOAK"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var iterations = ReadBoundedSoakIterations();
        var mutations = Enum.GetValues<StructuredTiffMutation>();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var input = new StructuredTiffInput(
                iteration % 2 == 0 ? TiffContainer.Classic : TiffContainer.BigTiff,
                (iteration / 2) % 2 == 0 ? TiffByteOrder.LittleEndian : TiffByteOrder.BigEndian,
                mutations[iteration % mutations.Length],
                unchecked((iteration * 1_664_525) + 1_013_904_223));
            var bytes = GeoTiffFuzzCorpus.CreateMutated(input);

            var parsed = GeoTiffGeoreferencing.TryRead(bytes, out var georeferencing);

            AssertParserInvariants(parsed, georeferencing, $"soak iteration {iteration}: {input}");
        }
    }

    private static int ReadBoundedSoakIterations()
    {
        const int defaultIterations = 100_000;
        const int maximumIterations = 1_000_000;
        var configured = Environment.GetEnvironmentVariable("HONUA_GEOTIFF_FUZZ_ITERATIONS");
        return int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            ? Math.Clamp(iterations, 1, maximumIterations)
            : defaultIterations;
    }

    private static void AssertParserInvariants(
        bool parsed,
        GeoTiffGeoreferencing georeferencing,
        string because)
    {
        if (!georeferencing.IsGeoreferenced)
        {
            return;
        }

        parsed.Should().BeTrue(because);
        georeferencing.HasRasterData.Should().BeTrue(because);
        georeferencing.UnsupportedTransformReason.Should().BeNull(because);
        georeferencing.Width.Should().BeGreaterThan(0, because);
        georeferencing.Width.Should().BeLessThanOrEqualTo((double)ulong.MaxValue, because);
        georeferencing.Width.Should().Be(Math.Truncate(georeferencing.Width), because);
        georeferencing.Height.Should().BeGreaterThan(0, because);
        georeferencing.Height.Should().BeLessThanOrEqualTo((double)ulong.MaxValue, because);
        georeferencing.Height.Should().Be(Math.Truncate(georeferencing.Height), because);
        double.IsFinite(georeferencing.OriginX).Should().BeTrue(because);
        double.IsFinite(georeferencing.OriginY).Should().BeTrue(because);
        double.IsFinite(georeferencing.PixelSizeX).Should().BeTrue(because);
        double.IsFinite(georeferencing.PixelSizeY).Should().BeTrue(because);
        georeferencing.PixelSizeX.Should().BeGreaterThan(0, because);
        georeferencing.PixelSizeY.Should().BeGreaterThan(0, because);
        double.IsFinite(georeferencing.ExtentWidth).Should().BeTrue(because);
        double.IsFinite(georeferencing.ExtentHeight).Should().BeTrue(because);
        georeferencing.ExtentWidth.Should().BeGreaterThan(0, because);
        georeferencing.ExtentHeight.Should().BeGreaterThan(0, because);
        georeferencing.CrsCode.Should().NotBe(0, because);
    }

    private static (GeoTiffGeoreferencing Output, GeoTiffGeoreferencing Source) CreateMaterialMismatch(
        MaterialMismatchInput input)
    {
        var magnitude = Math.Abs((long)input.Seed);
        var source = new GeoTiffGeoreferencing
        {
            Width = 32 + (magnitude % 256),
            Height = 32 + ((magnitude / 3) % 256),
            OriginX = -1_000_000 + (magnitude % 2_000_000),
            OriginY = -1_000_000 + ((magnitude / 7) % 2_000_000),
            PixelSizeX = 1 + (magnitude % 30),
            PixelSizeY = 1 + ((magnitude / 11) % 30),
            CrsCode = 32610,
            HasRasterData = true
        };

        var output = input.Kind switch
        {
            MaterialDifference.Crs => source with { CrsCode = 32611 },
            MaterialDifference.OriginX => source with { OriginX = source.OriginX + (source.ExtentWidth / 2d) },
            MaterialDifference.OriginY => source with { OriginY = source.OriginY + (source.ExtentHeight / 2d) },
            MaterialDifference.ExtentWidth => source with { Width = source.Width + 2 },
            MaterialDifference.ExtentHeight => source with { Height = source.Height + 2 },
            MaterialDifference.NonFiniteOutputOrigin => source with { OriginX = double.NaN },
            MaterialDifference.NonFiniteOutputScale => source with { PixelSizeY = double.PositiveInfinity },
            MaterialDifference.OverflowingOutputExtent => source with { Width = double.MaxValue },
            MaterialDifference.NegativeOutputScale => source with { PixelSizeX = -source.PixelSizeX },
            MaterialDifference.ExtremeFiniteOutputOrigin => source with { OriginY = double.MaxValue },
            MaterialDifference.NonFiniteSourceOrigin => source,
            MaterialDifference.NonFiniteSourceScale => source,
            MaterialDifference.OverflowingSourceExtent => source,
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.Kind, null)
        };

        source = input.Kind switch
        {
            MaterialDifference.NonFiniteSourceOrigin => source with { OriginY = double.NegativeInfinity },
            MaterialDifference.NonFiniteSourceScale => source with { PixelSizeX = double.NaN },
            MaterialDifference.OverflowingSourceExtent => source with { Height = double.MaxValue },
            _ => source
        };

        return (output, source);
    }
}

public readonly record struct BoundedTiffBytes(byte[] Bytes);

public readonly record struct MaterialMismatchInput(MaterialDifference Kind, int Seed);

public enum MaterialDifference
{
    Crs,
    OriginX,
    OriginY,
    ExtentWidth,
    ExtentHeight,
    NonFiniteOutputOrigin,
    NonFiniteOutputScale,
    OverflowingOutputExtent,
    NegativeOutputScale,
    ExtremeFiniteOutputOrigin,
    NonFiniteSourceOrigin,
    NonFiniteSourceScale,
    OverflowingSourceExtent
}

internal static class GeoTiffArbitraries
{
    public static Arbitrary<BoundedTiffBytes> BoundedTiffBytes()
        => Arb.From(
            Gen.Sized(size =>
                from length in Gen.Choose(0, Math.Min(4096, Math.Max(8, size * 4)))
                from bytes in Gen.ArrayOf(Gen.Choose(0, byte.MaxValue).Select(value => (byte)value), length)
                select new BoundedTiffBytes(bytes)));

    public static Arbitrary<ValidGeoTiffInput> ValidGeoTiffInput()
        => Arb.From(
            from container in Gen.Elements(Enum.GetValues<TiffContainer>())
            from byteOrder in Gen.Elements(Enum.GetValues<TiffByteOrder>())
            from width in Gen.Choose(1, 2048)
            from height in Gen.Choose(1, 2048)
            from originX in Gen.Choose(-50_000_000, 50_000_000)
            from originY in Gen.Choose(-50_000_000, 50_000_000)
            from pixelSizeX in Gen.Choose(1, 10_000)
            from pixelSizeY in Gen.Choose(1, 10_000)
            from epsg in Gen.Elements<ushort>(4326, 3857, 26910, 32610, 32710)
            from pixelIsPoint in Gen.Elements(true, false)
            select new ValidGeoTiffInput(
                container,
                byteOrder,
                width,
                height,
                originX,
                originY,
                pixelSizeX,
                pixelSizeY,
                epsg,
                pixelIsPoint));

    public static Arbitrary<StructuredTiffInput> StructuredTiffInput()
        => Arb.From(
            from container in Gen.Elements(Enum.GetValues<TiffContainer>())
            from byteOrder in Gen.Elements(Enum.GetValues<TiffByteOrder>())
            from mutation in Gen.Elements(Enum.GetValues<StructuredTiffMutation>())
            from seed in Gen.Choose(-10_000_000, 10_000_000)
            select new StructuredTiffInput(container, byteOrder, mutation, seed));

    public static Arbitrary<MaterialMismatchInput> MaterialMismatchInput()
        => Arb.From(
            from kind in Gen.Elements(Enum.GetValues<MaterialDifference>())
            from seed in Gen.Choose(-10_000_000, 10_000_000)
            select new MaterialMismatchInput(kind, seed));
}
