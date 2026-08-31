// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// Pixel baselines for the shared rendered-map pipeline. These tests intentionally live in the
/// WMS namespace so the OGC Classic Maps raster shard owns all three protocol entry points.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class RenderedRasterBaselineTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await _fixture.GetService<ILayerStyleCatalog>().SetMapLibreStyleAsync(
            WebAppFixture.TestLayerId,
            """{"version":8,"layers":[{"id":"baseline-point","type":"circle","paint":{"circle-color":"#d7191c","circle-radius":10,"circle-stroke-color":"#2c3e50","circle-stroke-width":2}}]}""");
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    public static TheoryData<string, string, RasterBaselineTolerance> Scenes => new()
    {
        {
            "wms-getmap-point",
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=37.4,-122.6,37.6,-122.4&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png",
            RasterBaselineTolerance.Exact
        },
        {
            "wms-getmap-transparent",
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=37.4,-122.6,37.6,-122.4&WIDTH=192&HEIGHT=128&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TRANSPARENT=true",
            RasterBaselineTolerance.Exact
        },
        {
            "static-map-overlays",
            $"/static/{WebAppFixture.TestServiceId}/bbox/-122.6,37.4,-122.4,37.6/256x192.png?layers={WebAppFixture.TestLayerId}&markers=-122.55,37.55,blue&path=-122.58,37.42|-122.42,37.58",
            RasterBaselineTolerance.Exact
        },
        {
            "mapserver-export-point",
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-122.6,37.4,-122.4,37.6&bboxSR=4326&imageSR=4326&size=256,192&format=png32&transparent=false&f=image",
            RasterBaselineTolerance.Exact
        }
    };

    [Theory]
    [MemberData(nameof(Scenes))]
    [Trait("Category", "Integration")]
    [Trait("Tier", Tiers.Integration)]
    [Operation(Operations.Render)]
    public async Task RenderedRaster_CanonicalScene_MatchesCommittedBaseline(
        string baselineName,
        string requestPath,
        RasterBaselineTolerance tolerance)
    {
        using var response = await _fixture.Client.GetAsync(requestPath);
        var actual = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(actual));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

        RasterBaseline.AssertMatches(baselineName, actual, tolerance);
    }
}

public readonly record struct RasterBaselineTolerance(double MaximumRootMeanSquareError, int MaximumChangedPixels)
{
    public static RasterBaselineTolerance Exact { get; } = new(0, 0);
}

internal static class RasterBaseline
{
    private const string UpdateEnvironmentVariable = "HONUA_UPDATE_RASTER_BASELINES";
    private const string SourceDirectoryEnvironmentVariable = "HONUA_RASTER_BASELINE_ROOT";

    public static void AssertMatches(string name, byte[] actualBytes, RasterBaselineTolerance tolerance)
    {
        if (string.Equals(Environment.GetEnvironmentVariable(UpdateEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            var sourceDirectory = Environment.GetEnvironmentVariable(SourceDirectoryEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(sourceDirectory))
            {
                throw new InvalidOperationException($"{SourceDirectoryEnvironmentVariable} is required while updating raster baselines.");
            }

            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllBytes(Path.Combine(sourceDirectory, $"{name}.png"), actualBytes);
            return;
        }

        var expectedPath = Path.Combine(AppContext.BaseDirectory, "Baselines", $"{name}.png");
        File.Exists(expectedPath).Should().BeTrue($"the committed raster baseline {name}.png must be copied to the test output");

        using var expected = SKBitmap.Decode(expectedPath);
        using var actual = SKBitmap.Decode(actualBytes);
        expected.Should().NotBeNull($"baseline {name}.png must be a decodable PNG");
        actual.Should().NotBeNull($"actual response for {name} must be a decodable PNG");
        actual!.Width.Should().Be(expected!.Width);
        actual.Height.Should().Be(expected.Height);

        long squaredError = 0;
        var changedPixels = 0;
        using var diff = new SKBitmap(actual.Width, actual.Height);

        for (var y = 0; y < actual.Height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                var expectedPixel = expected.GetPixel(x, y);
                var actualPixel = actual.GetPixel(x, y);
                var red = Math.Abs(expectedPixel.Red - actualPixel.Red);
                var green = Math.Abs(expectedPixel.Green - actualPixel.Green);
                var blue = Math.Abs(expectedPixel.Blue - actualPixel.Blue);
                var alpha = Math.Abs(expectedPixel.Alpha - actualPixel.Alpha);
                squaredError += (long)red * red + (long)green * green + (long)blue * blue + (long)alpha * alpha;
                if ((red | green | blue | alpha) != 0)
                {
                    changedPixels++;
                }

                diff.SetPixel(x, y, new SKColor((byte)red, (byte)green, (byte)blue, 255));
            }
        }

        var rootMeanSquareError = Math.Sqrt(squaredError / (actual.Width * actual.Height * 4d));
        if (rootMeanSquareError > tolerance.MaximumRootMeanSquareError || changedPixels > tolerance.MaximumChangedPixels)
        {
            var diagnosticsDirectory = Path.Combine(AppContext.BaseDirectory, "TestResults", "raster-baseline-diffs");
            Directory.CreateDirectory(diagnosticsDirectory);
            var actualPath = Path.Combine(diagnosticsDirectory, $"{name}.actual.png");
            var diffPath = Path.Combine(diagnosticsDirectory, $"{name}.diff.png");
            File.WriteAllBytes(actualPath, actualBytes);
            using var image = SKImage.FromBitmap(diff);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(diffPath, encoded.ToArray());

            throw new Xunit.Sdk.XunitException(
                $"Raster baseline '{name}' differed: RMSE={rootMeanSquareError:F6} (allowed {tolerance.MaximumRootMeanSquareError:F6}), " +
                $"changedPixels={changedPixels} (allowed {tolerance.MaximumChangedPixels}). Actual: {actualPath}; diff: {diffPath}");
        }
    }
}
