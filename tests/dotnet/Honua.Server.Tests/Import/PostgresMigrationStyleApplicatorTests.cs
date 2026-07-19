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
        var storedAssociations = new List<StyleLayerAssociation>();
        var styleWrites = 0;
        var layerWrites = 0;

        styleCatalog.GetStyleAsync("style:geoserver:roads", Arg.Any<CancellationToken>())
            .Returns(_ => storedStyle);
        styleCatalog.ListAssociationsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => storedAssociations.ToArray());
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
                    StyleVersion = 1,
                    RevisedBy = "geoserver-migration"
                };
                return storedStyle;
            });
        styleCatalog.AssociateLayerAsync(Arg.Any<int>(), "style:geoserver:roads", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var association = new StyleLayerAssociation(call.ArgAt<int>(0), "style:geoserver:roads", call.ArgAt<int>(2));
                if (!storedAssociations.Contains(association))
                {
                    storedAssociations.Add(association);
                }

                return true;
            });
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
                    StyleVersion = 1,
                    StyleRevisedBy = "geoserver-migration"
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
        layerWrites.Should().Be(1);
        storedLayers.Should().NotContainKey(84, "alternative styles must not replace a layer's render-facing default");
        await styleCatalog.Received(2).AssociateLayerAsync(42, request.TargetStyleId, 0, Arg.Any<CancellationToken>());
        await styleCatalog.Received(2).AssociateLayerAsync(84, request.TargetStyleId, 1, Arg.Any<CancellationToken>());
        await graphSync.Received(2).SyncLayerStylesAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_OperatorEditedCanonicalStyle_ReturnsConflictWithoutMutation()
    {
        var styleCatalog = Substitute.For<IStyleCatalog>();
        var layerCatalog = Substitute.For<ILayerStyleCatalog>();
        styleCatalog.GetStyleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new StyleCatalogRecord
        {
            StyleId = "style:geoserver:roads",
            MapLibreStyleJson = "{\"version\":8,\"sources\":{},\"layers\":[]}",
            RevisedBy = "operator@example.com"
        });
        styleCatalog.ListAssociationsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<StyleLayerAssociation>());
        var applicator = new PostgresMigrationStyleApplicator(styleCatalog, layerCatalog);

        var outcome = await applicator.ApplyAsync(CreateRequest());

        outcome.Should().Be(MigrationStyleApplyOutcome.SkippedConflict);
        await styleCatalog.DidNotReceiveWithAnyArgs().UpsertStyleAsync(default!, default!);
        await layerCatalog.DidNotReceiveWithAnyArgs().SetMapLibreStyleAsync(default, default!);
    }

    [Fact]
    public async Task ApplyAsync_NullCanonicalUpsert_ThrowsAndDoesNotReplaceLayerStyle()
    {
        var styleCatalog = Substitute.For<IStyleCatalog>();
        var layerCatalog = Substitute.For<ILayerStyleCatalog>();
        styleCatalog.GetStyleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new StyleCatalogRecord
        {
            StyleId = "style:geoserver:roads",
            MapLibreStyleJson = "{\"version\":8,\"sources\":{},\"layers\":[]}",
            RevisedBy = "geoserver-migration"
        });
        styleCatalog.ListAssociationsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<StyleLayerAssociation>());
        styleCatalog.UpsertStyleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((StyleCatalogRecord)null!);
        layerCatalog.GetLayerStyleAsync(42, Arg.Any<CancellationToken>()).Returns(new LayerStyleDefinition { LayerId = 42 });
        var applicator = new PostgresMigrationStyleApplicator(styleCatalog, layerCatalog);

        var action = () => applicator.ApplyAsync(CreateRequest());

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*upsert returned no record*");
        await layerCatalog.DidNotReceiveWithAnyArgs().SetMapLibreStyleAsync(default, default!);
    }

    [Fact]
    public async Task ApplyAsync_AssociationFailure_LeavesDefaultLayerUnchangedForResumableRetry()
    {
        var styleCatalog = Substitute.For<IStyleCatalog>();
        var layerCatalog = Substitute.For<ILayerStyleCatalog>();
        var canonicalJson = "{\"version\":8,\"name\":\"Roads\",\"sources\":{\"layer-42\":{\"type\":\"vector\",\"tiles\":[\"/tiles/42/{z}/{x}/{y}.mvt\"],\"minzoom\":0,\"maxzoom\":22}},\"layers\":[{\"id\":\"roads\",\"type\":\"line\",\"paint\":{\"line-color\":\"#224466\"},\"source\":\"layer-42\",\"source-layer\":\"layer\"}]}";
        styleCatalog.GetStyleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new StyleCatalogRecord
        {
            StyleId = "style:geoserver:roads",
            MapLibreStyleJson = canonicalJson,
            RevisedBy = "geoserver-migration"
        });
        styleCatalog.ListAssociationsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<StyleLayerAssociation>());
        styleCatalog.AssociateLayerAsync(42, Arg.Any<string>(), 0, Arg.Any<CancellationToken>()).Returns(false);
        layerCatalog.GetLayerStyleAsync(42, Arg.Any<CancellationToken>()).Returns(new LayerStyleDefinition { LayerId = 42 });
        var applicator = new PostgresMigrationStyleApplicator(styleCatalog, layerCatalog);

        var action = () => applicator.ApplyAsync(CreateRequest());

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Could not associate*");
        await layerCatalog.DidNotReceiveWithAnyArgs().SetMapLibreStyleAsync(default, default!);
    }

    private static MigrationLiveStyleApplyRequest CreateRequest() => new()
    {
        TargetStyleId = "style:geoserver:roads",
        Title = "Roads",
        MapLibreLayersJson = ConvertedLayers,
        ReviewDisposition = "applied",
        LayerTargets = [new MigrationStyleLayerTarget(42, 0)]
    };

    [Theory]
    [InlineData("manual-review", ConvertedLayers)]
    [InlineData("applied", null)]
    public async Task ApplyAsync_ManualReviewOrFatalConversion_DoesNotAssignLiveStyle(
        string disposition,
        string? convertedLayers)
    {
        var styleCatalog = Substitute.For<IStyleCatalog>();
        var layerCatalog = Substitute.For<ILayerStyleCatalog>();
        styleCatalog.ListAssociationsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<StyleLayerAssociation>());
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
