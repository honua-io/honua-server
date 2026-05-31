// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Verifies the manifest-to-apply-request translation in
/// <see cref="GeoservicesImportService.ApplyRelationshipsAsync"/> (issue #1256).
/// The actual persistence is exercised by the writer integration tests; here we
/// mock the catalog writer and assert that the apply step routes the right
/// Honua layer ids, foreign-key fields, and skip outcomes through it.
/// </summary>
public sealed class GeoservicesRelationshipApplyTests
{
    [Fact]
    public async Task ApplyRelationshipsAsync_AutomatedRelationshipWithResolvedLayers_RoutesApplyRequest()
    {
        MigrationRelationshipApplyRequest[]? captured = null;
        var writer = new Mock<IMigrationCatalogWriter>(MockBehavior.Strict);
        writer.Setup(w => w.EnsureRelationshipsAsync(
                It.IsAny<string>(),
                It.IsAny<IMetadataV2GraphStore?>(),
                It.IsAny<MigrationRelationshipApplyRequest[]>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, IMetadataV2GraphStore?, MigrationRelationshipApplyRequest[], CancellationToken>(
                (_, _, requests, _) =>
                {
                    captured = requests;
                    return Task.FromResult(requests.Select(r => new MigrationRelationshipApplyOutcome
                    {
                        SourceRelationshipId = r.SourceRelationshipId,
                        Outcome = MigrationCatalogWriteOutcome.Created,
                        Message = "applied",
                        TargetRelationshipRef = $"rel-{r.OriginLayerId}-{r.EsriRelationshipId ?? 0}"
                    }).ToArray());
                });

        var service = CreateService(writer.Object);
        var manifest = BuildManifestWithRelationship(
            originResourceId: "resource:Inspections:layer:0",
            relatedSourceLayerToken: "layer:1",
            cardinality: "1:N",
            esriRelationshipId: 3,
            classification: MigrationManifestRelationshipClassifications.Assisted);

        var outcomes = await service.ApplyRelationshipsAsync(
            manifest,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["resource:Inspections:layer:0"] = 200,
                ["resource:Inspections:layer:1"] = 201
            },
            graphStore: null,
            CancellationToken.None);

        outcomes.Should().ContainSingle().Which.Outcome.Should().Be(MigrationCatalogWriteOutcome.Created);
        captured.Should().NotBeNull();
        var request = captured!.Should().ContainSingle().Subject;
        request.OriginLayerId.Should().Be(200);
        request.RelatedLayerId.Should().Be(201);
        request.OriginKeyField.Should().Be("INSPECTION_ID");
        request.DestinationKeyField.Should().Be("INSPECTION_ID");
        request.EsriRelationshipId.Should().Be(3);
        request.Cardinality.Should().Be("1:N");
        request.Role.Should().Be("esriRelRoleOrigin");
    }

    [Fact]
    public async Task ApplyRelationshipsAsync_RelatedLayerNotInPublishedMap_RecordsSkippedOutcome()
    {
        var writer = new Mock<IMigrationCatalogWriter>(MockBehavior.Strict);
        // Strict: the writer must NOT be called when both sides cannot be resolved.

        var service = CreateService(writer.Object);
        var manifest = BuildManifestWithRelationship(
            originResourceId: "resource:Inspections:layer:0",
            relatedSourceLayerToken: "layer:99",
            cardinality: "1:N",
            esriRelationshipId: 3,
            classification: MigrationManifestRelationshipClassifications.Assisted);

        var outcomes = await service.ApplyRelationshipsAsync(
            manifest,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["resource:Inspections:layer:0"] = 200
            },
            graphStore: null,
            CancellationToken.None);

        outcomes.Should().ContainSingle()
            .Which.Message.Should().Contain("Related layer", "the related layer was not in the published map");
        writer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ApplyRelationshipsAsync_ManualReviewClassification_IsSkipped()
    {
        var writer = new Mock<IMigrationCatalogWriter>(MockBehavior.Strict);
        var service = CreateService(writer.Object);
        var manifest = BuildManifestWithRelationship(
            originResourceId: "resource:Inspections:layer:0",
            relatedSourceLayerToken: "layer:1",
            cardinality: "1:N",
            esriRelationshipId: 5,
            classification: MigrationManifestRelationshipClassifications.ManualReview);

        var outcomes = await service.ApplyRelationshipsAsync(
            manifest,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["resource:Inspections:layer:0"] = 200,
                ["resource:Inspections:layer:1"] = 201
            },
            graphStore: null,
            CancellationToken.None);

        outcomes.Should().BeEmpty();
        writer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ApplyRelationshipsAsync_NonArcGisSource_ReturnsEmpty()
    {
        var writer = new Mock<IMigrationCatalogWriter>(MockBehavior.Strict);
        var service = CreateService(writer.Object);
        var manifest = new MigrationManifestArtifact
        {
            SourceKind = "geoserver-rest",
            Source = new MigrationSourceIdentity { DisplayName = "GeoServer", BaseUrl = "http://example.com/geoserver" },
            Summary = new MigrationManifestSummary()
        };

        var outcomes = await service.ApplyRelationshipsAsync(
            manifest,
            new Dictionary<string, int>(StringComparer.Ordinal),
            graphStore: null,
            CancellationToken.None);

        outcomes.Should().BeEmpty();
        writer.VerifyNoOtherCalls();
    }

    private static MigrationManifestArtifact BuildManifestWithRelationship(
        string originResourceId,
        string relatedSourceLayerToken,
        string cardinality,
        int esriRelationshipId,
        string classification)
    {
        var compatibility = new MigrationCompatibilityAssessment
        {
            Level = "compatible",
            Reason = "Test"
        };
        var origin = new MigrationManifestTargetResource
        {
            SourceResourceId = originResourceId,
            SourceKind = "layer",
            Action = "publish",
            TargetResourceId = "target:resource:inspections:inspection-points",
            TargetServiceName = "inspections",
            TargetResourceName = "inspection-points",
            Relationships =
            [
                new MigrationManifestRelationshipRecord
                {
                    SourceRelationshipId = esriRelationshipId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    SourceResourceId = originResourceId,
                    Name = "Photos",
                    Cardinality = cardinality,
                    RelationshipType = "simple",
                    RelatedLayerIds = [relatedSourceLayerToken],
                    Role = "esriRelRoleOrigin",
                    OriginKeyField = "INSPECTION_ID",
                    DestinationKeyField = "INSPECTION_ID",
                    EsriRelationshipId = esriRelationshipId,
                    Classification = classification,
                    TargetRelationshipRef = classification != MigrationManifestRelationshipClassifications.ManualReview
                        ? $"target:relationship:inspections:inspection-points:{esriRelationshipId}"
                        : null
                }
            ],
            Compatibility = compatibility
        };
        var related = new MigrationManifestTargetResource
        {
            SourceResourceId = "resource:Inspections:layer:1",
            SourceKind = "table",
            Action = "publish",
            TargetResourceId = "target:resource:inspections:inspection-photos",
            TargetServiceName = "inspections",
            TargetResourceName = "inspection-photos",
            Compatibility = compatibility
        };

        return new MigrationManifestArtifact
        {
            SourceKind = "arcgis-geoservices-rest",
            Source = new MigrationSourceIdentity
            {
                DisplayName = "Inspections",
                BaseUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer"
            },
            Summary = new MigrationManifestSummary
            {
                SourceResourceCount = 2,
                TargetResourceCount = 2
            },
            TargetResources = [origin, related]
        };
    }

    private static GeoservicesImportService CreateService(IMigrationCatalogWriter catalogWriter)
    {
        var httpClient = new HttpClient(new NoopHandler());
        var restClient = new ArcGisRestClient(
            httpClient,
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Loose);
        connectionProvider.Setup(p => p.GetConnectionString()).Returns("Host=localhost;Database=test");
        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Loose);

        return new GeoservicesImportService(
            restClient,
            connectionProvider.Object,
            crsRegistry.Object,
            new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors),
            NullLogger<GeoservicesImportService>.Instance,
            layerPublishingService: null,
            catalogWriter: catalogWriter);
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented));
    }
}
