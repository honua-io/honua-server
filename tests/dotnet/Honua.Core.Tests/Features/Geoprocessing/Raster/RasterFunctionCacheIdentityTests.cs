// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Raster.Functions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Tests.Features.Geoprocessing.Raster;

public sealed class RasterFunctionCacheIdentityTests
{
    [Fact]
    public void Build_SourceOrderDoesNotChangeKey()
    {
        var identity = CreateIdentity() with
        {
            Sources =
            [
                CreateSource("z_source", "source-2", 'b'),
                CreateSource("a_source", "source-1", 'a'),
            ],
        };

        var first = RasterFunctionCacheKey.Build(identity);
        var second = RasterFunctionCacheKey.Build(identity with { Sources = identity.Sources.Reverse().ToArray() });

        first.Should().Be(second);
        first.Should().MatchRegex("^raster-function:v1:[0-9a-f]{64}$");
    }

    [Fact]
    public void Build_EachSemanticIdentityClassInvalidatesKey()
    {
        var identity = CreateIdentity();
        var baseline = RasterFunctionCacheKey.Build(identity);
        var changes = new RasterFunctionCacheIdentity[]
        {
            identity with { TenantId = "tenant-b" },
            identity with { FunctionName = "different-function" },
            identity with { FunctionVersion = 8 },
            identity with { DefinitionHash = Hash('b') },
            identity with { SemanticVersion = "2" },
            identity with { ImplementationVersion = "postgis-43" },
            identity with { Sources = [identity.Sources[0] with { ImmutableVersion = "generation-8" }] },
            identity with { Sources = [identity.Sources[0] with { ContentSha256 = Hash('c') }] },
            identity with { Grid = identity.Grid with { Srid = 3857 } },
            identity with { Grid = identity.Grid with { OriginX = 1 } },
            identity with { Grid = identity.Grid with { RotationY = 0.25 } },
            identity with { Time = identity.Time! with { End = identity.Time!.End.AddMinutes(1) } },
            identity with { Bands = [2, 1] },
            identity with { Render = identity.Render with { Quality = 89 } },
            identity with { Render = identity.Render with { Transparent = false } },
            identity with { Render = identity.Render with { NoData = -9999 } },
        };

        changes.Select(RasterFunctionCacheKey.Build).Should().OnlyContain(key => key != baseline);
        changes.Select(RasterFunctionCacheKey.Build).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Build_EquivalentTimeOffsetsProduceSameKey()
    {
        var identity = CreateIdentity();
        var offset = identity with
        {
            Time = new RasterFunctionTimeCacheIdentity(
                identity.Time!.Start.ToOffset(TimeSpan.FromHours(-5)),
                identity.Time.End.ToOffset(TimeSpan.FromHours(-5))),
        };

        RasterFunctionCacheKey.Build(offset).Should().Be(RasterFunctionCacheKey.Build(identity));
    }

    [Fact]
    public void JsonContract_HasNoLocatorOrSecretBearingFields()
    {
        var json = JsonSerializer.Serialize(
            CreateIdentity(),
            RasterFunctionJsonContext.Default.RasterFunctionCacheIdentity);

        json.Should().NotContainEquivalentOf("uri");
        json.Should().NotContainEquivalentOf("objectKey");
        json.Should().NotContainEquivalentOf("path");
        json.Should().NotContainEquivalentOf("locator");
        json.Should().NotContainEquivalentOf("credential");
        json.Should().NotContainEquivalentOf("token");
        json.Should().NotContainEquivalentOf("authorization");
    }

    [Fact]
    public void Build_DuplicateBindingOrLocatorShapedLogicalId_FailsClosed()
    {
        var identity = CreateIdentity();
        var duplicate = identity with { Sources = [identity.Sources[0], identity.Sources[0]] };
        var locator = identity with
        {
            Sources = [identity.Sources[0] with { LogicalSourceId = "s3://bucket/key" }],
        };

        var duplicateAct = () => RasterFunctionCacheKey.Build(duplicate);
        var locatorAct = () => RasterFunctionCacheKey.Build(locator);

        duplicateAct.Should().Throw<ArgumentException>();
        locatorAct.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void StoreValidation_RejectsNonExactReferenceAndInvalidGraph()
    {
        var badReference = new RasterFunctionDefinitionReference
        {
            TenantId = "tenant-a",
            Name = "vegetation",
            Version = 0,
            DefinitionHash = Hash('A'),
        };
        var badRequest = new RasterFunctionDefinitionCreateRequest
        {
            TenantId = "tenant-a",
            Name = "vegetation",
            ExpectedLatestVersion = 0,
            IdempotencyKey = "request-1",
            Definition = new RasterFunctionDefinition { Nodes = [], OutputNodeId = "missing" },
        };

        var referenceAct = () => RasterFunctionDefinitionStoreValidation.Validate(badReference);
        var requestAct = () => RasterFunctionDefinitionStoreValidation.Validate(badRequest);

        referenceAct.Should().Throw<ArgumentException>();
        requestAct.Should().Throw<ArgumentException>();
    }

    private static RasterFunctionCacheIdentity CreateIdentity()
        => new()
        {
            TenantId = "tenant-a",
            FunctionName = "vegetation",
            FunctionVersion = 7,
            DefinitionHash = Hash('a'),
            SemanticVersion = "1",
            ImplementationVersion = "postgis-42",
            Sources = [CreateSource("imagery", "catalog-11", '1')],
            Grid = new RasterFunctionGridCacheIdentity
            {
                Srid = 4326,
                Width = 256,
                Height = 128,
                OriginX = -157.9,
                OriginY = 21.4,
                PixelWidth = 0.001,
                PixelHeight = -0.001,
                RotationX = 0,
                RotationY = 0,
            },
            Time = new RasterFunctionTimeCacheIdentity(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero)),
            Bands = [1, 2],
            Render = new RasterFunctionRenderCacheIdentity
            {
                OutputFormat = RasterFormat.PNG,
                Quality = 90,
                Transparent = true,
                BackgroundColor = 0,
                Resampling = ResamplingAlgorithm.Bilinear,
            },
        };

    private static RasterFunctionSourceCacheIdentity CreateSource(string bindingName, string sourceId, char hashCharacter)
        => new()
        {
            BindingName = bindingName,
            SourceKind = RasterFunctionCacheSourceKind.Postgis,
            LogicalSourceId = sourceId,
            ImmutableVersion = "generation-7",
            ContentSha256 = Hash(hashCharacter),
        };

    private static string Hash(char character) => new(character, 64);
}
