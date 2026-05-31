// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
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
using Honua.Core.Features.Styling.Abstractions;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Issue #1015 slice 3: GeoServer style migration persistence and diagnostics.
///
/// Slice 1 (PR #1095) added idempotent catalog persistence of workspace and
/// layer-group entries. Slice 2 (PR #1107) added data-source and feature-data
/// copy. Slice 3 extends that to:
/// 1. Persist a deterministic row in <c>honua.migration_styles</c> for each
///    in-scope style on the manifest, capturing the original SLD body, any
///    converter output, and structured conversion diagnostics.
/// 2. Reuse the registered <see cref="ISldStyleConverter"/> rather than
///    reimplementing conversion logic; warnings/errors are written through to
///    the persisted row so the evidence pack carries explicit manual-review
///    records when visual parity cannot be guaranteed (issue AC).
/// 3. Respect the workspace-scope guard from issue #1098 so a manifest cannot
///    cause cross-workspace style mutations.
/// 4. Re-apply is idempotent on (source_kind, source_id).
/// </summary>
public sealed class GeoServerImportServiceStyleApplyTests
{
    [Fact]
    public async Task ImportConfigurationAsync_WithApplyModeAndCleanSld_PersistsStyleAsApplied()
    {
        var fixture = LoadFixture("StyleApplySlice");
        var catalogWriter = new RecordingMigrationCatalogWriter();
        var converter = new StubSldConverter(layersJson: "[{\"id\":\"roads-line\",\"type\":\"line\"}]");
        var request = NonDryRunRequest(fixture);

        var result = await CreateService(
                new FixtureHttpHandler(fixture.Responses),
                catalogWriter: catalogWriter,
                sldConverter: converter)
            .ImportConfigurationAsync(request);

        result.Success.Should().BeTrue();
        result.ApplyExecution.Should().NotBeNull();

        var lineStyleStep = result.ApplyExecution!.StepResults.Single(step =>
            step.Kind == "style" && step.SourceId == "style:ops:line");
        lineStyleStep.Outcome.Should().Be("applied", "a clean SLD with a registered converter is applied");

        catalogWriter.StyleRequests.Should().Contain(r =>
            r.SourceId == "style:ops:line" &&
            r.SourceFormat == "sld" &&
            r.WorkspaceName == "ops" &&
            r.ReviewDisposition == "applied" &&
            r.ConvertedBody != null &&
            r.ConvertedFormat == "maplibre-layers-json");

        // Diagnostics are persisted as JSON even when empty so operators can
        // query the column without null-handling.
        catalogWriter.StyleRequests
            .Single(r => r.SourceId == "style:ops:line").DiagnosticsJson
            .Should().StartWith("[").And.EndWith("]");
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithApplyModeReApply_IsIdempotentForStyleStep()
    {
        var fixture = LoadFixture("StyleApplySlice");
        var catalogWriter = new RecordingMigrationCatalogWriter();
        var converter = new StubSldConverter(layersJson: "[{\"id\":\"roads-line\",\"type\":\"line\"}]");
        var request = NonDryRunRequest(fixture);

        await CreateService(new FixtureHttpHandler(fixture.Responses), catalogWriter: catalogWriter, sldConverter: converter)
            .ImportConfigurationAsync(request);
        var secondResult = await CreateService(new FixtureHttpHandler(fixture.Responses), catalogWriter: catalogWriter, sldConverter: converter)
            .ImportConfigurationAsync(request);

        secondResult.Success.Should().BeTrue();
        var step = secondResult.ApplyExecution!.StepResults
            .Single(s => s.Kind == "style" && s.SourceId == "style:ops:line");
        step.Outcome.Should().Be("already-applied");

        // Idempotency contract: the writer is still invoked, but the underlying
        // upsert reports "already-applied" rather than creating a duplicate row.
        catalogWriter.StyleRequests
            .Count(r => r.SourceId == "style:ops:line")
            .Should().Be(2);
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithApplyModeAndConverterErrors_PersistsManualReviewWithDiagnostics()
    {
        var fixture = LoadFixture("StyleApplySlice");
        var catalogWriter = new RecordingMigrationCatalogWriter();
        // Converter returns errors so the apply path must NOT claim visual parity.
        var converter = new StubSldConverter(
            layersJson: null,
            warnings: new[] { "[VendorOption] dropped uom" },
            errors: new[] { "SLD document contained no convertible symbolizers." });
        var request = NonDryRunRequest(fixture);

        var result = await CreateService(
                new FixtureHttpHandler(fixture.Responses),
                catalogWriter: catalogWriter,
                sldConverter: converter)
            .ImportConfigurationAsync(request);

        result.Success.Should().BeTrue();
        var step = result.ApplyExecution!.StepResults
            .Single(s => s.Kind == "style" && s.SourceId == "style:ops:line");
        step.Outcome.Should().Be("manual-review", "converter errors block automatic visual parity per issue #1015 AC");
        step.Message.Should().Contain("manual-review");
        step.Message.Should().Contain("Do not claim visual parity");

        var persisted = catalogWriter.StyleRequests.Single(r => r.SourceId == "style:ops:line");
        persisted.ReviewDisposition.Should().Be("manual-review");
        persisted.ConvertedBody.Should().BeNull("the converter produced no MapLibre layers");
        persisted.DiagnosticsJson.Should().Contain("\"severity\":\"error\"");
        persisted.DiagnosticsJson.Should().Contain("convertible symbolizers");
        persisted.DiagnosticsJson.Should().Contain("\"severity\":\"warning\"");
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithApplyModeAndUnsupportedFormat_PersistsManualReviewRecord()
    {
        var fixture = LoadFixture("StyleApplySlice");
        var catalogWriter = new RecordingMigrationCatalogWriter();
        var converter = new StubSldConverter(layersJson: "[]");
        var request = NonDryRunRequest(fixture);

        var result = await CreateService(
                new FixtureHttpHandler(fixture.Responses),
                catalogWriter: catalogWriter,
                sldConverter: converter)
            .ImportConfigurationAsync(request);

        result.Success.Should().BeTrue();
        var cssStyleStep = result.ApplyExecution!.StepResults
            .Single(s => s.Kind == "style" && s.SourceId == "style:ops:deprecated");
        cssStyleStep.Outcome.Should().Be("manual-review");

        var persisted = catalogWriter.StyleRequests.Single(r => r.SourceId == "style:ops:deprecated");
        persisted.SourceFormat.Should().Be("css");
        persisted.ReviewDisposition.Should().Be("manual-review");
        persisted.DiagnosticsJson.Should().Contain("css");
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithApplyModeButNoCatalogWriter_KeepsStyleStepAsManualReview()
    {
        var fixture = LoadFixture("StyleApplySlice");
        var converter = new StubSldConverter(layersJson: "[{\"id\":\"x\"}]");
        var request = NonDryRunRequest(fixture);

        var result = await CreateService(new FixtureHttpHandler(fixture.Responses), sldConverter: converter)
            .ImportConfigurationAsync(request);

        result.Success.Should().BeTrue();
        result.ApplyExecution!.StepResults
            .Where(step => step.Kind == "style")
            .Should().OnlyContain(step => step.Outcome == "manual-review");
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithCrossWorkspaceStyleScope_RejectsOutOfScopeStyles()
    {
        var fixture = LoadFixture("StyleApplySlice");
        var catalogWriter = new RecordingMigrationCatalogWriter();
        var converter = new StubSldConverter(layersJson: "[{\"id\":\"x\"}]");

        // Scope to a workspace that does not contain the fixture's styles
        // (#1098 / PR #1100 cross-workspace guard).
        var request = NonDryRunRequest(fixture) with
        {
            WorkspaceNames = new[] { "unrelated-workspace" }
        };

        var result = await CreateService(
                new FixtureHttpHandler(fixture.Responses),
                catalogWriter: catalogWriter,
                sldConverter: converter)
            .ImportConfigurationAsync(request);

        result.Success.Should().BeTrue();

        // Filter strips workspaces, so the in-scope style set is empty AND the
        // catalog writer is never invoked for cross-workspace styles. The
        // explicit guard inside ApplyStyleCatalogStepAsync defends against the
        // case where a style somehow survives filtering (e.g. a global style
        // included in a layer reference); the test asserts the contract by
        // confirming no out-of-scope styles reached the writer.
        catalogWriter.StyleRequests
            .Should().NotContain(r => r.WorkspaceName == "ops");
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithDryRun_DoesNotApplyStyles()
    {
        var fixture = LoadFixture("StyleApplySlice");
        var catalogWriter = new RecordingMigrationCatalogWriter();
        var converter = new StubSldConverter(layersJson: "[{\"id\":\"x\"}]");
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = true,
            ApplyMode = false,
            ImportStyles = true,
            RequestTimeoutSeconds = 5
        };

        var result = await CreateService(
                new FixtureHttpHandler(fixture.Responses),
                catalogWriter: catalogWriter,
                sldConverter: converter)
            .ImportConfigurationAsync(request);

        result.Success.Should().BeTrue();
        catalogWriter.StyleRequests.Should().BeEmpty(
            "dry-run does not exercise the apply path and must not write style rows");
    }

    private static GeoServerImportRequest NonDryRunRequest(FixtureScenario fixture)
        => new()
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = false,
            ApplyMode = true,
            AutoPublishLayers = true,
            ImportStyles = true,
            RequestTimeoutSeconds = 5
        };

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
        IMigrationCatalogWriter? catalogWriter = null,
        ISldStyleConverter? sldConverter = null)
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
            sldConverter: sldConverter,
            layerPublishingService: layerPublishingService,
            catalogWriter: catalogWriter);
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

    private sealed class StubSldConverter : ISldStyleConverter
    {
        private readonly string? _layersJson;
        private readonly string[] _warnings;
        private readonly string[] _errors;

        public StubSldConverter(string? layersJson, IEnumerable<string>? warnings = null, IEnumerable<string>? errors = null)
        {
            _layersJson = string.IsNullOrEmpty(layersJson) ? null : layersJson;
            _warnings = warnings?.ToArray() ?? Array.Empty<string>();
            _errors = errors?.ToArray() ?? Array.Empty<string>();
        }

        public SldStyleConversionResult Convert(string sldXml)
            => new(
                MapLibreLayersJson: _layersJson,
                DetectedSldVersion: "Sld10",
                Warnings: _warnings,
                Errors: _errors);
    }

    private sealed class RecordingMigrationCatalogWriter : IMigrationCatalogWriter
    {
        private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _existingDataSources = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _existingStyles = new(StringComparer.OrdinalIgnoreCase);

        public List<MigrationCatalogServiceRequest> Requests { get; } = [];

        public List<MigrationDataSourceRequest> DataSourceRequests { get; } = [];

        public List<MigrationFeatureCopyRequest> FeatureCopyRequests { get; } = [];

        public List<MigrationStyleRequest> StyleRequests { get; } = [];

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
            return Task.FromResult(new MigrationFeatureCopyOutcome
            {
                Status = MigrationFeatureCopyStatus.SourceMissing,
                RowCount = 0
            });
        }

        public Task<MigrationCatalogWriteOutcome> EnsureStyleAsync(
            string connectionString,
            MigrationStyleRequest request,
            CancellationToken cancellationToken = default)
        {
            StyleRequests.Add(request);
            var key = $"{request.SourceKind}:{request.SourceId}";
            var outcome = _existingStyles.Add(key)
                ? MigrationCatalogWriteOutcome.Created
                : MigrationCatalogWriteOutcome.AlreadyExists;
            return Task.FromResult(outcome);
        }

        public Task<MigrationRelationshipApplyOutcome[]> EnsureRelationshipsAsync(
            string connectionString,
            Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphStore? graphStore,
            MigrationRelationshipApplyRequest[] requests,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<MigrationRelationshipApplyOutcome>());
    }
}
