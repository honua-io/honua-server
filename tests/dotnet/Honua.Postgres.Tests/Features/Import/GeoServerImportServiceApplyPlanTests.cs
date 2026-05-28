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
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;
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
            result.Outcome == "already-applied" &&
            result.HonuaLayerId == 100);
        secondResult.Warnings.Should().Contain(warning => warning.Contains("idempotent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithExistingLayerConflict_LinksLayerToTargetService()
    {
        var fixture = LoadFixture("MixedCatalog");
        var publisher = new RecordingLayerPublishingService();
        publisher.SeedExistingLayer("public", "roads", 240);

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
        result.ResourcesAlreadyApplied.Should().Be(1);
        result.ApplyExecution!.StepResults.Should().Contain(result =>
            result.SourceId == "layer:demo:roads" &&
            result.Outcome == "already-applied" &&
            result.HonuaLayerId == 240);

        publisher.AttachRequests.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                LayerId = 240,
                ServiceName = "demo-geoserver",
                Enabled = true
            });
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithUnexpectedApplyFailure_FailsOverallResultWithEvidence()
    {
        var fixture = LoadFixture("MixedCatalog");
        var publisher = new RecordingLayerPublishingService
        {
            FailNextPublish = true
        };

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

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("failed unexpectedly");
        result.FailedResources.Should().Be(1);
        result.ApplyPlan.Should().NotBeNull();
        result.ApplyExecution.Should().NotBeNull();
        result.ApplyExecution!.Summary.FailedStepCount.Should().Be(1);
        result.ApplyExecution.StepResults.Should().Contain(result =>
            result.SourceId == "layer:demo:roads" &&
            result.Outcome == "failed");
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithApplyModeAndCatalogWriter_AppliesWorkspaceAndLayerGroupCatalogEntries()
    {
        var fixture = LoadFixture("CatalogApplySlice");
        var publisher = new RecordingLayerPublishingService();
        var catalogWriter = new RecordingMigrationCatalogWriter();
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = false,
            ApplyMode = true,
            ImportStyles = false,
            AutoPublishLayers = true,
            RequestTimeoutSeconds = 5
        };

        var result = await CreateService(new FixtureHttpHandler(fixture.Responses), publisher, catalogWriter)
            .ImportConfigurationAsync(request);

        result.Success.Should().BeTrue();
        result.ApplyExecution.Should().NotBeNull();
        var workspaceStep = result.ApplyExecution!.StepResults.SingleOrDefault(step =>
            step.Kind == "workspace" && step.SourceId == "workspace:ops");
        workspaceStep.Should().NotBeNull("workspace catalog entries must be applied by slice 1");
        workspaceStep!.Outcome.Should().Be("applied");
        workspaceStep.TargetServiceName.Should().Be("ops");

        var layerGroupStep = result.ApplyExecution.StepResults.SingleOrDefault(step =>
            step.Kind == "layer-group" && step.SourceId == "layer-group:ops:ops-base");
        layerGroupStep.Should().NotBeNull("layer-group catalog entries must be applied by slice 1");
        layerGroupStep!.Outcome.Should().Be("applied");

        catalogWriter.Requests.Select(r => r.ServiceName)
            .Should().Contain("ops")
            .And.Contain(name => name.Contains("ops-base", StringComparison.Ordinal));
        catalogWriter.Requests.Should().OnlyContain(r => r.Srid == 4326);
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithApplyMode_IsIdempotentOnReApply()
    {
        var fixture = LoadFixture("CatalogApplySlice");
        var publisher = new RecordingLayerPublishingService();
        var catalogWriter = new RecordingMigrationCatalogWriter();
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = false,
            ApplyMode = true,
            AutoPublishLayers = true,
            RequestTimeoutSeconds = 5
        };

        var firstService = CreateService(new FixtureHttpHandler(fixture.Responses), publisher, catalogWriter);
        await firstService.ImportConfigurationAsync(request);

        var secondService = CreateService(new FixtureHttpHandler(fixture.Responses), publisher, catalogWriter);
        var secondResult = await secondService.ImportConfigurationAsync(request);

        secondResult.Success.Should().BeTrue();
        var workspaceStep = secondResult.ApplyExecution!.StepResults.Single(step =>
            step.Kind == "workspace" && step.SourceId == "workspace:ops");
        workspaceStep.Outcome.Should().Be("already-applied");
        var layerGroupStep = secondResult.ApplyExecution.StepResults.Single(step =>
            step.Kind == "layer-group" && step.SourceId == "layer-group:ops:ops-base");
        layerGroupStep.Outcome.Should().Be("already-applied");

        // Idempotency contract: re-applying records the catalog write but reports
        // "already-applied" rather than creating duplicate catalog rows.
        catalogWriter.Requests
            .Count(r => string.Equals(r.ServiceName, "ops", StringComparison.Ordinal))
            .Should().Be(2);
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithApplyModeButNoCatalogWriter_KeepsCatalogStepsAsManualReview()
    {
        var fixture = LoadFixture("CatalogApplySlice");
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = false,
            ApplyMode = true,
            RequestTimeoutSeconds = 5
        };

        var result = await CreateService(new FixtureHttpHandler(fixture.Responses))
            .ImportConfigurationAsync(request);

        result.Success.Should().BeTrue();
        result.ApplyExecution!.StepResults
            .Where(step => step.Kind == "workspace")
            .Should().OnlyContain(step => step.Outcome == "manual-review");
        result.ApplyExecution.StepResults
            .Where(step => step.Kind == "layer-group")
            .Should().OnlyContain(step => step.Outcome == "manual-review");
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithApplyModeAndCancellation_StopsBeforeWritingMoreCatalogEntries()
    {
        var fixture = LoadFixture("CatalogApplySlice");
        var publisher = new RecordingLayerPublishingService();
        var catalogWriter = new RecordingMigrationCatalogWriter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await CreateService(new FixtureHttpHandler(fixture.Responses), publisher, catalogWriter)
            .ImportConfigurationAsync(
                new GeoServerImportRequest
                {
                    GeoServerRestUrl = fixture.ServiceUrl,
                    TargetHonuaUrl = "https://honua.example.test",
                    DryRun = false,
                    ApplyMode = true,
                    AutoPublishLayers = true,
                    RequestTimeoutSeconds = 5
                },
                progress: null,
                cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
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
        ILayerPublishingService? layerPublishingService = null,
        IMigrationCatalogWriter? catalogWriter = null)
    {
        var httpClient = new HttpClient(handler);
        var restClient = new GeoServerRestClient(
            httpClient,
            NullLogger<GeoServerRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Strict);
        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Strict);

        if (layerPublishingService != null || catalogWriter != null)
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
            layerPublishingService: layerPublishingService,
            catalogWriter: catalogWriter);
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
        private readonly Dictionary<string, PublishedLayerSummary> _publishedTargets = new(StringComparer.OrdinalIgnoreCase);

        public List<LayerPublishRequest> Requests { get; } = [];

        public List<LayerAttachRequest> AttachRequests { get; } = [];

        public bool FailNextPublish { get; init; }

        public void SeedExistingLayer(string schema, string table, int layerId)
        {
            _publishedTargets[$"default:{schema}.{table}"] = CreateSummary(layerId, schema, table, "default");
        }

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
            if (FailNextPublish)
            {
                throw new LayerPublishingException(
                    LayerPublishingErrorKind.Unknown,
                    "Publisher failed unexpectedly.");
            }

            var key = $"{request.ServiceName}:{request.Schema}.{request.Table}";
            var existingLayer = _publishedTargets.Values.FirstOrDefault(layer =>
                string.Equals(layer.Schema, request.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(layer.Table, request.Table, StringComparison.OrdinalIgnoreCase));
            if (existingLayer != null)
            {
                throw new LayerPublishingException(
                    LayerPublishingErrorKind.Conflict,
                    $"Layer already exists for table '{request.Schema}.{request.Table}'.",
                    existingLayer.LayerId);
            }

            Requests.Add(request);
            var published = CreateSummary(
                100 + Requests.Count - 1,
                request.Schema,
                request.Table,
                request.ServiceName ?? "default",
                request.LayerName,
                request.Description,
                request.Enabled,
                request.GeometryType,
                request.Srid,
                request.PrimaryKey);
            _publishedTargets[key] = published;
            return Task.FromResult(published);
        }

        public Task<PublishedLayerSummary?> LinkExistingLayerToServiceAsync(
            string connectionString,
            int layerId,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            var existing = _publishedTargets.Values.FirstOrDefault(layer => layer.LayerId == layerId);
            if (existing == null)
            {
                return Task.FromResult<PublishedLayerSummary?>(null);
            }

            AttachRequests.Add(new LayerAttachRequest(layerId, serviceName, enabled));
            var linked = CreateSummary(
                existing.LayerId,
                existing.Schema,
                existing.Table,
                serviceName,
                existing.LayerName,
                existing.Description,
                enabled,
                existing.GeometryType,
                existing.Srid,
                existing.PrimaryKey);
            _publishedTargets[$"{serviceName}:{existing.Schema}.{existing.Table}"] = linked;
            return Task.FromResult<PublishedLayerSummary?>(linked);
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

        private static PublishedLayerSummary CreateSummary(
            int layerId,
            string schema,
            string table,
            string serviceName,
            string? layerName = null,
            string? description = null,
            bool enabled = true,
            string? geometryType = null,
            int? srid = null,
            string? primaryKey = null)
            => new()
            {
                LayerId = layerId,
                LayerName = layerName ?? table,
                Schema = schema,
                Table = table,
                Description = description,
                GeometryType = geometryType ?? "LineString",
                Srid = srid ?? 4326,
                PrimaryKey = primaryKey,
                FieldCount = 3,
                Enabled = enabled,
                ServiceName = serviceName
            };
    }

    private sealed record LayerAttachRequest(int LayerId, string ServiceName, bool Enabled);

    private sealed class RecordingMigrationCatalogWriter : IMigrationCatalogWriter
    {
        private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _existingDataSources = new(StringComparer.OrdinalIgnoreCase);

        public List<MigrationCatalogServiceRequest> Requests { get; } = [];

        public List<MigrationDataSourceRequest> DataSourceRequests { get; } = [];

        public List<MigrationFeatureCopyRequest> FeatureCopyRequests { get; } = [];

        public Task<MigrationCatalogWriteOutcome> EnsureCatalogServiceAsync(
            string connectionString,
            MigrationCatalogServiceRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var outcome = _existing.Add(request.ServiceName)
                ? MigrationCatalogWriteOutcome.Created
                : MigrationCatalogWriteOutcome.AlreadyExists;
            return Task.FromResult(outcome);
        }

        public Task<MigrationCatalogWriteOutcome> EnsureDataSourceAsync(
            string connectionString,
            MigrationDataSourceRequest request,
            CancellationToken cancellationToken = default)
        {
            DataSourceRequests.Add(request);
            var key = $"{request.SourceKind}:{request.SourceId}";
            var outcome = _existingDataSources.Add(key)
                ? MigrationCatalogWriteOutcome.Created
                : MigrationCatalogWriteOutcome.AlreadyExists;
            return Task.FromResult(outcome);
        }

        public Task<MigrationFeatureCopyOutcome> CopyFeatureDataAsync(
            string connectionString,
            MigrationFeatureCopyRequest request,
            CancellationToken cancellationToken = default)
        {
            FeatureCopyRequests.Add(request);
            // Default recording writer reports SourceMissing so slice-1
            // expectations (publish from source table) remain unchanged when the
            // test only needs the catalog-writer recording behavior.
            return Task.FromResult(new MigrationFeatureCopyOutcome
            {
                Status = MigrationFeatureCopyStatus.SourceMissing,
                RowCount = 0
            });
        }

        // Slice 3 (#1015): apply-plan tests do not exercise style persistence
        // directly; record-and-noop so the interface remains satisfied.
        public Task<MigrationCatalogWriteOutcome> EnsureStyleAsync(
            string connectionString,
            MigrationStyleRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(MigrationCatalogWriteOutcome.Created);
    }
}
