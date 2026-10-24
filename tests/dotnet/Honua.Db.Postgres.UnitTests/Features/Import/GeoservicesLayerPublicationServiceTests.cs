// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.Attachments;
using Honua.Db.Postgres.Features.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Db.Postgres.Tests.Features.Import;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoservicesLayerPublicationServiceTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task TryPublishImportedLayerAsync_ConflictRecoveryFails_WarnsUnlessCancelled(
        bool failListing, int failureKind)
    {
        Exception failure = failureKind switch
        {
            1 => new LayerPublishingException(LayerPublishingErrorKind.Validation, "Recovery failed."),
            2 => new OperationCanceledException(),
            _ => new InvalidOperationException("Recovery failed.")
        };
        var publishing = new Mock<ILayerPublishingService>(MockBehavior.Strict);
        publishing.Setup(service => service.PublishLayerAsync(
                "Host=unused", It.IsAny<LayerPublishRequest>(), CancellationToken.None))
            .ThrowsAsync(new LayerPublishingException(LayerPublishingErrorKind.Conflict, "Already published.", 27));
        publishing.Setup(service => service.RewritePublishedLayerFieldsAsync(
                "Host=unused", 27, It.IsAny<LayerPublishRequest>(), CancellationToken.None))
            .Returns(Task.FromResult<PublishedLayerSummary?>(null));
        var refresh = publishing.Setup(service => service.RefreshMaterializedFeaturesForSourceTableAsync(
            "Host=unused", "honua_data", "parcels", CancellationToken.None));
        if (failListing)
        {
            refresh.ReturnsAsync([]);
            publishing.Setup(service => service.ListPublishedLayersAsync(
                    "Host=unused", "planning", CancellationToken.None))
                .ThrowsAsync(failure);
        }
        else
        {
            refresh.ThrowsAsync(failure);
        }

        var sut = new GeoservicesLayerPublicationService(
            NullLogger<GeoservicesLayerPublicationService>.Instance, publishing.Object);
        var warnings = new List<string>();
        var act = () => sut.TryPublishImportedLayerAsync(
            new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/rest/services/Planning/FeatureServer",
                LayerId = 0,
                TableName = "parcels",
                ServiceName = "planning",
                AutoPublish = true,
                OverwriteExisting = true
            },
            "honua_data",
            new GeoservicesLayerInfo { Id = 0, Name = "Parcels", Fields = [] },
            warnings, null, "job-1", DateTimeOffset.UtcNow, 2,
            "Host=unused", CancellationToken.None, replacingExistingTarget: true);

        if (failureKind == 2)
        {
            (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(failure);
            warnings.Should().BeEmpty();
        }
        else
        {
            (await act()).Should().BeNull();
            warnings.Should().ContainSingle().Which.Should().Be(
                "AutoPublish was requested, but publishing did not complete.");
        }

        publishing.VerifyAll();
        if (!failListing)
        {
            publishing.Verify(service => service.ListPublishedLayersAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task SRV_IMP_002_ReimportingPublishedLayer_RefreshesAndReusesExistingPublication()
    {
        const string connectionString = "Host=unused";
        var existing = new PublishedLayerSummary
        {
            LayerId = 27,
            LayerName = "Parcels",
            Schema = "honua_data",
            Table = "parcels",
            GeometryType = "MULTIPOLYGON",
            Srid = 4326,
            ServiceName = "planning",
            Enabled = true
        };
        var publishing = new Mock<ILayerPublishingService>(MockBehavior.Strict);
        publishing.Setup(service => service.PublishLayerAsync(
                connectionString,
                It.IsAny<LayerPublishRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LayerPublishingException(
                LayerPublishingErrorKind.Conflict,
                "Layer already exists.",
                existing.LayerId));
        publishing.Setup(service => service.RefreshMaterializedFeaturesForSourceTableAsync(
                connectionString,
                existing.Schema,
                existing.Table,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MaterializedFeatureRefreshResult
            {
                LayerId = existing.LayerId,
                LayerName = existing.LayerName,
                Schema = existing.Schema,
                Table = existing.Table,
                MaterializedFeatureCount = 2
            }]);
        publishing.Setup(service => service.RewritePublishedLayerFieldsAsync(
                connectionString,
                existing.LayerId,
                It.IsAny<LayerPublishRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<PublishedLayerSummary?>(null));
        publishing.Setup(service => service.ListPublishedLayersAsync(
                connectionString,
                existing.ServiceName,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([existing]);

        var sut = new GeoservicesLayerPublicationService(
            NullLogger<GeoservicesLayerPublicationService>.Instance,
            publishing.Object);
        var warnings = new List<string>();

        var result = await sut.TryPublishImportedLayerAsync(
            new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/rest/services/Planning/FeatureServer",
                LayerId = 0,
                TableName = existing.Table,
                TargetSchema = existing.Schema,
                TargetSrid = existing.Srid,
                AutoPublish = true,
                ServiceName = existing.ServiceName,
                OverwriteExisting = true
            },
            existing.Schema,
            new GeoservicesLayerInfo
            {
                Id = 0,
                Name = existing.LayerName,
                GeometryType = "esriGeometryPolygon",
                Fields = []
            },
            warnings,
            progress: null,
            jobId: "job-1",
            startedAt: DateTimeOffset.UtcNow,
            featuresProcessed: 2,
            connectionString,
            CancellationToken.None,
            replacingExistingTarget: true);

        result.Should().BeSameAs(existing);
        warnings.Should().NotContain(warning => warning.Contains("publishing did not complete", StringComparison.Ordinal));
        publishing.VerifyAll();
    }

    [Fact]
    public async Task ReimportConflict_RewritesFieldMetadataBeforeRefreshingTheSnapshot()
    {
        const string connectionString = "Host=unused";
        var rewritten = new PublishedLayerSummary
        {
            LayerId = 27,
            LayerName = "Parcels",
            Schema = "honua_data",
            Table = "parcels",
            GeometryType = "MULTIPOLYGON",
            Srid = 4326,
            ServiceName = "planning",
            FieldCount = 4,
            Enabled = true
        };
        var publishing = new Mock<ILayerPublishingService>(MockBehavior.Strict);
        publishing.Setup(service => service.PublishLayerAsync(
                connectionString, It.IsAny<LayerPublishRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LayerPublishingException(LayerPublishingErrorKind.Conflict, "Already published.", rewritten.LayerId));
        publishing.Setup(service => service.RewritePublishedLayerFieldsAsync(
                connectionString, rewritten.LayerId, It.IsAny<LayerPublishRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rewritten);
        publishing.Setup(service => service.RefreshMaterializedFeaturesForSourceTableAsync(
                connectionString, rewritten.Schema, rewritten.Table, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sut = new GeoservicesLayerPublicationService(
            NullLogger<GeoservicesLayerPublicationService>.Instance, publishing.Object);
        var warnings = new List<string>();
        var store = new RecordingImportedAttachmentStore();
        var sourceObjectIds = new Dictionary<long, long> { [2] = 9 };

        var result = await sut.TryPublishImportedLayerAsync(
            ReplaceRequest(), "honua_data", PolygonLayer(), warnings, null, "job-1", DateTimeOffset.UtcNow, 1,
            connectionString, CancellationToken.None, replacingExistingTarget: true,
            replacedSourceObjectIds: sourceObjectIds, importedAttachmentStore: store);

        result.Should().BeSameAs(rewritten);
        result!.FieldCount.Should().Be(4);
        warnings.Should().BeEmpty();
        store.LayerId.Should().Be(rewritten.LayerId);
        store.SourceObjectIds.Should().BeEquivalentTo(sourceObjectIds);
        publishing.Verify(service => service.ListPublishedLayersAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        publishing.VerifyAll();
    }

    [Fact]
    public async Task ReimportConflict_WhenFieldRewriteFails_StillRefreshesAndRemaps()
    {
        const string connectionString = "Host=unused";
        var existing = PublishedParcels();
        var publishing = new Mock<ILayerPublishingService>(MockBehavior.Strict);
        publishing.Setup(service => service.PublishLayerAsync(
                connectionString, It.IsAny<LayerPublishRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LayerPublishingException(LayerPublishingErrorKind.Conflict, "Already published.", existing.LayerId));
        publishing.Setup(service => service.RewritePublishedLayerFieldsAsync(
                connectionString, existing.LayerId, It.IsAny<LayerPublishRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Field catalog rejected the replacement."));
        publishing.Setup(service => service.RefreshMaterializedFeaturesForSourceTableAsync(
                connectionString, existing.Schema, existing.Table, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        publishing.Setup(service => service.ListPublishedLayersAsync(
                connectionString, existing.ServiceName, It.IsAny<CancellationToken>()))
            .ReturnsAsync([existing]);

        var sut = new GeoservicesLayerPublicationService(
            NullLogger<GeoservicesLayerPublicationService>.Instance, publishing.Object);
        var warnings = new List<string>();
        var store = new RecordingImportedAttachmentStore();

        var result = await sut.TryPublishImportedLayerAsync(
            ReplaceRequest(), existing.Schema, PolygonLayer(), warnings, null, "job-1", DateTimeOffset.UtcNow, 1,
            connectionString, CancellationToken.None, replacingExistingTarget: true,
            replacedSourceObjectIds: new Dictionary<long, long> { [1] = 1 }, importedAttachmentStore: store);

        result.Should().BeSameAs(existing);
        warnings.Should().ContainSingle()
            .Which.Should().Be("Published field metadata could not be rewritten for the replaced table.");
        store.Calls.Should().Be(1);
        publishing.VerifyAll();
    }

    [Fact]
    public async Task Reimport_WhenAttachmentRemapFails_ReturnsTheLayerWithAWarning()
    {
        const string connectionString = "Host=unused";
        var published = PublishedParcels();
        var publishing = new Mock<ILayerPublishingService>(MockBehavior.Strict);
        publishing.Setup(service => service.PublishLayerAsync(
                connectionString, It.IsAny<LayerPublishRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(published);
        var store = new RecordingImportedAttachmentStore { Failure = new IOException("attachment table unavailable") };
        var sut = new GeoservicesLayerPublicationService(
            NullLogger<GeoservicesLayerPublicationService>.Instance, publishing.Object);
        var warnings = new List<string>();

        var result = await sut.TryPublishImportedLayerAsync(
            ReplaceRequest(), published.Schema, PolygonLayer(), warnings, null, "job-1", DateTimeOffset.UtcNow, 1,
            connectionString, CancellationToken.None, replacingExistingTarget: true,
            replacedSourceObjectIds: new Dictionary<long, long> { [2] = 1 }, importedAttachmentStore: store);

        result.Should().BeSameAs(published);
        warnings.Should().ContainSingle()
            .Which.Should().Be("Existing attachments could not be remapped onto the replaced object ids.");
        publishing.Verify(service => service.RewritePublishedLayerFieldsAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<LayerPublishRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        publishing.VerifyAll();
    }

    [Fact]
    public async Task ReimportConflict_WhenAttachmentRemapIsCancelled_PropagatesWithoutAWarning()
    {
        const string connectionString = "Host=unused";
        var rewritten = PublishedParcels();
        var publishing = new Mock<ILayerPublishingService>(MockBehavior.Strict);
        publishing.Setup(service => service.PublishLayerAsync(
                connectionString, It.IsAny<LayerPublishRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LayerPublishingException(LayerPublishingErrorKind.Conflict, "Already published.", rewritten.LayerId));
        publishing.Setup(service => service.RewritePublishedLayerFieldsAsync(
                connectionString, rewritten.LayerId, It.IsAny<LayerPublishRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rewritten);
        publishing.Setup(service => service.RefreshMaterializedFeaturesForSourceTableAsync(
                connectionString, rewritten.Schema, rewritten.Table, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var cancellation = new OperationCanceledException();
        var store = new RecordingImportedAttachmentStore { Failure = cancellation };
        var sut = new GeoservicesLayerPublicationService(
            NullLogger<GeoservicesLayerPublicationService>.Instance, publishing.Object);
        var warnings = new List<string>();

        var act = () => sut.TryPublishImportedLayerAsync(
            ReplaceRequest(), rewritten.Schema, PolygonLayer(), warnings, null, "job-1", DateTimeOffset.UtcNow, 1,
            connectionString, CancellationToken.None, replacingExistingTarget: true,
            replacedSourceObjectIds: new Dictionary<long, long> { [2] = 1 }, importedAttachmentStore: store);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(cancellation);
        warnings.Should().BeEmpty();
        publishing.VerifyAll();
    }

    private static GeoservicesImportRequest ReplaceRequest() => new()
    {
        ServiceUrl = "https://example.com/rest/services/Planning/FeatureServer",
        LayerId = 0,
        TableName = "parcels",
        ServiceName = "planning",
        AutoPublish = true,
        OverwriteExisting = true
    };

    private static GeoservicesLayerInfo PolygonLayer() => new()
    {
        Id = 0,
        Name = "Parcels",
        GeometryType = "esriGeometryPolygon",
        Fields = []
    };

    private static PublishedLayerSummary PublishedParcels() => new()
    {
        LayerId = 27,
        LayerName = "Parcels",
        Schema = "honua_data",
        Table = "parcels",
        GeometryType = "MULTIPOLYGON",
        Srid = 4326,
        ServiceName = "planning",
        Enabled = true
    };

    private sealed class RecordingImportedAttachmentStore : IImportedAttachmentStore
    {
        public int Calls { get; private set; }

        public int? LayerId { get; private set; }

        public IReadOnlyDictionary<long, long>? SourceObjectIds { get; private set; }

        public Exception? Failure { get; init; }

        public Task RemapImportedAttachmentsAsync(
            int layerId,
            IReadOnlyDictionary<long, long> sourceObjectIds,
            CancellationToken cancellationToken)
        {
            Calls++;
            LayerId = layerId;
            SourceObjectIds = sourceObjectIds;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }

        public Task<bool> HasLegacyAttachmentsAsync(int layerId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Attachment> UploadImportedAsync(
            int layerId, long featureId, string source, long sourceParentId, long sourceAttachmentId,
            Guid generation, string filename, string contentType, Stream content, string? keywords,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> CompleteImportAsync(int layerId, Guid generation, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
