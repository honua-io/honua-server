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
/// Verifies the GeoServer migration inventory scanner against deterministic
/// fixture-backed baselines. Baselines are regenerated on demand by setting
/// <c>UPDATE_GEOSERVER_INVENTORY_BASELINES=1</c>.
/// </summary>
public sealed class GeoServerInventoryBaselineTests
{
    private static readonly JsonSerializerOptions IndentedSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    [Theory]
    [InlineData("MixedCatalog")]
    public async Task ScanSourceAsync_ProducesExpectedBaseline(string scenario)
    {
        var fixture = LoadFixture(scenario);
        var service = CreateService(new FixtureHttpHandler(fixture.Responses));

        var artifact = await service.ScanSourceAsync(new GeoServerDiscoveryRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            IncludeCompatibilityAnalysis = true,
            IncludeStyleContent = true,
            TimeoutSeconds = 5
        });

        var actualJson = JsonSerializer.Serialize(artifact, IndentedSerializerOptions);
        AssertMatchesBaseline(scenario, actualJson);
    }

    [Fact]
    public async Task ScanSourceAsync_MixedCatalog_ReportsStableCodesAndEndpointReferences()
    {
        var fixture = LoadFixture("MixedCatalog");
        var service = CreateService(new FixtureHttpHandler(fixture.Responses));

        var artifact = await service.ScanSourceAsync(new GeoServerDiscoveryRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            IncludeCompatibilityAnalysis = true,
            IncludeStyleContent = true,
            TimeoutSeconds = 5
        });

        var roads = artifact.Resources.Should().ContainSingle(resource => resource.Id == "layer:demo:roads").Subject;
        roads.Capabilities.Should().Equal("enabled", "query", "wfs", "wms");
        roads.Compatibility.Code.Should().Be(ImportCompatibilityCodes.GeoServerSupported);

        var disabled = artifact.Resources.Should().ContainSingle(resource => resource.Id == "layer:demo:closed").Subject;
        disabled.Compatibility.Code.Should().Be(ImportCompatibilityCodes.GeoServerDisabledLayer);
        disabled.Capabilities.Should().Equal("wfs", "wms");

        var emptyGroup = artifact.Resources.Should().ContainSingle(resource => resource.Id == "layer-group:demo:empty-group").Subject;
        emptyGroup.Compatibility.Code.Should().Be(ImportCompatibilityCodes.GeoServerEmptyLayerGroup);
        emptyGroup.Capabilities.Should().Equal("wms");

        artifact.ExternalDependencies.Should().ContainSingle(dependency => dependency.Id == "service-endpoint:wms")
            .Which.Should().BeEquivalentTo(new
            {
                Kind = "service-endpoint",
                Name = "WMS",
                DependencyType = "ogc-service",
                Address = "https://example.com/geoserver/wms"
            });
        artifact.ExternalDependencies.Should().ContainSingle(dependency => dependency.Id == "service-endpoint:wfs")
            .Which.Compatibility.Code.Should().Be(ImportCompatibilityCodes.GeoServerServiceEndpoint);

        var containerIds = artifact.Containers.Select(container => container.Id).ToHashSet(StringComparer.Ordinal);
        artifact.ExternalDependencies
            .Where(dependency => dependency.Kind == "service-endpoint")
            .Should().OnlyContain(dependency => containerIds.Contains(dependency.ContainerId));

        artifact.Containers.Should().ContainSingle(container => container.Id == "workspace:global")
            .Which.Compatibility.Reason.Should().Be("Compatible: 2; partial: 0; incompatible: 0.");

        artifact.ExternalDependencies.Should().ContainSingle(dependency => dependency.Id == "datastore:demo:legacy")
            .Which.Compatibility.Code.Should().Be(ImportCompatibilityCodes.GeoServerUnsupportedStore);
        artifact.ExternalDependencies.Should().ContainSingle(dependency => dependency.Id == "coverage-store:demo:imagery")
            .Which.Compatibility.Code.Should().Be(ImportCompatibilityCodes.GeoServerManualReview);

        var style = artifact.Styles.Should().ContainSingle(item => item.Id == "style:demo:line").Subject;
        style.Compatibility.Code.Should().Be(ImportCompatibilityCodes.GeoServerStyleConversionRequired);
        style.Metadata.Should().ContainKey("styleReference")
            .WhoseValue.Should().Be("https://example.com/geoserver/rest/workspaces/demo/styles/line.json");
        style.Metadata.Should().ContainKey("styleContentReference")
            .WhoseValue.Should().Be("https://example.com/geoserver/rest/workspaces/demo/styles/line.sld");
        style.Metadata.Keys.Should().NotContain("sldContent");

        artifact.ExternalDependencies.Should().ContainSingle(dependency => dependency.Kind == "external-graphic")
            .Which.Compatibility.Code.Should().Be(ImportCompatibilityCodes.GeoServerExternalGraphic);
    }

    private static FixtureScenario LoadFixture(string scenario)
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Features",
            "Import",
            "Fixtures",
            "GeoServer",
            $"{scenario}.json");

        using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var root = document.RootElement;
        var serviceUrl = root.GetProperty("serviceUrl").GetString()
            ?? throw new InvalidDataException($"Fixture {scenario} is missing serviceUrl.");
        var responses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("responses").EnumerateObject())
        {
            responses[entry.Name] = entry.Value.ValueKind == JsonValueKind.String
                ? entry.Value.GetString() ?? string.Empty
                : entry.Value.GetRawText();
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
            "GeoServer",
            baselineFile);

        var sourceBaselinePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..",
            "Features",
            "Import",
            "Baselines",
            "GeoServer",
            baselineFile));

        var regenRequested = string.Equals(
            Environment.GetEnvironmentVariable("UPDATE_GEOSERVER_INVENTORY_BASELINES"),
            "1",
            StringComparison.Ordinal);

        if (regenRequested)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourceBaselinePath)!);
            File.WriteAllText(sourceBaselinePath, actualJson);
            Directory.CreateDirectory(Path.GetDirectoryName(outputBaselinePath)!);
            File.WriteAllText(outputBaselinePath, actualJson);
            return;
        }

        File.Exists(sourceBaselinePath).Should().BeTrue(
            $"baseline {baselineFile} must exist at {sourceBaselinePath}. Re-run with UPDATE_GEOSERVER_INVENTORY_BASELINES=1 to regenerate it and commit the result.");

        var expectedJson = File.ReadAllText(sourceBaselinePath);
        actualJson.Should().Be(
            expectedJson,
            $"baseline {baselineFile} should match scanner output. Re-run with UPDATE_GEOSERVER_INVENTORY_BASELINES=1 to refresh after intentional model changes.");
    }

    private static GeoServerImportService CreateService(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var restClient = new GeoServerRestClient(
            httpClient,
            NullLogger<GeoServerRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Strict);
        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Strict);

        crsRegistry.Setup(registry => registry.ResolveBySridAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int srid, CancellationToken _) => new ValueTask<CrsDefinition?>(
                srid switch
                {
                    3857 => new CrsDefinition("http://www.opengis.net/def/crs/EPSG/0/3857", 3857, AxisOrder.EastNorth, false),
                    4326 => new CrsDefinition("http://www.opengis.net/def/crs/EPSG/0/4326", 4326, AxisOrder.EastNorth, true),
                    _ => null
                }));

        return new GeoServerImportService(
            restClient,
            connectionProvider.Object,
            crsRegistry.Object,
            NullLogger<GeoServerImportService>.Instance);
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

            var contentType = pathAndQuery.EndsWith(".xml", StringComparison.Ordinal)
                ? "application/xml"
                : pathAndQuery.EndsWith(".sld", StringComparison.Ordinal)
                    ? "application/vnd.ogc.sld+xml"
                    : "application/json";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
        }
    }
}
