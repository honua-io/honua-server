// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// Issue #4600, acceptance criterion 1, end to end over the real scan: a FeatureServer source is scanned by
/// <see cref="GeoservicesImportService.ScanSourceAsync"/>, translated to a manifest, and every discovered
/// construct is accounted against a service selection before apply. The source serves a point layer with
/// attachments, one simple and one composite relationship, a nonspatial table and a multipatch layer; the
/// expected accounting follows from that source, not from the accountant's output.
/// </summary>
public sealed class GeoservicesServiceConstructAccountingScanTests
{
    private const string ServiceUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer";
    private const string Inspections = "resource:Inspections:layer:0";
    private const string Notes = "resource:Inspections:table:1";
    private const string Surveys = "resource:Inspections:layer:2";

    [Fact]
    public async Task ScannedService_WithTheMultipatchLayerLeftOut_BlocksOnTheUnselectedLayerAndReviewsTheCompositeRelationship()
    {
        var manifest = await ScanAndTranslateAsync();

        var report = MigrationServiceConstructAccountant.Account(
            manifest,
            [Select(Inspections, "field_ops.inspections"), Select(Notes, "field_ops.inspection_notes")]);

        report.Executed.Should().BeTrue();
        report.DiscoveredResourceCount.Should().Be(3, "the service root lists two layers and one table");
        report.SelectedResourceCount.Should().Be(2);
        report.IsBlocking.Should().BeTrue();
        report.Differences.Select(difference => (difference.Code, difference.Severity, difference.Subject, difference.Actual))
            .Should().Equal(
                (MigrationFidelityDifferenceCodes.ConstructReviewRequired, MigrationFidelityDifferenceSeverities.Unverified,
                    Inspections, "manual-review (ARCGIS_RELATIONSHIPS_MANUAL_REVIEW)"),
                (MigrationFidelityDifferenceCodes.ServiceResourceUnselected, MigrationFidelityDifferenceSeverities.Blocking,
                    Surveys, "not selected"));

        // The multipatch layer is accounted construct by construct even though nothing selects it.
        report.Entries.Where(entry => entry.SourceId == Surveys)
            .Should().NotBeEmpty()
            .And.OnlyContain(entry => entry.Disposition == MigrationConstructDispositions.Unselected);
        report.Entries.Where(entry => entry.SourceId == Surveys).Select(entry => entry.Construct)
            .Should().Contain(["service.resource", "resource.geometry"]);

        // Source-to-target mapping for the selected layer comes from the manifest and the selection.
        var inspections = report.Entries.Single(entry => entry.SourceId == Inspections && entry.Construct == "service.resource");
        inspections.Disposition.Should().Be(MigrationConstructDispositions.Migrated);
        inspections.TargetTable.Should().Be("field_ops.inspections");
        inspections.TargetResourceId.Should().Be(manifest.TargetResources.Single(target => target.SourceResourceId == Inspections).TargetResourceId);
        report.Entries.Single(entry => entry.SourceId == Inspections && entry.Construct == "resource.attachments")
            .Should().Match<MigrationConstructAccountingEntry>(entry =>
                entry.Disposition == MigrationConstructDispositions.Migrated &&
                entry.Verification == MigrationConstructVerifications.AttachmentReconciliation);
        report.Entries.Where(entry => entry.SourceId == Inspections && entry.Construct == "resource.relationships")
            .Select(entry => entry.Disposition)
            .Should().BeEquivalentTo([MigrationConstructDispositions.Migrated, MigrationConstructDispositions.Review]);
        report.Entries.Where(entry => entry.SourceId == Notes).Select(entry => entry.Construct)
            .Should().NotContain("resource.geometry", "a nonspatial table has no geometry to account");
        report.Entries.Single(entry => entry.Construct == "service.type").Codes.Should().Equal("FeatureServer");
    }

    [Fact]
    public async Task ScannedService_WithEveryResourceSelected_BlocksOnTheUnsupportedMultipatchGeometry()
    {
        var manifest = await ScanAndTranslateAsync();

        var report = MigrationServiceConstructAccountant.Account(
            manifest,
            [
                Select(Inspections, "field_ops.inspections"),
                Select(Notes, "field_ops.inspection_notes"),
                Select(Surveys, "field_ops.surveys")
            ]);

        report.Differences.Select(difference => (difference.Code, difference.Subject, difference.Actual))
            .Should().Equal(
                (MigrationFidelityDifferenceCodes.ConstructReviewRequired, Inspections, "manual-review (ARCGIS_RELATIONSHIPS_MANUAL_REVIEW)"),
                (MigrationFidelityDifferenceCodes.ConstructUnsupported, Surveys, "unsupported (ARCGIS_UNSUPPORTED_GEOMETRY)"));
        report.Entries.Single(entry => entry.SourceId == Surveys && entry.Construct == "resource.geometry")
            .Disposition.Should().Be(MigrationConstructDispositions.Blocker);
    }

