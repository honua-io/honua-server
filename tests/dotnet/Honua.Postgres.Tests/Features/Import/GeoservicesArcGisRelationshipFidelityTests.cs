// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
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
/// Targeted fixtures for the per-relationship fidelity extraction added by
/// issue #1256. The High Fidelity baseline asserts the simple (id,name,relatedTableId)
/// case; these fixtures cover the explicit metadata-rich cases that drive the
/// translator+apply pipeline:
/// <list type="bullet">
///   <item>Simple relationship with cardinality + role + keyField yields Automated</item>
///   <item>Composite relationship downgrades to ManualReview</item>
///   <item>Many-to-many relationship downgrades to ManualReview</item>
/// </list>
/// </summary>
public sealed class GeoservicesArcGisRelationshipFidelityTests
{
    [Fact]
    public async Task ScanSourceAsync_SimpleRelationshipWithCardinalityAndKeyField_EmitsAutomatedFidelity()
    {
        var responses = BuildResponses(
            relationshipsJson: "[{\"id\":2,\"name\":\"InspectionPhotos\",\"cardinality\":\"esriRelCardinalityOneToMany\",\"role\":\"esriRelRoleOrigin\",\"keyField\":\"INSPECTION_ID\",\"relatedTableId\":3}]");
        var service = CreateService(new FixtureHttpHandler(responses));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
            TimeoutSeconds = 5
        });

        var resource = artifact.Resources.Should().ContainSingle().Subject;
        var record = artifact.FidelityClassifications.Should().ContainSingle(r =>
            r.SourceId == resource.Id && r.Category == "relationships").Subject;
        record.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Automated);
        record.Code.Should().Be(ImportCompatibilityCodes.Compatible);
        record.Id.Should().Be($"classification:{resource.Id}:relationship:2");
        record.Metadata["relationshipId"].Should().Be("2");
        record.Metadata["name"].Should().Be("InspectionPhotos");
        record.Metadata["cardinality"].Should().Be("esriRelCardinalityOneToMany");
        record.Metadata["role"].Should().Be("esriRelRoleOrigin");
        record.Metadata["originKeyField"].Should().Be("INSPECTION_ID");
        record.Metadata["destinationKeyField"].Should().Be("INSPECTION_ID");
        record.Metadata["relatedLayerIds"].Should().Be("layer:3");
    }

    [Fact]
    public async Task ScanSourceAsync_CompositeRelationship_EmitsManualReviewFidelity()
    {
        var responses = BuildResponses(
            relationshipsJson: "[{\"id\":4,\"name\":\"OwnsParts\",\"cardinality\":\"esriRelCardinalityOneToMany\",\"role\":\"esriRelRoleOrigin\",\"keyField\":\"PARENT_ID\",\"relatedTableId\":5,\"composite\":true}]");
        var service = CreateService(new FixtureHttpHandler(responses));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
            TimeoutSeconds = 5
        });

        var resource = artifact.Resources.Should().ContainSingle().Subject;
        var record = artifact.FidelityClassifications.Should().ContainSingle(r =>
            r.SourceId == resource.Id && r.Category == "relationships").Subject;
        record.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.ManualReview);
        record.Code.Should().Be(ImportCompatibilityCodes.ArcGisRelationshipsManualReview);
        record.Metadata["relationshipType"].Should().Be("composite");
    }

    [Fact]
    public async Task ScanSourceAsync_ManyToManyRelationship_EmitsManualReviewFidelity()
    {
        var responses = BuildResponses(
            relationshipsJson: "[{\"id\":6,\"name\":\"JunctionLink\",\"cardinality\":\"esriRelCardinalityManyToMany\",\"role\":\"esriRelRoleOrigin\",\"keyField\":\"LINK_ID\",\"relatedTableId\":7}]");
        var service = CreateService(new FixtureHttpHandler(responses));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
            TimeoutSeconds = 5
        });

        var resource = artifact.Resources.Should().ContainSingle().Subject;
        var record = artifact.FidelityClassifications.Should().ContainSingle(r =>
            r.SourceId == resource.Id && r.Category == "relationships").Subject;
        record.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.ManualReview);
        record.Code.Should().Be(ImportCompatibilityCodes.ArcGisRelationshipsManualReview);
        record.Metadata["cardinality"].Should().Be("esriRelCardinalityManyToMany");
    }

    [Fact]
    public async Task ScanSourceAsync_MultipleRelationshipsOnSameLayer_EmitsOneRecordPerEntry()
    {
        var responses = BuildResponses(
            relationshipsJson:
                "[{\"id\":2,\"name\":\"Photos\",\"cardinality\":\"esriRelCardinalityOneToMany\",\"role\":\"esriRelRoleOrigin\",\"keyField\":\"INSPECTION_ID\",\"relatedTableId\":3}," +
                "{\"id\":4,\"name\":\"OwnsParts\",\"cardinality\":\"esriRelCardinalityOneToMany\",\"role\":\"esriRelRoleOrigin\",\"keyField\":\"PARENT_ID\",\"relatedTableId\":5,\"composite\":true}]");
        var service = CreateService(new FixtureHttpHandler(responses));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
            TimeoutSeconds = 5
        });

        var resource = artifact.Resources.Should().ContainSingle().Subject;
        var records = artifact.FidelityClassifications
            .Where(r => r.SourceId == resource.Id && r.Category == "relationships")
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToArray();
        records.Should().HaveCount(2);
        records[0].Id.Should().Be($"classification:{resource.Id}:relationship:2");
        records[0].AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Automated);
        records[1].Id.Should().Be($"classification:{resource.Id}:relationship:4");
        records[1].AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.ManualReview);
    }

    [Fact]
    public async Task ScanSourceAsync_RelationshipInfosArray_IsHonouredAsAlternateSpelling()
    {
        // Some MapServer layers expose relationships under "relationshipInfos"
        // instead of "relationships"; the scanner accepts both.
        var responses = BuildResponses(
            relationshipsJson: "[]",
            relationshipInfosJson: "[{\"id\":9,\"name\":\"Lineage\",\"cardinality\":\"esriRelCardinalityOneToOne\",\"role\":\"esriRelRoleOrigin\",\"keyField\":\"PREDECESSOR_ID\",\"relatedTableId\":4}]");
        var service = CreateService(new FixtureHttpHandler(responses));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
            TimeoutSeconds = 5
        });

        var resource = artifact.Resources.Should().ContainSingle().Subject;
        var record = artifact.FidelityClassifications.Should().ContainSingle(r =>
            r.SourceId == resource.Id && r.Category == "relationships").Subject;
        record.Metadata["relationshipId"].Should().Be("9");
        record.Metadata["cardinality"].Should().Be("esriRelCardinalityOneToOne");
        record.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Automated);
    }

    private static Dictionary<string, string> BuildResponses(
        string relationshipsJson,
        string? relationshipInfosJson = null)
    {
        var rootJson = "{" +
            "\"currentVersion\":11.2," +
            "\"serviceDescription\":\"Relationships Test\"," +
            "\"capabilities\":\"Query\"," +
            "\"layers\":[{\"id\":0,\"name\":\"Inspections\"}]" +
            "}";

        var relationshipFragment = relationshipInfosJson == null
            ? $"\"relationships\":{relationshipsJson}"
            : $"\"relationships\":{relationshipsJson},\"relationshipInfos\":{relationshipInfosJson}";

        var layerJson = "{" +
            "\"id\":0,\"name\":\"Inspections\"," +
            "\"geometryType\":\"esriGeometryPoint\"," +
            "\"capabilities\":\"Query\"," +
            "\"spatialReference\":{\"wkid\":3857}," +
            relationshipFragment + "," +
            "\"drawingInfo\":{\"renderer\":{\"type\":\"simple\"}}," +
            "\"fields\":[{\"name\":\"OBJECTID\",\"type\":\"esriFieldTypeOID\"}]" +
            "}";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/arcgis/rest/services/Inspections/FeatureServer?f=json"] = rootJson,
            ["/arcgis/rest/services/Inspections/FeatureServer/0?f=json"] = layerJson,
            ["/arcgis/rest/services/Inspections/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json"] = "{\"count\":1}"
        };
    }

    private static GeoservicesImportService CreateService(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var restClient = new ArcGisRestClient(
            httpClient,
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Loose);
        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Loose);
        crsRegistry.Setup(registry => registry.ResolveBySridAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int srid, CancellationToken _) => new ValueTask<CrsDefinition?>(
                srid == 3857
                    ? new CrsDefinition("http://www.opengis.net/def/crs/EPSG/0/3857", 3857, AxisOrder.EastNorth, false)
                    : null));

        return new GeoservicesImportService(
            restClient,
            connectionProvider.Object,
            crsRegistry.Object,
            new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors),
            NullLogger<GeoservicesImportService>.Instance);
    }

    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses;

        public FixtureHttpHandler(Dictionary<string, string> responses)
        {
            _responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (!_responses.TryGetValue(pathAndQuery, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"error\":{\"code\":404,\"message\":\"no fixture\"}}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
