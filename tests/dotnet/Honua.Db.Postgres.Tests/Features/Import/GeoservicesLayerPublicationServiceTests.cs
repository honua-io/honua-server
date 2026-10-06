// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed class GeoservicesLayerPublicationServiceTests
{
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
}