    private static async Task<MigrationManifestArtifact> ScanAndTranslateAsync()
    {
        var service = CreateService(new FixtureHttpHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/arcgis/rest/services/Inspections/FeatureServer?f=json"] = """
                {"currentVersion":11.2,"serviceDescription":"Inspections","capabilities":"Query",
                 "layers":[{"id":0,"name":"Inspections"},{"id":2,"name":"Surveys3D"}],
                 "tables":[{"id":1,"name":"InspectionNotes"}]}
                """,
            ["/arcgis/rest/services/Inspections/FeatureServer/0?f=json"] = """
                {"id":0,"name":"Inspections","geometryType":"esriGeometryPoint","capabilities":"Query",
                 "hasAttachments":true,"spatialReference":{"wkid":3857},
                 "relationships":[
                   {"id":1,"name":"InspectionNotes","relatedTableId":1,"cardinality":"esriRelCardinalityOneToMany","role":"esriRelRoleOrigin","keyField":"GlobalID"},
                   {"id":2,"name":"InspectionOwner","relatedTableId":1,"composite":true,"cardinality":"esriRelCardinalityOneToMany","role":"esriRelRoleOrigin","keyField":"GlobalID"}],
                 "fields":[{"name":"OBJECTID","type":"esriFieldTypeOID"},{"name":"GlobalID","type":"esriFieldTypeGlobalID"}]}
                """,
            ["/arcgis/rest/services/Inspections/FeatureServer/1?f=json"] = """
                {"id":1,"name":"InspectionNotes","capabilities":"Query",
                 "fields":[{"name":"OBJECTID","type":"esriFieldTypeOID"},{"name":"INSPECTION_GUID","type":"esriFieldTypeGUID"}]}
                """,
            ["/arcgis/rest/services/Inspections/FeatureServer/2?f=json"] = """
                {"id":2,"name":"Surveys3D","geometryType":"esriGeometryMultiPatch","capabilities":"Query",
                 "spatialReference":{"wkid":3857},"fields":[{"name":"OBJECTID","type":"esriFieldTypeOID"}]}
                """,
            ["/arcgis/rest/services/Inspections/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json"] = """{"count":12}""",
            ["/arcgis/rest/services/Inspections/FeatureServer/1/query?where=1%3D1&returnCountOnly=true&f=json"] = """{"count":30}""",
            ["/arcgis/rest/services/Inspections/FeatureServer/2/query?where=1%3D1&returnCountOnly=true&f=json"] = """{"count":4}"""
        }));

        var inventory = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest { ServiceUrl = ServiceUrl, TimeoutSeconds = 5 });

        inventory.ScanCompleteness.Warnings.Should().NotContain(warning => warning.StartsWith("Failed to scan", StringComparison.Ordinal));
        return MigrationManifestTranslator.Translate(inventory);
    }

    private static MigrationConstructSelection Select(string sourceResourceId, string targetTable)
        => new() { SourceResourceId = sourceResourceId, TargetTable = targetTable };

    private static GeoservicesImportService CreateService(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var restClient = new ArcGisRestClient(
            httpClient,
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var connectionProvider = new Mock<IAdoNetDatabaseConnectionProvider>(MockBehavior.Strict);
        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Strict);

        crsRegistry.Setup(registry => registry.ResolveBySridAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int srid, CancellationToken _) => new ValueTask<CrsDefinition?>(
                srid switch
                {
                    3857 => new CrsDefinition("http://www.opengis.net/def/crs/EPSG/0/3857", 3857, AxisOrder.EastNorth, false),
                    _ => null
                }));

        return new GeoservicesImportService(
            restClient,
            connectionProvider.Object,
            crsRegistry.Object,
            new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors),
            NullLogger<GeoservicesImportService>.Instance,
            new GeoservicesLayerPublicationService(NullLogger<GeoservicesLayerPublicationService>.Instance));
    }

    private sealed class FixtureHttpHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (!responses.TryGetValue(pathAndQuery, out var body))
            {
                throw new InvalidOperationException($"Fixture has no response for {pathAndQuery}.");
            }

            // Ownership of the HttpResponseMessage transfers to the HttpClient pipeline that invokes
            // this handler; it is disposed by the caller, not here (cs/local-not-disposed false positive).
            return Task.FromResult<HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
