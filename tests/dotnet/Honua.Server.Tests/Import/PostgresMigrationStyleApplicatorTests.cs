// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Postgres.Features.Migration;
using NSubstitute;

namespace Honua.Server.Tests.Import;

public sealed class PostgresMigrationStyleApplicatorTests
{
    private const string ConvertedLayers = """
        [{"id":"roads","type":"line","paint":{"line-color":"#224466"}}]
        """;

    [Fact]
    public async Task ApplyAsync_CleanConversion_PopulatesLiveCatalogsAndIsIdempotent()
    {
        var styleCatalog = Substitute.For<IStyleCatalog>();
        var layerCatalog = Substitute.For<ILayerStyleCatalog>();
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();
        StyleCatalogRecord? storedStyle = null;
        var storedLayers = new Dictionary<int, LayerStyleDefinition>();
        var styleWrites = 0;
        var layerWrites = 0;

        styleCatalog.GetStyleAsync("style:geoserver:roads", Arg.Any<CancellationToken>())
            .Returns(_ => storedStyle);
        styleCatalog.CreateStyleAsync(
                "style:geoserver:roads",
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                styleWrites++;
                storedStyle = new StyleCatalogRecord
                {
                    StyleId = "style:geoserver:roads",
                    Title = call.ArgAt<string?>(2),
                    MapLibreStyleJson = call.ArgAt<string>(1),
                    StyleVersion = 1
                };
                return storedStyle;
            });
        styleCatalog.AssociateLayerAsync(Arg.Any<int>(), "style:geoserver:roads", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);
        layerCatalog.GetLayerStyleAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => storedLayers.GetValueOrDefault(call.ArgAt<int>(0)));
        layerCatalog.SetMapLibreStyleAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                layerWrites++;
                var value = new LayerStyleDefinition
                {
                    LayerId = call.ArgAt<int>(0),
                    MapLibreStyleJson = call.ArgAt<string>(1),
                    StyleVersion = 1
                };
                storedLayers[value.LayerId] = value;
                return value;
            });

        var applicator = new PostgresMigrationStyleApplicator(styleCatalog, layerCatalog, graphSync);
        var request = new MigrationLiveStyleApplyRequest
        {
            TargetStyleId = "style:geoserver:roads",
            Title = "Roads",
            MapLibreLayersJson = ConvertedLayers,
            ReviewDisposition = "applied",
            LayerTargets = [new MigrationStyleLayerTarget(42, 0), new MigrationStyleLayerTarget(84, 1)]
        };

        var first = await applicator.ApplyAsync(request);
        var retrievedStyle = await styleCatalog.GetStyleAsync(request.TargetStyleId);
        var retrievedLayer = await layerCatalog.GetLayerStyleAsync(42);
        var second = await applicator.ApplyAsync(request);

        first.Should().Be(MigrationStyleApplyOutcome.Applied);
        second.Should().Be(MigrationStyleApplyOutcome.AlreadyApplied);
        retrievedStyle!.MapLibreStyleJson.Should().Contain("/tiles/42/{z}/{x}/{y}.mvt");
        retrievedStyle.MapLibreStyleJson.Should().Contain("/tiles/84/{z}/{x}/{y}.mvt");
        retrievedLayer!.MapLibreStyleJson.Should().Contain("\"source\":\"layer-42\"");
        styleWrites.Should().Be(1);
        layerWrites.Should().Be(2);
        await styleCatalog.Received(2).AssociateLayerAsync(42, request.TargetStyleId, 0, Arg.Any<CancellationToken>());
        await styleCatalog.Received(2).AssociateLayerAsync(84, request.TargetStyleId, 1, Arg.Any<CancellationToken>());
        await graphSync.Received(2).SyncLayerStylesAsync(42, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("manual-review", ConvertedLayers)]
    [InlineData("applied", null)]
    public async Task ApplyAsync_ManualReviewOrFatalConversion_DoesNotAssignLiveStyle(
        string disposition,
        string? convertedLayers)
    {
        var styleCatalog = Substitute.For<IStyleCatalog>();
        var layerCatalog = Substitute.For<ILayerStyleCatalog>();
        var applicator = new PostgresMigrationStyleApplicator(styleCatalog, layerCatalog);

        var outcome = await applicator.ApplyAsync(new MigrationLiveStyleApplyRequest
        {
            TargetStyleId = "style:geoserver:roads",
            Title = "Roads",
            MapLibreLayersJson = convertedLayers,
            ReviewDisposition = disposition,
            LayerTargets = [new MigrationStyleLayerTarget(42, 0)]
        });

        outcome.Should().Be(MigrationStyleApplyOutcome.SkippedManualReview);
        await styleCatalog.DidNotReceiveWithAnyArgs().CreateStyleAsync(default!, default!);
        await styleCatalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!);
        await layerCatalog.DidNotReceiveWithAnyArgs().SetMapLibreStyleAsync(default, default!);
    }

    [Fact]
    public async Task ApplyAsync_NoPublishedLayer_DoesNotCreateStandaloneRenderStyle()
    {
        var styleCatalog = Substitute.For<IStyleCatalog>();
        var layerCatalog = Substitute.For<ILayerStyleCatalog>();
        var applicator = new PostgresMigrationStyleApplicator(styleCatalog, layerCatalog);

        var outcome = await applicator.ApplyAsync(new MigrationLiveStyleApplyRequest
        {
            TargetStyleId = "style:geoserver:roads",
            Title = "Roads",
            MapLibreLayersJson = ConvertedLayers,
            ReviewDisposition = "applied"
        });

        outcome.Should().Be(MigrationStyleApplyOutcome.SkippedNoPublishedLayers);
        await styleCatalog.DidNotReceiveWithAnyArgs().CreateStyleAsync(default!, default!);
    }
}
