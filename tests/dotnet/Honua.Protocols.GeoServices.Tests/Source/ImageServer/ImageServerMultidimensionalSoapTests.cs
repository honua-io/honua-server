// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerMultidimensionalSoapTests
{
    private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly string[] SetNames = ["Variables", "DimensionAttributes", "DimensionValues"];
    private static readonly double[] FixtureTimeDays = [45292, 45293];
    private static readonly double[] FixtureDepths = [-10, -5, 0];

    [UnitTest]
    [InlineData("2024-01-01T00:00:00Z", 45292)]
    [InlineData("1899-12-29T06:00:00Z", -1.25)]
    [InlineData("1899-12-30T06:00:00Z", 0.25)]
    [Operation(Operations.GetServiceInfo)]
    public void TemporalCoordinates_UseOleAutomationDateAndRoundTrip(string isoDate, double expectedDays)
    {
        var date = DateTimeOffset.Parse(isoDate, CultureInfo.InvariantCulture);
        var milliseconds = (double)date.ToUnixTimeMilliseconds();
        var info = new ImageServerMultidimensionalInfo
        {
            Variables = [new ImageServerMultidimensionalVariable
            {
                Name = "temperature", Dimensions = [new ImageServerMultidimensionalDimension
                {
                    Name = "StdTime", DimensionSize = 1, Extent = [milliseconds, milliseconds], Values = [milliseconds]
                }]
            }]
        };
        var sets = AssertShape(ImageServerSoapEndpoints.BuildMultidimensionalResult(info));
        var attributes = Property(Property(sets[1], "temperature"), "StdTime");
        Number(Property(attributes, "Minimum")).Should().Be(expectedDays);
        Number(Property(attributes, "Maximum")).Should().Be(expectedDays);
        foreach (var bounds in Property(Property(sets[2], "temperature"), "StdTime").Elements())
        {
            var coordinate = bounds.Elements().Should().ContainSingle().Subject;
            Number(coordinate).Should().Be(expectedDays);
            DateTime.FromOADate(Number(coordinate)).Ticks.Should().Be(date.UtcDateTime.Ticks);
        }
        info.Variables[0].Dimensions[0].Values![0].Should().Be(milliseconds, "SOAP must not mutate REST/domain coordinates");
    }

    [UnitTest]
    [InlineData(true)]
    [InlineData(false)]
    [Operation(Operations.GetServiceInfo)]
    public void EmptyMetadata_PreservesThreeEmptyTypedPropertySets(bool absent)
    {
        var sets = AssertShape(ImageServerSoapEndpoints.BuildMultidimensionalResult(
            absent ? null : new ImageServerMultidimensionalInfo()));
        foreach (var set in sets) { Child(set, "PropertyArray").Elements().Should().BeEmpty(); }
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public void NumericAndUnknownDimensions_PreserveValuesAndDoNotInventCoordinates()
    {
        var info = new ImageServerMultidimensionalInfo
        {
            Variables =
            [
                new ImageServerMultidimensionalVariable
                {
                    Name = "temperature", Unit = "degC", Description = "Water temperature",
                    Dimensions =
                    [
                        new ImageServerMultidimensionalDimension
                        {
                            Name = "StdZ", Unit = "Meters", DimensionSize = 3,
                            Extent = [-10, 0], Values = FixtureDepths, HasRegularIntervals = true
                        },
                        new ImageServerMultidimensionalDimension { Name = "x", DimensionSize = 64 }
                    ]
                },
                new ImageServerMultidimensionalVariable { Name = "salinity", Dimensions = [] }
            ]
        };
        var sets = AssertShape(ImageServerSoapEndpoints.BuildMultidimensionalResult(info));
        Property(Property(sets[0], "temperature"), "Unit").Value.Should().Be("degC");
        Child(Property(sets[0], "salinity"), "PropertyArray").Elements().Should().BeEmpty();
        var attributes = Property(sets[1], "temperature");
        var depth = Property(attributes, "StdZ");
        Type(Property(depth, "Count")).Should().Be("xsd:int");
        Number(Property(depth, "Count")).Should().Be(3);
        Number(Property(depth, "Minimum")).Should().Be(-10);
        Number(Property(depth, "Maximum")).Should().Be(0);
        Number(Property(depth, "Interval")).Should().Be(5);
        Property(depth, "IntervalUnit").Value.Should().Be("Meters");
        Property(depth, "HasRanges").Value.Should().Be("false");
        var coordinates = Property(sets[2], "temperature");
        foreach (var bounds in Property(coordinates, "StdZ").Elements())
        {
            bounds.Elements().Select(Number).Should().Equal(FixtureDepths);
        }
        var unknown = Property(attributes, "x");
        Child(unknown, "PropertyArray").Elements().Select(property => Child(property, "Key").Value)
            .Should().BeEquivalentTo("Count", "HasRegularIntervals", "HasRanges");
        Child(coordinates, "PropertyArray").Elements().Select(property => Child(property, "Key").Value)
            .Should().Equal("StdZ");
        Child(Property(sets[2], "salinity"), "PropertyArray").Elements().Should().BeEmpty();
    }

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

                    fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, accessPolicy: new AccessPolicy { AllowAnonymous = false });
                    await PostAsync(fixture, HttpStatusCode.Unauthorized);
                    fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, clearAccessPolicy: true);
                    fixture.SetV2LayerEnabled(WebAppFixture.TestLayerId, false);
                    await PostAsync(fixture, HttpStatusCode.NotFound);
                    fixture.SetV2LayerEnabled(WebAppFixture.TestLayerId, true);
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

    private static XElement[] AssertShape(XElement result)
    {
        Type(result).Should().Be("tns:MultidimensionalInfo");
        result.Elements().Select(element => element.Name.LocalName).Should().Equal("Names", "Values");
        var names = Child(result, "Names");
        Type(names).Should().Be("tns:ArrayOfString");
        names.Elements().Select(element => element.Value).Should().Equal(SetNames);
        var values = Child(result, "Values");
        Type(values).Should().Be("tns:ArrayOfAnyType");
        var sets = values.Elements().ToArray();
        sets.Should().HaveCount(3);
        foreach (var set in sets)
        {
            set.Name.LocalName.Should().Be("AnyType");
            Type(set).Should().Be("tns:PropertySet");
            Type(Child(set, "PropertyArray")).Should().Be("tns:ArrayOfPropertySetProperty");
        }
        foreach (var property in result.Descendants().Where(element => element.Name.LocalName == "PropertySetProperty"))
        {
            Type(property).Should().Be("tns:PropertySetProperty");
            property.Elements().Select(element => element.Name.LocalName).Should().Equal("Key", "Value");
        }
        foreach (var value in result.Descendants().Where(element => Type(element) == "tns:ArrayOfArgument"))
        {
            value.Elements().Should().HaveCount(2);
            foreach (var argument in value.Elements())
            {
                argument.Name.LocalName.Should().Be("Argument");
                Type(argument).Should().Be("tns:ArrayOfDouble");
                argument.Elements().Select(element => element.Name.LocalName).Should().OnlyContain(name => name == "Double");
            }
        }
        return sets;
    }

    private static XElement Child(XElement element, string name)
        => element.Elements().Single(child => child.Name.LocalName == name);
    private static XElement Property(XElement set, string key)
        => Child(Child(set, "PropertyArray").Elements().Single(property => Child(property, "Key").Value == key), "Value");
    private static string? Type(XElement element) => element.Attribute(XName.Get("type", Xsi))?.Value;
    private static double Number(XElement element) => double.Parse(element.Value, CultureInfo.InvariantCulture);
}
