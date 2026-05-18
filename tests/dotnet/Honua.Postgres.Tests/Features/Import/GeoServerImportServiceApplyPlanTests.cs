// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
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
    public async Task ImportConfigurationAsync_WithNonDryRun_GeneratesDeterministicApplyPlanAndExecutionEvidence()
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
        firstResult.ApplyExecution.Should().NotBeNull();
        firstResult.ResourcesPlanned.Should().Be(firstResult.ApplyPlan!.Summary.TotalStepCount);
        firstResult.ResourcesManualReview.Should().Be(firstResult.ApplyExecution!.Summary.ManualReviewStepCount);
        firstResult.Warnings.Should().Contain(warning => warning.Contains("catalog mutation", StringComparison.OrdinalIgnoreCase));
        firstProgress.Values.Last().ApplyPlan.Should().NotBeNull();
        firstProgress.Values.Last().ApplyExecution.Should().NotBeNull();
        firstProgress.Values.Last().CurrentPhase.Should().Be("Apply plan executed");

        JsonSerializer.Serialize(firstResult.ApplyPlan, SerializerOptions)
            .Should().Be(JsonSerializer.Serialize(secondResult.ApplyPlan, SerializerOptions));
        firstResult.ApplyPlan.ReplayToken.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        secondProgress.Values.Last().ApplyPlan!.ReplayToken.Should().Be(firstResult.ApplyPlan.ReplayToken);
        secondProgress.Values.Last().ApplyExecution!.PlanFingerprint.Should().Be(firstResult.ApplyPlan.PlanFingerprint);
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithPostGisLayerAndPublisher_AppliesCatalogLayer()
    {
        var fixture = LoadFixture("MixedCatalog");
        var publisher = new RecordingLayerPublishingService();

        var result = await CreateService(new FixtureHttpHandler(fixture.Responses), publisher)
            .ImportConfigurationAsync(new GeoServerImportRequest
            {
                GeoServerRestUrl = fixture.ServiceUrl,
                TargetHonuaUrl = "https://honua.example.test",
                DryRun = false,
                ImportStyles = true,
                AutoPublishLayers = true,
                RequestTimeoutSeconds = 5
            });

        result.Success.Should().BeTrue();
        result.ApplyExecution.Should().NotBeNull();
        result.ResourcesApplied.Should().Be(1);
        result.ApplyExecution!.Summary.AppliedStepCount.Should().Be(1);
        result.ApplyExecution.StepResults.Should().Contain(result =>
            result.Outcome == "applied" &&
            result.SourceId == "layer:demo:roads" &&
            result.HonuaLayerId == 100);

        publisher.Requests.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Schema = "public",
                Table = "roads",
                LayerName = "roads",
                ServiceName = "demo-geoserver",
                Enabled = true
            });
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithAlreadyPublishedLayer_RecordsIdempotentReplay()
    {
        var fixture = LoadFixture("MixedCatalog");
        var publisher = new RecordingLayerPublishingService();
        var service = CreateService(new FixtureHttpHandler(fixture.Responses), publisher);
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = false,
            ImportStyles = true,
            RequestTimeoutSeconds = 5
        };

        var firstResult = await service.ImportConfigurationAsync(request);
        var secondResult = await service.ImportConfigurationAsync(request);

        firstResult.ResourcesApplied.Should().Be(1);
        secondResult.Success.Should().BeTrue();
        secondResult.ResourcesAlreadyApplied.Should().Be(1);
        secondResult.ApplyExecution!.StepResults.Should().Contain(result =>
            result.SourceId == "layer:demo:roads" &&
            result.Outcome == "already-applied");
        secondResult.Warnings.Should().Contain(warning => warning.Contains("idempotent", StringComparison.OrdinalIgnoreCase));
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

    private static GeoServerImportService CreateService(
        HttpMessageHandler handler,
        ILayerPublishingService? layerPublishingService = null)
    {
        var httpClient = new HttpClient(handler);
        var restClient = new GeoServerRestClient(
            httpClient,
            NullLogger<GeoServerRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Strict);
        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Strict);

        if (layerPublishingService != null)
        {
            connectionProvider.Setup(provider => provider.GetConnectionString())
                .Returns("Host=localhost;Database=honua");
        }

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
            NullLogger<GeoServerImportService>.Instance,
            layerPublishingService: layerPublishingService);
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

    private sealed class RecordingLayerPublishingService : ILayerPublishingService
    {
        private readonly HashSet<string> _publishedTargets = new(StringComparer.OrdinalIgnoreCase);

        public List<LayerPublishRequest> Requests { get; } = [];

        public Task<IReadOnlyList<PublishedLayerSummary>> ListPublishedLayersAsync(
            string connectionString,
            string serviceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PublishedLayerSummary>>([]);

        public Task<PublishedLayerSummary> PublishLayerAsync(
            string connectionString,
            LayerPublishRequest request,
            CancellationToken cancellationToken = default)
        {
            var key = $"{request.ServiceName}:{request.Schema}.{request.Table}";
            if (!_publishedTargets.Add(key))
            {
                throw new LayerPublishingException(
                    LayerPublishingErrorKind.Conflict,
                    $"Layer already exists for table '{request.Schema}.{request.Table}'.");
            }

            Requests.Add(request);
            return Task.FromResult(new PublishedLayerSummary
            {
                LayerId = 100 + Requests.Count - 1,
                LayerName = request.LayerName,
                Schema = request.Schema,
                Table = request.Table,
                Description = request.Description,
                GeometryType = request.GeometryType ?? "LineString",
                Srid = request.Srid ?? 4326,
                PrimaryKey = request.PrimaryKey,
                FieldCount = 3,
                Enabled = request.Enabled,
                ServiceName = request.ServiceName ?? "default"
            });
        }

        public Task<TablePublishValidationResult> ValidateTableForPublishAsync(
            string connectionString,
            TablePublishValidationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TablePublishValidationResult
            {
                IsValid = true,
                Status = "valid",
                Schema = request.Schema,
                Table = request.Table,
                ServiceName = request.ServiceName ?? "default"
            });

        public Task<PublishedLayerSummary?> SetLayerEnabledAsync(
            string connectionString,
            int layerId,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<PublishedLayerSummary?>(null);

        public Task<IReadOnlyList<PublishedLayerSummary>> SetServiceLayersEnabledAsync(
            string connectionString,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PublishedLayerSummary>>([]);

        public Task<LayerExtentRefreshResult?> RefreshLayerExtentsAsync(
            string connectionString,
            string serviceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult<LayerExtentRefreshResult?>(null);
    }
}
