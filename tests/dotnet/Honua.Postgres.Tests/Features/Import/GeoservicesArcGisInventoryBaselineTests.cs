// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Verifies the ArcGIS migration inventory scanner against committed JSON baselines for
/// supported, partial/unsupported, and manual-review scenarios. Fixtures are deterministic
/// embedded JSON files; baselines are regenerated on demand by setting
/// <c>UPDATE_ARCGIS_INVENTORY_BASELINES=1</c>.
/// </summary>
public sealed class GeoservicesArcGisInventoryBaselineTests
{
    private static readonly JsonSerializerOptions IndentedSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    [Theory]
    [InlineData("FeatureServer-Supported")]
    [InlineData("MapServer-MixedRenderers")]
    [InlineData("FeatureServer-AuthRequired")]
    public async Task ScanSourceAsync_ProducesExpectedBaseline(string scenario)
    {
        var fixture = LoadFixture(scenario);
        var service = CreateService(new FixtureHttpHandler(fixture.Responses));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = fixture.ServiceUrl,
            TimeoutSeconds = 5
        });

        var actualJson = JsonSerializer.Serialize(artifact, IndentedSerializerOptions);
        AssertMatchesBaseline(scenario, actualJson);
    }

    [Fact]
    public async Task ScanSourceAsync_FeatureServerSupported_ReportsFieldMetadataAndCodes()
    {
        var fixture = LoadFixture("FeatureServer-Supported");
        var service = CreateService(new FixtureHttpHandler(fixture.Responses));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = fixture.ServiceUrl,
            TimeoutSeconds = 5
        });

        var parcels = artifact.Resources.Should().ContainSingle(r => r.Name == "Parcels").Subject;
        parcels.Fields.Should().HaveCount(4);
        parcels.Fields.Select(f => f.Name).Should().Equal("AREA_SQM", "OBJECTID", "PARCEL_ID", "ZONING");

        var zoning = parcels.Fields.Single(f => f.Name == "ZONING");
        zoning.DomainType.Should().Be("codedValue");
        zoning.DomainName.Should().Be("ZoningCode");
        zoning.DomainValues.Should().NotBeNull();
        zoning.DomainValues!.Select(v => v.Code).Should().Equal("C1", "R1", "R2");

        parcels.Compatibility.Code.Should().Be(ImportCompatibilityCodes.Compatible);
        parcels.Compatibility.Level.Should().Be("compatible");

        var renderer = artifact.Styles.Should().Contain(s => s.Name == "Parcels").Which;
        renderer.Compatibility.Level.Should().Be("partial");
        renderer.Compatibility.Code.Should().Be(ImportCompatibilityCodes.ManualReview);
    }

    [Fact]
    public async Task ScanSourceAsync_MapServerMixedRenderers_ReportsCodesPerLayer()
    {
        var fixture = LoadFixture("MapServer-MixedRenderers");
        var service = CreateService(new FixtureHttpHandler(fixture.Responses));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = fixture.ServiceUrl,
            TimeoutSeconds = 5
        });

        var blockGroups = artifact.Styles.Should().Contain(s => s.Name == "BlockGroups").Which;
        blockGroups.Compatibility.Level.Should().Be("partial");
        blockGroups.Compatibility.Code.Should().Be(ImportCompatibilityCodes.ArcGisExternalSymbol);

        var heatmap = artifact.Styles.Should().Contain(s => s.Name == "PopulationDensity").Which;
        heatmap.Compatibility.Level.Should().Be("incompatible");
        heatmap.Compatibility.Code.Should().Be(ImportCompatibilityCodes.ArcGisUnsupportedRenderer);

        artifact.OverallCompatibility.Level.Should().Be("incompatible");
    }

    [Fact]
    public async Task ScanSourceAsync_FeatureServerAuthRequired_EmitsTokenRequiredCode()
    {
        var fixture = LoadFixture("FeatureServer-AuthRequired");
        var service = CreateService(new FixtureHttpHandler(fixture.Responses));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = fixture.ServiceUrl,
            TimeoutSeconds = 5
        });

        artifact.AuthPosture.Mode.Should().Be("auth-required");
        artifact.AuthPosture.AccessConfirmed.Should().BeFalse();
        artifact.OverallCompatibility.Level.Should().Be("partial");
        artifact.OverallCompatibility.Code.Should().Be(ImportCompatibilityCodes.ArcGisTokenRequired);
        artifact.ScanCompleteness.Status.Should().Be("failed");
        artifact.Resources.Should().BeEmpty();
        artifact.Containers.Should().BeEmpty();
    }

    private static FixtureScenario LoadFixture(string scenario)
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Features",
            "Import",
            "Fixtures",
            "ArcGis",
            $"{scenario}.json");

        using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var root = document.RootElement;
        var serviceUrl = root.GetProperty("serviceUrl").GetString()
            ?? throw new InvalidDataException($"Fixture {scenario} is missing serviceUrl.");
        var responses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("responses").EnumerateObject())
        {
            responses[entry.Name] = entry.Value.GetRawText();
        }
        return new FixtureScenario(serviceUrl, responses);
    }

    private static void AssertMatchesBaseline(string scenario, string actualJson)
    {
        var baselineFile = $"{scenario}-expected.json";
        var outputBaselinePath = Path.Combine(
            AppContext.BaseDirectory,
            "Features",
            "Import",
            "Baselines",
            "ArcGis",
            baselineFile);

        var sourceBaselinePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..",
            "Features",
            "Import",
            "Baselines",
            "ArcGis",
            baselineFile));

        var regenRequested = string.Equals(
            Environment.GetEnvironmentVariable("UPDATE_ARCGIS_INVENTORY_BASELINES"),
            "1",
            StringComparison.Ordinal);

        if (regenRequested || !File.Exists(sourceBaselinePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourceBaselinePath)!);
            File.WriteAllText(sourceBaselinePath, actualJson);
            Directory.CreateDirectory(Path.GetDirectoryName(outputBaselinePath)!);
            File.WriteAllText(outputBaselinePath, actualJson);
            return;
        }

        var expectedJson = File.ReadAllText(sourceBaselinePath);
        actualJson.Should().Be(
            expectedJson,
            $"baseline {baselineFile} should match scanner output. Re-run with UPDATE_ARCGIS_INVENTORY_BASELINES=1 to refresh after intentional model changes.");
    }

    private static GeoservicesImportService CreateService(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var restClient = new ArcGisRestClient(
            httpClient,
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Strict);
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
            NullLogger<GeoservicesImportService>.Instance);
    }

    private sealed record FixtureScenario(string ServiceUrl, IReadOnlyDictionary<string, string> Responses);

    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> _responses;

        public FixtureHttpHandler(IReadOnlyDictionary<string, string> responses)
        {
            _responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (!_responses.TryGetValue(pathAndQuery, out var body))
            {
                throw new InvalidOperationException(
                    $"Fixture has no response for {pathAndQuery}. Add it to the fixture JSON or correct the request path.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
