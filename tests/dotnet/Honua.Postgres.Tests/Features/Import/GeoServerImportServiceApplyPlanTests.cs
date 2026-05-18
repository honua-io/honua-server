// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class GeoServerImportServiceApplyPlanTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public async Task ImportConfigurationAsync_WithNonDryRun_GeneratesDeterministicApplyPlanEvidence()
    {
        var fixture = LoadFixture("MixedCatalog");
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = false,
            ImportStyles = true,
            RequestTimeoutSeconds = 5
        };
        var firstProgress = new ListProgress<GeoServerImportProgress>();
        var secondProgress = new ListProgress<GeoServerImportProgress>();

        var firstResult = await CreateService(new FixtureHttpHandler(fixture.Responses))
            .ImportConfigurationAsync(request, firstProgress);
        var secondResult = await CreateService(new FixtureHttpHandler(fixture.Responses))
            .ImportConfigurationAsync(request, secondProgress);

        firstResult.Success.Should().BeTrue();
        firstResult.WasDryRun.Should().BeFalse();
        firstResult.WorkspacesImported.Should().Be(0);
        firstResult.LayersImported.Should().Be(0);
        firstResult.ApplyPlan.Should().NotBeNull();
        firstResult.ResourcesPlanned.Should().Be(firstResult.ApplyPlan!.Summary.TotalStepCount);
        firstResult.Warnings.Should().Contain(warning => warning.Contains("catalog mutation", StringComparison.OrdinalIgnoreCase));
        firstProgress.Values.Last().ApplyPlan.Should().NotBeNull();
        firstProgress.Values.Last().CurrentPhase.Should().Be("Apply plan generated");

        JsonSerializer.Serialize(firstResult.ApplyPlan, SerializerOptions)
            .Should().Be(JsonSerializer.Serialize(secondResult.ApplyPlan, SerializerOptions));
        firstResult.ApplyPlan.ReplayToken.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        secondProgress.Values.Last().ApplyPlan!.ReplayToken.Should().Be(firstResult.ApplyPlan.ReplayToken);
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithUnsupportedResources_ClassifiesManualReviewAndUnsupportedWithoutCredentialLeakage()
    {
        var fixture = LoadFixture("MixedCatalog");
        var result = await CreateService(new FixtureHttpHandler(fixture.Responses))
            .ImportConfigurationAsync(new GeoServerImportRequest
            {
                GeoServerRestUrl = fixture.ServiceUrl,
                TargetHonuaUrl = "https://honua.example.test",
                DryRun = false,
                ImportStyles = true,
                RequestTimeoutSeconds = 5
            });

        result.ApplyPlan.Should().NotBeNull();
        var applyPlan = result.ApplyPlan!;
        applyPlan.ManualReviewItems.Should().Contain(item => item.Code == ImportCompatibilityCodes.GeoServerDisabledLayer);
        applyPlan.ManualReviewItems.Should().Contain(item => item.Code == ImportCompatibilityCodes.GeoServerEmptyLayerGroup);
        applyPlan.UnsupportedItems.Should().Contain(item => item.Code == ImportCompatibilityCodes.GeoServerUnsupportedStore);
        applyPlan.UnsupportedItems.Should().Contain(item => item.Code == ImportCompatibilityCodes.GeoServerStyleConversionRequired);
        applyPlan.Steps.Should().Contain(step => step.Disposition == "manual-review");
        applyPlan.Steps.Should().Contain(step => step.Disposition == "unsupported");

        var evidenceJson = JsonSerializer.Serialize(applyPlan, SerializerOptions);
        evidenceJson.Should().NotContain("token=fixture", "credential-bearing style URLs must be normalized before evidence is emitted");
        evidenceJson.Should().NotContain("secret", "credential material must not appear in apply-plan evidence");
        evidenceJson.Should().NotContain("password", "credential fields must not appear in apply-plan evidence");
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

    private sealed class ListProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value)
        {
            Values.Add(value);
        }
    }

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
