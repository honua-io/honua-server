// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using static Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer.ImageServerMultidimensionalSoapAssertions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerMultidimensionalSoapTests
{
    private static readonly double[] FixtureTimeDays = [45292, 45293];
    private static readonly double[] FixtureDepths = [-10, -5, 0];

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [InterfaceOperation(TestProtocols.ImageServer, "GetMultidimensionalInfo")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    [Endpoint("GET /rest/services/{id}/ImageServer/multidimensionalInfo")]
    public async Task PersistedCoverage_SoapPropertySetsPreserveSqlAndRestSemantics()
    {
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await fixture.InitializeAsync();
        try
        {
            // Production auth remains enabled: declare a legitimately public baseline
            // before checking the independent resource and publication denial paths.
            fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, accessPolicy: new AccessPolicy { AllowAnonymous = true });
            // The numeric REST alias also has a feature publication in this shared seed.
            // Make that independent comparison public; SOAP policy transitions below
            // must target the distinct EsriImageLayer resource by publication identity.
            fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, accessPolicy: new AccessPolicy { AllowAnonymous = true });
            SetImagePublicationState(fixture, allowAnonymous: true);
            await fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
            {
                using var scope = fixture.Services.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IMultidimensionalCoverageStore>();
                var registration = await store.RegisterAsync(Registration());
                try
                {
                    foreach (var set in AssertShape(await PostAsync(fixture)))
                    {
                        Child(set, "PropertyArray").Elements().Should().BeEmpty("an unscanned registration has no advertised variables");
                    }
                    await store.UpdateMetadataAsync(registration.Id, Metadata());
                    await using (var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!))
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = """
                    SELECT metadata #>> '{variables,0,name}', metadata #>> '{variables,0,noData}',
                        metadata #>> '{temporal,start}', metadata #>> '{temporal,end}',
                        metadata #>> '{temporal,stepCount}'
                    FROM honua.multidim_coverage_catalog WHERE id = @id
                    """;
                        command.Parameters.AddWithValue("id", registration.Id);
                        await using var reader = await command.ExecuteReaderAsync();
                        (await reader.ReadAsync()).Should().BeTrue();
                        reader.GetString(0).Should().Be("temperature");
                        reader.GetString(1).Should().Be("-9999");
                        DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture)
                            .Should().Be(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
                        DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)
                            .Should().Be(new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero));
                        reader.GetString(4).Should().Be("2");
                    }
                    using var restResponse = await fixture.Client.GetAsync(
                        $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/multidimensionalInfo?f=json");
                    var restBody = await restResponse.Content.ReadAsStringAsync();
                    restResponse.StatusCode.Should().Be(HttpStatusCode.OK, restBody);
                    using var rest = JsonDocument.Parse(restBody);
                    rest.RootElement.TryGetProperty("error", out _).Should().BeFalse(restBody);
                    var variables = rest.RootElement.GetProperty("multidimensionalInfo").GetProperty("variables");
                    variables.GetArrayLength().Should().Be(2);
                    var restTime = variables[0].GetProperty("dimensions")[0].GetProperty("values");
                    restTime[0].GetDouble().Should().Be(1_704_067_200_000);
                    restTime[1].GetDouble().Should().Be(1_704_153_600_000);

                    var sets = AssertShape(await PostAsync(fixture));
                    Property(Property(sets[0], "temperature"), "Unit").Value.Should().Be("degC");
                    Property(Property(sets[0], "temperature"), "Description").Value.Should().Be("Water temperature");
                    foreach (var bounds in Property(Property(sets[2], "temperature"), "StdTime").Elements())
                    {
                        bounds.Elements().Select(Number).Should().Equal(FixtureTimeDays);
                    }
                    foreach (var bounds in Property(Property(sets[2], "temperature"), "StdZ").Elements())
                    {
                        bounds.Elements().Select(Number).Should().Equal(FixtureDepths);
                    }
                    var time = Property(Property(sets[1], "temperature"), "StdTime");
                    Number(Property(time, "Count")).Should().Be(2);
                    Number(Property(time, "Interval")).Should().Be(1);
                    Property(time, "IntervalUnit").Value.Should().Be("Days");
                    Property(time, "HasRegularIntervals").Value.Should().Be("true");
                    Child(Property(sets[2], "salinity"), "PropertyArray").Elements().Should().BeEmpty();

                    SetImagePublicationState(fixture, allowAnonymous: false);
                    await PostAsync(fixture, HttpStatusCode.Unauthorized);
                    SetImagePublicationState(fixture, allowAnonymous: true, enabled: false);
                    await PostAsync(fixture, HttpStatusCode.NotFound);
                    SetImagePublicationState(fixture, allowAnonymous: true);
                    (await store.UnregisterAsync(registration.Id)).Should().BeTrue();
                    foreach (var set in AssertShape(await PostAsync(fixture)))
                    {
                        Child(set, "PropertyArray").Elements().Should().BeEmpty();
                    }
                }
                finally { await store.UnregisterAsync(registration.Id); }
            });
        }
        finally { await fixture.DisposeAsync(); }
    }

    private static void SetImagePublicationState(WebAppFixture fixture, bool allowAnonymous, bool enabled = true)
    {
        var snapshot = fixture.GetCurrentV2GraphSnapshot();
        var publication = snapshot.Graph.Publications.Single(candidate =>
            candidate.PublicationType == MetadataV2PublicationType.EsriImageLayer &&
            snapshot.Index.ServicesById.TryGetValue(candidate.ServiceId, out var owningService) &&
            owningService.Metadata.Name == WebAppFixture.TestServiceId &&
            owningService.Protocols.Contains(ServiceProtocols.ImageServer, StringComparer.OrdinalIgnoreCase));
        var service = snapshot.Index.ServicesById[publication.ServiceId];
        publication.LayerIndex.Should().Be(WebAppFixture.TestLayerId);
        snapshot.ResolveStorageLayerId(publication).Should().Be(WebAppFixture.TestLayerId);
        var resource = snapshot.ResolveResource(publication);
        resource.Should().NotBeNull();
        resource!.Type.Should().Be(MetadataV2ResourceType.RasterDataset);
        var status = new MetadataV2Status
        {
            Lifecycle = enabled ? MetadataV2LifecycleStatus.Active : MetadataV2LifecycleStatus.Retired,
            State = MetadataV2OperationalState.Ready
        };
        var graph = snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Services = snapshot.Graph.Services.Select(candidate => candidate.Metadata.Id == service.Metadata.Id
                ? candidate with { AccessPolicy = new AccessPolicy { AllowAnonymous = true } } : candidate).ToArray(),
            Resources = snapshot.Graph.Resources.Select(candidate => candidate.Metadata.Id == resource.Metadata.Id
                ? candidate with { AccessPolicy = new AccessPolicy { AllowAnonymous = allowAnonymous } } : candidate).ToArray(),
            Publications = snapshot.Graph.Publications.Select(candidate => candidate.Metadata.Id == publication.Metadata.Id
                ? candidate with { Status = status } : candidate).ToArray()
        };
        fixture.GetService<TestMetadataV2GraphProvider>().SetGraph(graph, schema: fixture.CurrentSchema);
        var updated = fixture.GetCurrentV2GraphSnapshot();
        var updatedPublication = updated.Graph.Publications.Single(candidate => candidate.Metadata.Id == publication.Metadata.Id);
        updatedPublication.ServiceId.Should().Be(service.Metadata.Id);
        updatedPublication.ResourceId.Should().Be(resource.Metadata.Id);
        updated.ResolveStorageLayerId(updatedPublication).Should().Be(WebAppFixture.TestLayerId);
        updated.ResolveResource(updatedPublication)!.AccessPolicy!.AllowAnonymous.Should().Be(allowAnonymous);
        updated.IsRoutable(updatedPublication).Should().Be(enabled);
    }

    private static MultidimensionalCoverageRegistrationRequest Registration() => new()
    {
        LayerId = WebAppFixture.TestLayerId,
        Name = "soap-md-profile",
        Format = MultidimensionalCoverageFormat.NetCdf4,
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "owned-test",
        ObjectKey = "soap-md-profile.nc",
        Variables = []
    };

    private static MultidimensionalCoverageMetadata Metadata() => new()
    {
        Format = MultidimensionalCoverageFormat.NetCdf4,
        Srid = 4326,
        Extent = new RasterExtent { XMin = -122.5, YMin = 37.7, XMax = -122.35, YMax = 37.84, Srid = 4326 },
        Temporal = new TemporalExtent(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero), StepCount: 2),
        Vertical = new VerticalExtent(-10, 0, StepCount: 3, Units: "Meters"),
        Variables =
        [
            new MultidimensionalCoverageVariable("temperature", "float32",
                [new MultidimensionalCoverageDimension("time", 2), new MultidimensionalCoverageDimension("depth", 3)],
                ChunkLayout: null, Units: "degC", LongName: "Water temperature", StandardName: "water_temperature", NoData: -9999),
            new MultidimensionalCoverageVariable("salinity", "float32", [new MultidimensionalCoverageDimension("x", 64)],
                ChunkLayout: null, Units: null, LongName: null, StandardName: null, NoData: -9999)
        ]
    };

    private static async Task<XElement> PostAsync(WebAppFixture fixture, HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        using var content = new StringContent("""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body><GetMultidimensionalInfo xmlns="http://www.esri.com/schemas/ArcGIS/10.8" /></soap:Body>
            </soap:Envelope>
            """, Encoding.UTF8, "text/xml");
        using var response = await fixture.Client.PostAsync($"/services/{WebAppFixture.TestServiceId}/ImageServer", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expectedStatus, body);
        var document = XDocument.Parse(body);
        if (expectedStatus != HttpStatusCode.OK)
        {
            document.Descendants().Where(element => element.Name.LocalName == "Result").Should().BeEmpty(body);
            body.Should().NotContain("temperature").And.NotContain("salinity");
            return document.Descendants().Single(element => element.Name.LocalName == "Fault");
        }
        document.Descendants().Where(element => element.Name.LocalName == "Fault").Should().BeEmpty(body);
        return document.Descendants().Single(element => element.Name.LocalName == "Result");
    }
}
